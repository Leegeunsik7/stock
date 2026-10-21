// ============================================================================
// MarketDataStreamGovernanceTests — [시세 하드닝 · 추천 A] 시세 스트림 파싱/발행 · 이상 시세 인터락 · 조각 재조립 · 100ms 스냅샷 렌더 거버너
//
//   수정 전 실측 대상:
//     · TossWebSocketMarketDataGateway — 최신 틱 필드별 개별 Exchange(찢긴 판독) · 역순/중복 seq 덮어쓰기 · 조각 프레임 개별 파싱 · 가격 가드 0
//     · StateAwareRenderGovernor.ShouldRender — 후행 에지 부재(창 안 거부된 최종 상태 유실) · Normal 부하 무제한 렌더
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Infrastructure;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    internal sealed class SmManualTimeProvider : TimeProvider
    {
        public readonly List<SmManualTimer> Timers = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var t = new SmManualTimer(callback, state, dueTime); lock (Timers) Timers.Add(t); return t; }

        internal sealed class SmManualTimer : ITimer
        {
            private readonly TimerCallback _cb; private readonly object? _state;
            public TimeSpan Due { get; private set; }
            public bool Disposed { get; private set; }
            public SmManualTimer(TimerCallback cb, object? s, TimeSpan due) { _cb = cb; _state = s; Due = due; }
            public bool Change(TimeSpan due, TimeSpan period) { if (Disposed) throw new ObjectDisposedException(nameof(SmManualTimer)); Due = due; return true; }
            public bool Armed => !Disposed && Due != Timeout.InfiniteTimeSpan;
            public bool Fire() { if (!Armed) return false; Due = Timeout.InfiniteTimeSpan; _cb(_state); return true; }
            public void Dispose() => Disposed = true;
            public System.Threading.Tasks.ValueTask DisposeAsync() { Disposed = true; return default; }
        }
    }

    [Collection("ConcurrencyStress")]
    public sealed class MarketDataStreamGovernanceTests
    {
        private static byte[] Pkt(long seq, string sym, long price, long qty, long ts = 0)
            => Encoding.UTF8.GetBytes($"{{\"seq\":{seq},\"symbol\":\"{sym}\",\"price\":{price},\"qty\":{qty},\"ts\":{ts}}}");

        private static long H(string sym) => TossWebSocketMarketDataGateway.FnvSpan(Encoding.UTF8.GetBytes(sym));

        private static TossWebSocketMarketDataGateway Gw(MarketDataSpikeInterlock? spike = null)
            => new(new SequenceContinuityGuardian(), spikeGuard: spike);

        // ═══ §1 파싱 · 발행 정합성 ═══

        [Fact]
        public void Latest_Tick_Snapshot_Is_Never_Torn_Across_Symbols()
        {
            // A 종목은 가격 10000+(seq%50)·수량=seq, B 종목은 가격 50000+(seq%50)·수량=seq*2 — 판독 1건이 두 종목을 섞으면 즉시 탐지.
            var gw = Gw();
            long ha = H("005930"), hb = H("035720");
            var packets = Enumerable.Range(1, 20_000).Select(i => i % 2 == 0 ? Pkt(i, "005930", 10_000 + i % 50, i) : Pkt(i, "035720", 50_000 + i % 50, i * 2L)).ToArray();
            using var stop = new ManualResetEventSlim(false);
            int torn = 0, reads = 0;
            var reader = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    if (!gw.TryReadLatest(out var t)) continue;
                    reads++;
                    bool ok = t.SymbolHash == ha ? t.PriceRaw == 10_000 + t.Seq % 50 && t.Qty == t.Seq
                            : t.SymbolHash == hb && t.PriceRaw == 50_000 + t.Seq % 50 && t.Qty == t.Seq * 2;
                    if (!ok) Interlocked.Increment(ref torn);
                }
            });
            reader.Start();
            foreach (var p in packets) gw.IngestPacket(p, 0);
            stop.Set(); reader.Join();
            Assert.Equal(0, torn);                         // 수정 전 구조(필드별 Exchange)는 심볼·가격 혼합 판독 가능
            Assert.True(reads > 100, $"reads={reads}");
            Assert.Equal(20_000, gw.TicksIngested);
        }

        [Fact]
        public void Out_Of_Order_Or_Duplicate_Sequence_Never_Rewinds_Latest()
        {
            var gw = Gw();
            Assert.True(gw.IngestPacket(Pkt(10, "005930", 70_000, 5), 0));
            Assert.False(gw.IngestPacket(Pkt(9, "005930", 60_000, 5), 0));    // 재연결 리플레이(과거)
            Assert.False(gw.IngestPacket(Pkt(10, "005930", 60_000, 5), 0));   // 중복
            Assert.True(gw.TryReadLatest(out var t));
            Assert.Equal(70_000, t.PriceRaw);                                  // 수정 전: 60,000 으로 되돌아감
            Assert.Equal(2, gw.OutOfOrderDropped);
            Assert.True(gw.IngestPacket(Pkt(11, "005930", 70_100, 5), 0));
        }

        [Theory]
        [InlineData("{\"seq\":1,\"symbol\":\"005930\",\"price\":0,\"qty\":1}")]
        [InlineData("{\"seq\":1,\"symbol\":\"005930\",\"price\":-500,\"qty\":1}")]
        [InlineData("{\"symbol\":\"005930\",\"price\":1000}")]
        [InlineData("{\"seq\":1,\"symbol\":\"005930\",\"price\":10")]
        public void Invalid_Packets_Are_Rejected_Without_Publishing(string json)
        {
            var gw = Gw();
            Assert.False(gw.IngestPacket(Encoding.UTF8.GetBytes(json), 0));
            Assert.False(gw.TryReadLatest(out _));
            Assert.Equal(1, gw.ParseErrors);
        }

        // ═══ §1 조각 프레임 재조립 ═══

        [Fact]
        public void Fragmented_WebSocket_Message_Is_Reassembled_Before_Parsing()
        {
            var gw = Gw();
            var asm = new WsMessageAssembler(4096);
            byte[] full = Pkt(1, "005930", 71_000, 3);
            int cut = full.Length / 3;
            Assert.False(asm.Append(full.AsSpan(0, cut), endOfMessage: false));          // 수정 전: 이 조각을 곧바로 파싱 → 파싱 오류 · 틱 유실
            Assert.False(asm.Append(full.AsSpan(cut, cut), endOfMessage: false));
            Assert.True(asm.Append(full.AsSpan(2 * cut), endOfMessage: true));
            Assert.True(gw.IngestPacket(asm.Message, 0));
            Assert.Equal(0, gw.ParseErrors);
            Assert.True(asm.Append(Pkt(2, "005930", 71_010, 3), endOfMessage: true));   // 다음 메시지는 새로 시작
            Assert.True(gw.IngestPacket(asm.Message, 0));
        }

        [Fact]
        public void Oversized_Message_Is_Dropped_Whole_And_Next_Message_Recovers()
        {
            var asm = new WsMessageAssembler(64);
            Assert.False(asm.Append(new byte[50], false));
            Assert.False(asm.Append(new byte[50], true));                                // 64B 초과 → 메시지 전체 폐기
            Assert.Equal(1, asm.OversizedDropped);
            Assert.True(asm.Append(Encoding.UTF8.GetBytes("{\"seq\":1}"), true));
            Assert.Equal("{\"seq\":1}", Encoding.UTF8.GetString(asm.Message));
        }

        // ═══ §3 방향 B — 이상 시세 인터락 ═══

        [Fact]
        public void Tick_Jump_Spike_Quarantines_Line_Blocks_Arming_And_Other_Symbols_Continue()
        {
            int trips = 0;
            var spike = new MarketDataSpikeInterlock(onTrip: _ => trips++);
            var gw = Gw(spike);
            Assert.True(gw.IngestPacket(Pkt(1, "005930", 70_000, 1), 0));
            Assert.True(gw.IngestPacket(Pkt(2, "035720", 50_000, 1), 0));
            Assert.False(gw.IngestPacket(Pkt(3, "005930", 7_000, 1), 0));   // 자릿수 오류(-90%) 틱
            Assert.True(spike.ArmingBlocked);
            Assert.True(spike.IsQuarantined(H("005930")));
            Assert.False(gw.IngestPacket(Pkt(4, "005930", 70_100, 1), 0));  // 격리 라인 — 정상 가격이어도 무시
            Assert.True(gw.IngestPacket(Pkt(5, "035720", 50_100, 1), 0));   // 다른 종목은 계속
            Assert.True(gw.TryReadLatest(out var t));
            Assert.Equal(H("035720"), t.SymbolHash);
            Assert.Equal(1, trips);
            Assert.Equal(2, gw.SpikeRejected);
        }

        [Fact]
        public void Price_Band_Breach_Detected_Only_With_Injected_Reference()
        {
            var spike = new MarketDataSpikeInterlock();
            long h = H("000660");
            Assert.Equal(SpikeVerdict.Accepted, spike.Observe(h, 130_000));      // 기준가 없음 — 틱 점프만 적용
            var s2 = new MarketDataSpikeInterlock();
            s2.SetReference(h, 100_000);                                         // 전일 종가
            Assert.Equal(SpikeVerdict.Accepted, s2.Observe(h, 129_000));         // +29%
            Assert.Equal(SpikeVerdict.OutOfPriceBand, new MarketDataSpikeInterlock().Let(x => { x.SetReference(h, 100_000); return x.Observe(h, 131_000); }));
        }

        [Fact]
        public void Gradual_Legit_Moves_Within_Jump_Limit_Are_Accepted()
        {
            var spike = new MarketDataSpikeInterlock();
            long h = H("005930"), px = 70_000;
            for (int i = 0; i < 200; i++) { px += px / 200; Assert.Equal(SpikeVerdict.Accepted, spike.Observe(h, px)); }   // 틱당 0.5% 상승 누적
            Assert.False(spike.Tripped);
        }

        [Fact]
        public void Spike_Rearm_Requires_Both_Physical_Keys_And_Clears_Quarantine()
        {
            var spike = new MarketDataSpikeInterlock();
            long h = H("005930");
            spike.Observe(h, 70_000); spike.Observe(h, 1);
            Assert.False(spike.TryRearm(true, false));
            Assert.True(spike.ArmingBlocked);
            Assert.True(spike.TryRearm(true, true));
            Assert.False(spike.ArmingBlocked);
            Assert.False(spike.IsQuarantined(h));
            Assert.Equal(SpikeVerdict.Accepted, spike.Observe(h, 70_000));
        }

        // ═══ δ=0 ═══

        [Fact]
        public void Ingest_Read_Spike_And_Assembler_Hot_Paths_Are_Zero_Alloc()
        {
            var gw = Gw(new MarketDataSpikeInterlock());
            var asm = new WsMessageAssembler(4096);
            var packets = Enumerable.Range(1, 3_000).Select(i => Pkt(i, i % 2 == 0 ? "005930" : "035720", 70_000 + i % 30, i, i)).ToArray();
            int k = 0;
            void Cycle()
            {
                for (int i = 0; i < 1_000; i++)
                {
                    var p = packets[k++];
                    int half = p.Length / 2;
                    asm.Append(p.AsSpan(0, half), false);
                    if (asm.Append(p.AsSpan(half), true)) gw.IngestPacket(asm.Message, i);
                    gw.TryReadLatest(out _);
                }
            }
            Cycle();
            Assert.Equal(0, MinAllocated(Cycle));
            Assert.Equal(0, gw.ParseErrors);
        }

        // ═══ §2 스냅샷 렌더 거버너 ═══

        [Fact]
        public void Governor_Renders_Leading_Coalesces_Burst_And_Always_Renders_Final_State()
        {
            var time = new SmManualTimeProvider();
            int renders = 0;
            using var g = new SnapshotRenderGovernor(() => renders++, time: time);
            g.Signal(1);
            Assert.Equal(1, renders);                               // 유휴 → 즉시
            for (long v = 2; v <= 500; v++) g.Signal(v);            // 100ms 창 안 시세 연타
            Assert.Equal(1, renders);
            Assert.True(time.Timers[0].Fire());                     // 창 종료 → 후행 1회(마지막 버전 500 반영)
            Assert.Equal(2, renders);                               // 수정 전 ShouldRender 는 이 최종 상태를 다음 이벤트까지 유실
            Assert.True(time.Timers[0].Fire());
            Assert.Equal(2, renders);
            Assert.False(time.Timers[0].Armed);                     // 유휴 복귀
        }

        [Fact]
        public void Unchanged_Snapshot_Version_Suppresses_Render()
        {
            var time = new SmManualTimeProvider();
            int renders = 0;
            using var g = new SnapshotRenderGovernor(() => renders++, time: time);
            g.Signal(7); time.Timers[0].Fire(); time.Timers[0].Fire();
            g.Signal(7);                                            // 값 미변동 신호
            Assert.Equal(1, renders);
            Assert.Equal(1, g.SuppressedUnchanged);
        }

        [Fact]
        public void Frozen_Load_Stretches_Window_To_250ms()
        {
            var time = new SmManualTimeProvider();
            var load = new StateAwareRenderGovernor();
            load.SetFrozen(true);
            using var g = new SnapshotRenderGovernor(() => { }, loadGovernor: load, time: time);
            g.Signal(1);
            Assert.Equal(TimeSpan.FromMilliseconds(250), time.Timers[0].Due);
            load.SetFrozen(false);
            time.Timers[0].Fire();
            g.Signal(2);
            Assert.Equal(TimeSpan.FromMilliseconds(100), time.Timers[0].Due);
        }

        [Fact]
        public void Governor_Dispose_And_Render_Fault_Are_Fail_Safe()
        {
            var time = new SmManualTimeProvider();
            var g = new SnapshotRenderGovernor(() => throw new ObjectDisposedException("renderer"), time: time);
            g.Signal(1); g.Signal(2); time.Timers[0].Fire();
            Assert.Equal(2, g.RenderFaults);                        // 예외가 신호/타이머 스레드로 전파되지 않음
            g.Dispose();
            g.Signal(3);
            Assert.Equal(2, g.Renders);
            Assert.True(time.Timers[0].Disposed);
        }

        [Fact]
        public void Governor_Signal_Is_Zero_Alloc_Under_Tick_Storm()
        {
            var time = new SmManualTimeProvider();
            using var g = new SnapshotRenderGovernor(static () => { }, time: time);
            long v = 0;
            for (int i = 0; i < 1000; i++) g.Signal(++v);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 100_000; i++) g.Signal(++v); }));
        }

        [Fact]
        public void Number_Text_Is_Cached_And_Zero_Alloc_When_Unchanged()
        {
            var t = new ZeroAllocNumberText();
            Assert.Same(t.For(512), new ZeroAllocNumberText().For(512));   // 0~1023 정적 캐시
            string big = t.For(72_500);
            Assert.Same(big, t.For(72_500));                                 // 미변동 → 같은 인스턴스
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) { t.For(i % 1024); } }));
            Assert.NotSame(big, t.For(72_600));
            Assert.Equal("72600", t.For(72_600));
        }

        // ═══ 결속 확인(IL) ═══

        private static bool Calls(Type owner, string methodName, MethodBase target, bool searchNested)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = owner.GetMethods(All).Where(m => methodName == "*" || m.Name == methodName).Cast<MethodBase>().ToList();
            if (searchNested) foreach (var n in owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)) methods.AddRange(n.GetMethods(All));
            foreach (var m in methods)
            {
                var il = m.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (int k = 0; k + 4 < il.Length; k++)
                    if (il[k] is 0x28 or 0x6F)
                        try { if (m.Module.ResolveMethod(BitConverter.ToInt32(il, k + 1)) == target) return true; } catch (ArgumentException) { }
            }
            return false;
        }

        [Fact]
        public void Dashboard_Index_Refresh_Uses_Snapshot_Governor_Not_Leading_Only_Gate()
        {
            var dash = typeof(MS2E.UI.Portable.Pages.Shell.Dashboard);
            Assert.True(Calls(dash, "OnIdxRefreshed", typeof(SnapshotRenderGovernor).GetMethod(nameof(SnapshotRenderGovernor.Signal))!, false));
            Assert.False(Calls(dash, "OnIdxRefreshed", typeof(StateAwareRenderGovernor).GetMethod(nameof(StateAwareRenderGovernor.ShouldRender))!, false));
        }

        [Fact]
        public void Host_Arming_Predicate_Reads_Market_Spike_Interlock()
            => Assert.True(Calls(typeof(StockManager.Blazor.BlazorHomeForm), "*",
                typeof(MarketDataSpikeInterlock).GetProperty(nameof(MarketDataSpikeInterlock.ArmingBlocked))!.GetMethod!, true));
    }

    internal static class LetExtension
    {
        public static TR Let<T, TR>(this T value, Func<T, TR> f) => f(value);
    }
}

// ============================================================================
// ChaosStormInterlockStressTests — [카오스 스톰 · 추천 A]
//
//  §1 [실결함] 블랙박스 사건 코드 충돌 — InterlockRaised/Cleared/EgressBypassBlocked 가 유동성 슬라이서 코드와 같았다(포렌식 판독 불가).
//  §2 [실결함] 최종 송출 초크포인트 TOCTOU — 드레인은 카나리·라우터·지연 프로브 '앞'에서 집계어를 1회 읽는다. 그 뒤 격발한 비트를
//     LiveArmedEgressSentinel 이 송출 직전 재판독으로 veto(무장 상태여도 전달 0) · 판독 예외 = 차단 · 우회 sticky 와 구분.
//  §3 28계층(비트 0~26 + 63) — 단일 격발 전수 · 무작위 복합 500조합 · 28스레드 동시 격발/해제(전이 비트당 정확히 1회) · δ=0.
//  §4 재무장 — 물리 이중키 없이는 전 소스 거부(소스 전수 정적 스캔 + 대표 소스 실측) · 미체결 대사 미완료면 키가 있어도 차단 유지.
//  §5 카오스 주입 9벡터 — 실 Trip 경로 · 반환 전 비트 격발(0ms 원자 Disarm) · 주입 후 오발주 0 · 블랙박스 주입→격발 순서 상호 검증 ·
//     실 파이프 프록시 강제 종료 → 링크 비트 · 무작위 스톰(동시 시세/발주 부하) 중 '차단 진입 주문 전달 0'.
//  §6 [방향 B] 연쇄 격발 동결 — 창 내 3비트 = 블랙박스 봉인(최초 원인 보존) + 동결 1회 · 창 밖 = 무트립 · 기동 각인 무시.
//  §7 [방향 A] 비행 기록계 초당 10만 건+ · 4스레드 동시 기록 · 할당 0 · 카오스 주입 δ=0.
//  §8 호스트 결선 · 오중 빌드.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ChaosStormInterlockStressTests
    {
        private readonly ITestOutputHelper _out;
        public ChaosStormInterlockStressTests(ITestOutputHelper o) => _out = o;

        private static readonly long[] Codes = { 5930, 660, 35420 };
        private const long Px = 70_000, Tick = 100;

        private static OrderBookPayload Book(long code)
        {
            var b = new OrderBookPayload { SymbolCode = code, TimestampTicks = 1 };
            for (int i = 0; i < 10; i++) { b.AskPrice[i] = Px + Tick * (i + 1); b.AskQty[i] = 1_000; b.BidPrice[i] = Px - Tick * i; b.BidQty[i] = 1_000; }
            return b;
        }

        private static void Feed(PaperTradingSimulationHarness h)
        {
            foreach (var c in Codes) { h.OnTossTick(c, Px); var b = Book(c); h.OnKiwoomBook(in b); }
        }

        private sealed class Src : IArmingBlockSource { public volatile bool Blocked; public bool ArmingBlocked => Blocked; }

        private sealed class CountingBroker : IBroker
        {
            public long Placed;
            public string Name => "count";
            public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            { Interlocked.Increment(ref Placed); return (true, "1"); }
            public void Cancel(string orderId) { }
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        /// <summary>송출 진입 시점 집계어 판독 → 그 상태에서 하류로 전달됐는지(차단 진입 주문 전달 = 위반).</summary>
        private sealed class EntryProbe : IBroker
        {
            private readonly IBroker _inner; private readonly Func<bool> _blocked;
            public long BlockedAtEntry, ForwardedWhileBlockedAtEntry, Calls;
            public EntryProbe(IBroker inner, Func<bool> blocked) { _inner = inner; _blocked = blocked; }
            public string Name => _inner.Name;
            public bool IsLive => _inner.IsLive;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            {
                Interlocked.Increment(ref Calls);
                bool blocked = _blocked();
                if (blocked) Interlocked.Increment(ref BlockedAtEntry);
                var r = _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
                if (blocked && r.ok) Interlocked.Increment(ref ForwardedWhileBlockedAtEntry);
                return r;
            }
            public void Cancel(string orderId) => _inner.Cancel(orderId);
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        // ════════════ §1 사건 코드 유일성 ════════════

        [Fact]
        public void FlightEventKinds_AreUnique_InterlockKindsNoLongerAliasLiquidity()
        {
            var values = Enum.GetValues<FlightEventKind>().Select(v => (int)v).ToArray();
            var dup = values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.True(dup.Length == 0, "중복 사건 코드: " + string.Join(",", dup));                  // ★구 70/71/72 충돌
            Assert.NotEqual((int)FlightEventKind.LiquiditySlice, (int)FlightEventKind.InterlockRaised);
            Assert.Equal("InterlockRaised", FlightEventKind.InterlockRaised.ToString());
            Assert.Equal("EgressBypassBlocked", ((FlightEventKind)(byte)FlightEventKind.EgressBypassBlocked).ToString());
        }

        // ════════════ §2 송출 초크포인트 TOCTOU ════════════

        [Fact]
        public void EgressSentinel_VetoesWhenInterlockRaisedAfterUpstreamCheck_EvenIfArmed()
        {
            var mask = new ArmingInterlockBitmask();
            var src = new Src();
            var link = mask.Bind(ArmingInterlockSource.KisProtocolDivergence, src);
            Assert.True(mask.Seal(out _));
            var inner = new CountingBroker();
            // 무장 술어 평가 순간에 비트가 격발(드레인 판독 이후 · Place 직전 경합 재현)
            bool raceOnce = false;
            var s = new LiveArmedEgressSentinel(inner, () => { if (raceOnce) { src.Blocked = true; link.Resync(); } return true; },
                                                interlockBlocked: () => mask.Blocked);
            Assert.True(s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000).ok);
            raceOnce = true;
            var r = s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000);
            Assert.False(r.ok);                                                                  // ★무장인데도 전달 0
            Assert.Equal((1L, 1L, 0L), (inner.Placed, s.InterlockVetoes, s.BypassAttempts));     // 우회(sticky)와 구분
            Assert.False(s.Tripped);
            src.Blocked = false; link.Resync(); raceOnce = false;
            Assert.True(s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000).ok);   // 해제 = 재개(비 sticky)
            var throwing = new LiveArmedEgressSentinel(inner, () => true, interlockBlocked: () => throw new InvalidOperationException());
            Assert.False(throwing.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000).ok);   // 판독 예외 = 차단
            Assert.Equal(2, inner.Placed);
        }

        // ════════════ §3 29계층(v12.17.6 비트 28 OrderPathLatency 추가) ════════════

        private static readonly ArmingInterlockSource[] AllSources = Enumerable.Range(0, 29).Select(i => (ArmingInterlockSource)i).ToArray();

        private static (ArmingInterlockBitmask Mask, Src[] Srcs, ArmingInterlockLink[] Links, InterlockForensicRecorder Rec) Full()
        {
            var mask = new ArmingInterlockBitmask(AllSources);
            var rec = new InterlockForensicRecorder(new ZeroAllocForensicAuditStream(8192));
            mask.AttachTransitionSink(rec);
            var srcs = AllSources.Select(_ => new Src()).ToArray();
            var links = AllSources.Select((b, i) => mask.Bind(b, srcs[i])).ToArray();
            return (mask, srcs, links, rec);
        }

        [Fact]
        public void TwentyNineLayers_SingleAndRandomCombos_BlockAndVetoEgress()
        {
            Assert.Equal(29, Enum.GetValues<ArmingInterlockSource>().Count(s => (int)s < 63));    // 0~28 + 63
            var (mask, srcs, links, _) = Full();
            var inner = new CountingBroker();
            var sentinel = new LiveArmedEgressSentinel(inner, () => true, interlockBlocked: () => mask.Blocked);
            Assert.True(mask.Blocked);                                                           // ★비트 63(기동 샌니티) — 봉인 전
            Assert.False(sentinel.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 1, 1).ok);
            Assert.True(mask.Seal(out _));
            Assert.False(mask.Blocked);
            for (int i = 0; i < srcs.Length; i++)
            {
                srcs[i].Blocked = true; links[i].Resync();
                Assert.True(mask.Blocked && mask.IsRaised(AllSources[i]), "비트 " + i);
                Assert.False(sentinel.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 1, 1).ok);
                srcs[i].Blocked = false; links[i].Resync();
                Assert.False(mask.Blocked);
            }
            var rng = new Random(20261007);
            for (int combo = 0; combo < 500; combo++)
            {
                long want = 0;
                for (int i = 0; i < srcs.Length; i++) { bool on = rng.Next(4) == 0; srcs[i].Blocked = on; links[i].Resync(); if (on) want |= 1L << i; }
                Assert.Equal(want, mask.Mask);
                Assert.Equal(want != 0, mask.Blocked);
                long before = inner.Placed;
                bool ok = sentinel.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 1, 1).ok;
                Assert.Equal(want == 0, ok);
                Assert.Equal(want == 0 ? before + 1 : before, inner.Placed);
            }
            Assert.Equal(0, sentinel.BypassAttempts);
        }

        [Fact]
        public void TwentyNineLayers_ConcurrentRaiseClear_ExactlyOnceTransitions_ZeroAlloc()
        {
            var (mask, srcs, links, rec) = Full();
            Assert.True(mask.Seal(out _));
            long raised0 = rec.RaisedEvents;
            using var barrier = new Barrier(srcs.Length);
            Parallel.For(0, srcs.Length, new ParallelOptions { MaxDegreeOfParallelism = srcs.Length }, i =>
            {
                barrier.SignalAndWait();
                srcs[i].Blocked = true;
                for (int k = 0; k < 50; k++) links[i].Resync();                                // 같은 비트 중복 동기화
            });
            Assert.Equal((1L << 29) - 1, mask.Mask);
            Assert.Equal(29, rec.RaisedEvents - raised0);                                        // ★비트당 정확히 1회
            Parallel.For(0, srcs.Length, i => { srcs[i].Blocked = false; links[i].Resync(); });
            Assert.Equal(0, mask.Mask);
            Assert.False(mask.Blocked);
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int r = 0; r < 20; r++)
                    for (int i = 0; i < srcs.Length; i++) { srcs[i].Blocked = (r & 1) == 0; links[i].Resync(); _ = mask.Blocked; }
            }));
        }

        // ════════════ §4 재무장 ════════════

        [Fact]
        public void Rearm_RequiresPhysicalDualKey_AllSources_And_ReconciliationIncompleteStaysBlocked()
        {
            // 정적 전수: TryRearm(bool valveWired, bool liveArmingWired) 구현은 첫 문장에서 이중키 미충족 = false
            string root = RepoRoot();
            int scanned = 0;
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "StockManager"), "*.cs", SearchOption.AllDirectories))
                     .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                string text = File.ReadAllText(f);
                foreach (Match m in Regex.Matches(text, @"bool TryRearm\(bool valveWired, bool liveArmingWired\)\s*\{\s*([^\r\n]*)"))
                {
                    scanned++;
                    string first = m.Groups[1].Value;
                    Assert.True(first.Contains("valveWired") && first.Contains("liveArmingWired") && first.Contains("return false"),
                                $"{Path.GetFileName(f)}: 이중키 선검사 누락 — {first}");
                }
            }
            Assert.True(scanned >= 18, "스캔 수 " + scanned);

            // 실측: 대표 소스 — 한 키만으로는 거부 · sticky 유지
            var inner = new CountingBroker();
            var s = new LiveArmedEgressSentinel(inner, () => false);
            s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 1, 1);
            Assert.True(s.Tripped);
            Assert.False(s.TryRearm(true, false));
            Assert.False(s.TryRearm(false, true));
            Assert.True(s.Tripped);
            var v = new KisVtsProtocolValidator(vts: true);
            v.Trip(KisDivergenceKind.RestTrHeader);
            Assert.False(v.TryRearm(false, true));
            Assert.True(v.Tripped);
            var m2 = new ChildSlotTrackingMatrix(4);
            m2.TripExternal();
            Assert.False(m2.TryRearm(true, false));
            Assert.True(m2.Tripped);

            // ★대사 미완료: 이중키를 줘도 최초 미체결 대사가 확정되기 전엔 차단 유지
            using var h = new PaperTradingSimulationHarness();
            var recon = new BrokerOpenOrderReconciler(h.Execution);
            Assert.True(recon.ArmingBlocked);
            Assert.True(recon.TryRearm(true, true));
            Assert.True(recon.ArmingBlocked);
        }

        // ════════════ §5 카오스 주입 ════════════

        private static (PaperTradingSimulationHarness H, EntryProbe Probe, LiveArmedEgressSentinel Sentinel, MmfFlightRecorder Box) Rig()
        {
            var box = new MmfFlightRecorder(1 << 14);
            EntryProbe? probe = null; LiveArmedEgressSentinel? sentinel = null;
            PaperTradingSimulationHarness? hh = null;
            var h = new PaperTradingSimulationHarness(blackbox: box, brokerDecorator: b =>
            {
                sentinel = new LiveArmedEgressSentinel(b, () => true, box, interlockBlocked: () => hh!.Interlocks.Blocked);
                probe = new EntryProbe(sentinel, () => hh!.Interlocks.Blocked);                  // 송출 진입 시점 집계어(지연 결속)
                return probe;
            });
            hh = h;
            Feed(h);
            h.Pump();
            return (h, probe!, sentinel!, box);
        }

        [Theory]
        [InlineData(ChaosVector.PriceSpike)] [InlineData(ChaosVector.ProxyKill)] [InlineData(ChaosVector.SilentTimeout)]
        [InlineData(ChaosVector.ClockRegression)] [InlineData(ChaosVector.MmfCorruption)] [InlineData(ChaosVector.CredentialLeak)]
        [InlineData(ChaosVector.OrderbookCross)] [InlineData(ChaosVector.ServerError)] [InlineData(ChaosVector.LivelockEscape)]
        public void EachVector_RealTripPath_DisarmsBeforeReturn_ZeroOrdersAfter_ForensicOrder(ChaosVector v)
        {
            var (h, probe, sentinel, box) = Rig();
            using var _h = h;
            var chaos = new ChaosStormInjector(h, box);
            Assert.Equal(0L, h.Interlocks.Mask);
            foreach (var c in Codes) h.Submit(c, OrderSide.Buy, 1, Px + Tick, 1);
            h.Pump();
            long placedBefore = h.Broker.Placed;
            Assert.True(placedBefore > 0);
            Assert.True(chaos.Inject(v), "반환 전 비트 미격발: " + v);                               // ★0ms 원자 Disarm
            Assert.True(h.Interlocks.IsRaised(ChaosStormInjector.BitOf(v)));
            Assert.True(chaos.SyncTransitions >= 1);                                            // 격발이 주입 호출 안에서 완결
            for (int r = 0; r < 5; r++) { Feed(h); foreach (var c in Codes) h.Submit(c, OrderSide.Buy, 1, Px + Tick, 1); h.Pump(); }
            Assert.Equal(placedBefore, h.Broker.Placed);                                         // ★격발 후 오발주 0
            Assert.Equal(0, probe.ForwardedWhileBlockedAtEntry);
            Assert.True(h.Worker.InterlockVetoes > 0);
            // 블랙박스 상호 검증: 주입 기록(ChaosInjected · 검증 1) + 그보다 앞선 시퀀스의 InterlockRaised(격발은 주입 기록 전에 이미 완결)
            var snap = new FlightRecordEvent[box.Capacity];
            int n = box.SnapshotRecent(snap);
            var events = snap.Take(n).OrderBy(e => e.Sequence).ToArray();
            var inj = events.Single(e => e.Event == FlightEventKind.ChaosInjected);
            Assert.Equal(((long)v, 1L), (inj.SymbolCode, inj.Arg2));
            Assert.Contains(events, e => e.Event == FlightEventKind.InterlockRaised && e.Sequence < inj.Sequence
                                         && (e.Arg0 & (1L << (int)ChaosStormInjector.BitOf(v))) != 0);
            _ = sentinel;
        }

        private static Task DeadProxy(string pipe)
            => Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                var hello = new ProxyHelloPayload { ApiKind = ProxyApiKind.ProductionCom, ProtocolVersion = ProxyApiKind.ProtocolVersion };
                await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.ProxyHello, in hello));
                for (int i = 0; i < 4; i++) { await srv.WriteAsync(BridgeSerializer.PackHeartbeat(i + 1)); await Task.Delay(30); }
                // 프로세스 무단 종료 대리: 하트비트 없이 파이프 즉시 파기
            });

        [Fact]
        public async Task RealPipeProxyKill_LinkBitDisarms()
        {
            string pipe = "ms2e_chaos_" + Guid.NewGuid().ToString("N");
            var srv = DeadProxy(pipe);
            using var bridge = new KiwoomBridgeClient(pipeName: pipe, heartbeatTimeoutMs: 400);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KiwoomBridgeLink);
            var link = new KiwoomBridgeLinkInterlock(() => bridge.IsProductionProxy, required: true);
            link.BindInterlock(mask, ArmingInterlockSource.KiwoomBridgeLink);
            bridge.ProxyHelloReceived += _ => link.Evaluate();
            await bridge.ConnectAsync();
            var loop = bridge.StartAsync();
            Assert.True(SpinWait.SpinUntil(() => { link.Evaluate(); return !mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink); }, 3000));
            await srv;                                                                           // 프록시 소멸
            Assert.True(SpinWait.SpinUntil(() => { link.Evaluate(); return mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink); }, 5000));
            bridge.Dispose();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task RandomStorm_UnderConcurrentFeedAndOrders_NoForwardOnceBlocked_AllVerified()
        {
            var (h, probe, sentinel, box) = Rig();
            using var _h = h;
            var chaos = new ChaosStormInjector(h, box, seed: 0xC0FFEE);
            using var stop = new CancellationTokenSource();
            long loops = 0;
            var load = Task.Run(() =>                                                            // 시세 + 발주 + 드레인 부하(단일 펌프 스레드)
            {
                while (!stop.IsCancellationRequested)
                {
                    for (int i = 0; i < 64; i++) h.OnTossTick(Codes[i % 3], Px);
                    foreach (var c in Codes) h.Submit(c, OrderSide.Buy, 1, Px + Tick, 1);
                    h.Pump();
                    loops++;
                }
            });
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref loops) > 20, 5000));
            int bad = chaos.Storm(5_000);                                                        // 부하 중 무작위 5천 건
            Thread.Sleep(50);
            long placedAfterStorm = h.Broker.Placed;
            Thread.Sleep(100);
            stop.Cancel();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            _out.WriteLine($"[스톰] 주입 {chaos.Injected} · 검증 {chaos.Verified} · 부하 루프 {loops} · 가상 체결 {h.Broker.Placed} · 드레인 veto {h.Worker.InterlockVetoes} · 송출 veto {sentinel.InterlockVetoes} · 최대 주입→전이 {chaos.MaxInjectToTransitionMicros}µs");
            Assert.Equal(0, bad);
            Assert.Equal(5_000, chaos.Verified);
            Assert.Equal(0, probe.ForwardedWhileBlockedAtEntry);                                  // ★차단 상태 진입 주문 전달 0
            Assert.Equal(placedAfterStorm, h.Broker.Placed);                                      // 스톰 후 추가 송출 0
            Assert.Equal(0, sentinel.BypassAttempts);
            for (int v = 0; v < ChaosStormInjector.VectorCount; v++) Assert.True(chaos.InjectedOf((ChaosVector)v) > 0);
        }

        // ════════════ §6 [방향 B] 연쇄 격발 동결 ════════════

        [Fact]
        public void StormGate_ThreeBitsInWindow_SealsBlackbox_PreservesFirstCause_FreezesOnce()
        {
            long now = 1_000;
            var box = new MmfFlightRecorder(1024);
            var freezes = new List<string>();
            var gate = new CascadingStormFreezeGate(box, freezes.Add, threshold: 3, window: TimeSpan.FromMilliseconds(50), now: () => now);
            long win = (long)(0.05 * System.Diagnostics.Stopwatch.Frequency);
            gate.OnTransition(0b111, 0, 0b111);                                                   // 기동 각인(미무장) = 무시
            Assert.False(gate.Tripped);
            Assert.Equal(1, gate.IgnoredUnarmed);
            gate.Arm();
            gate.OnTransition(1L << 1, 0, 2); now += win + 10;                                    // 창 밖
            gate.OnTransition(1L << 2, 0, 6); now += win + 10;
            gate.OnTransition(1L << 3, 0, 14);
            Assert.False(gate.Tripped);                                                           // 산발 고장 = 스톰 아님
            box.Record(FlightEventKind.ChaosInjected, 99, 0, 0);                                  // 최초 원인 증거
            gate.OnTransition(1L << 4, 0, 30); now += 5;
            gate.OnTransition(1L << 5, 0, 62);
            Assert.True(gate.Tripped);                                                            // 3·4·5 가 창 안
            Assert.Equal((1L << 3) | (1L << 4) | (1L << 5), gate.StormBits);
            Assert.True(box.IsSealed);
            Assert.Single(freezes);
            long recorded = box.TotalRecorded;
            for (int i = 0; i < 5_000; i++) box.Record(FlightEventKind.InterlockRaised, i, 0, 0);  // 스톰 여파 홍수
            Assert.Equal(recorded, box.TotalRecorded);                                            // ★덮어쓰기 0
            var snap = new FlightRecordEvent[box.Capacity];
            int n = box.SnapshotRecent(snap);
            Assert.Contains(snap.Take(n), e => e.Event == FlightEventKind.ChaosInjected && e.SymbolCode == 99);
            Assert.Contains(snap.Take(n), e => e.Event == FlightEventKind.StormFreeze);
            gate.OnTransition(1L << 6, 0, 126);
            Assert.Single(freezes);                                                               // 1회
        }

        [Fact]
        public void StormGate_OnHarness_RandomStormTripsAndSeals()
        {
            var (h, _, _, box) = Rig();
            using var _h = h;
            var freezes = 0;
            var gate = new CascadingStormFreezeGate(box, _ => freezes++);
            h.Recorder.Chain(gate);
            gate.Arm();
            var chaos = new ChaosStormInjector(h, box, seed: 7);
            chaos.Storm(64);
            Assert.True(chaos.SyncTransitions > 0);                                              // 2번째 체인 슬롯(주입기)도 전이 수신
            Assert.True(gate.Tripped);
            Assert.True(System.Numerics.BitOperations.PopCount((ulong)gate.StormBits) >= 3);
            Assert.Equal(1, freezes);
            Assert.True(box.IsSealed);
            Assert.True(box.DroppedWhileSealed > 0);                                              // 이후 주입 기록 = 드롭(증거 보존)
        }

        // ════════════ §7 [방향 A] 비행 기록계 · δ=0 ════════════

        [Fact]
        public void FlightRecorder_100kPerSecond_FourWriters_ZeroAlloc()
        {
            var box = new MmfFlightRecorder(1 << 17);
            for (int i = 0; i < 1000; i++) box.Record(FlightEventKind.ChaosInjected, i, 0, 0);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) box.Record(FlightEventKind.InterlockRaised, i, i, i); }));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, 4, t => { for (int i = 0; i < 250_000; i++) box.Record(FlightEventKind.ChaosInjected, t, i, 0); });
            sw.Stop();
            double rate = 1_000_000 / sw.Elapsed.TotalSeconds;
            _out.WriteLine($"[블랙박스] 4스레드 100만 건 {sw.ElapsedMilliseconds}ms = {rate:N0}건/s · 랩 드롭 {box.DroppedLapped}");
            Assert.True(rate >= 100_000, "초당 10만 건 미달: " + rate);
            Assert.Equal(box.ClaimedSeq, box.TotalRecorded + box.DroppedLapped + box.DroppedWhileSealed);
        }

        [Fact]
        public void ChaosInject_And_StormGate_HotPath_ZeroAlloc()
        {
            var (h, _, _, box) = Rig();
            using var _h = h;
            var gate = new CascadingStormFreezeGate(null, null, threshold: 64);                   // 트립 없는 평가 경로
            h.Recorder.Chain(gate);
            gate.Arm();
            var chaos = new ChaosStormInjector(h, box);
            for (int v = 0; v < ChaosStormInjector.VectorCount; v++) chaos.Inject((ChaosVector)v);   // 최초 격발(콜드)
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) chaos.Inject(chaos.NextVector()); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) gate.OnTransition(1L << (i & 31), 0, 1); }));
            Assert.Equal(0, chaos.Unverified);
        }

        // ════════════ §8 결선 · 오중 빌드 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_EgressInterlockVeto_StormGateArmedAfterSeal_FiveBuilds()
        {
            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Equal(2, Regex.Matches(form, Regex.Escape("interlockBlocked: () => interlockMask.Blocked")).Count);   // 키움 · KIS 송출 센티널
            Assert.Contains("interlockRecorder.Chain(stormGate);", form);
            int seal = form.IndexOf("interlockMask.Seal(out long missingInterlocks)", StringComparison.Ordinal);
            int arm = form.IndexOf("else stormGate.Arm();", StringComparison.Ordinal);
            Assert.True(seal > 0 && arm > seal);                                                  // 봉인 성공 뒤 감시 개시
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}

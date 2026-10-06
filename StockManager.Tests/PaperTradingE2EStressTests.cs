// ============================================================================
// PaperTradingE2EStressTests — [E2E 스트레스 · 추천 A] 실전 게이팅 사슬 + 가상 브로커 모의 거래 · 7대 인터락 동시 격발 ·
//                              포렌식 즉시 각인 · δ=0 · 텔레메트리 렌더 거버너(방향 A) · 송출 우회 백업 인터락(방향 B)
//
//  §1 E2E — 틱/10호가 → 제안 → 드레인(인터락·라우터 게이트·보호 지정가·T2T) → 가상 체결 → 대사 0 잔존 · P50≤P99≤P99.9.
//  §2 7대 인터락 — 계층별 단일 격발 = 정확히 그 비트 · 포렌식 Disarm 1건 · 이후 가상 송출 0 / 7계층 동시 격발(라운드 반복) =
//     비트당 전이 정확히 1회 · 체인 무결 · 격발 후 송출 0 / 관측자 예외는 차단 무영향 · 관측자 교체 금지.
//  §3 [방향 A] 텔레메트리 — seqlock 찢김 0(동시 판독) · 1만 틱 부하에서 렌더 창 합치기.
//  §4 [방향 B] LiveArmedEgressSentinel — 무장 해제 Place 전달 0 · 증거 · sticky · 이중키 재무장 · 드레인 경유 동결.
//  §5 δ=0(E2E 정상 상태 · 종목 문자열 캐시) · 실 브로커 유출 0(객체 그래프) · 호스트 결선 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class PaperTradingE2EStressTests
    {
        private static readonly long[] Codes = { 5930, 660, 35420, 51910, 5380, 207940, 68270, 105560 };
        private const long Px = 70_000, Tick = 100;

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static OrderBookPayload Book(long code, long mid = Px, long qty = 1_000)
        {
            var b = new OrderBookPayload { SymbolCode = code, TimestampTicks = 1 };
            for (int i = 0; i < 10; i++)
            {
                b.AskPrice[i] = mid + Tick * (i + 1); b.AskQty[i] = qty;
                b.BidPrice[i] = mid - Tick * i;       b.BidQty[i] = qty;
            }
            return b;
        }

        private static void Feed(PaperTradingSimulationHarness h)
        {
            foreach (var c in Codes) { h.OnTossTick(c, Px); var b = Book(c); h.OnKiwoomBook(in b); }
        }

        private static PaperTradingSimulationHarness Ready()
        {
            var h = new PaperTradingSimulationHarness();
            Feed(h);
            h.Pump();                                                 // 피드 수립 → RouterFeed 레벨 차단 해제
            Assert.Equal(0L, h.Interlocks.Mask);
            return h;
        }

        // ════════════ §1 E2E ════════════

        [Fact]
        public void E2E_FullChain_AllFilled_Reconciled_LatencyDistribution()
        {
            using var h = Ready();
            int n = 0;
            for (int round = 0; round < 20; round++)
            {
                Feed(h);
                foreach (var c in Codes)
                {
                    h.Submit(c, OrderSide.Buy, 1, Px + Tick, 3); n++;      // 지정가 매수(최우선 매도 체결 가능가)
                    h.Submit(c, OrderSide.Sell, 1, Px, 2); n++;            // 지정가 매도
                    h.Submit(c, OrderSide.Buy, 0, 0, 1); n++;              // 시장가 → 보호 지정가 + 1~3호가 깊이
                }
                h.Pump();
            }
            Assert.True(h.Telemetry.TryRead(out var t));
            Assert.Equal(n, t.Submitted);
            Assert.Equal(n, t.Forwarded);
            Assert.Equal(n, t.VirtualPlaced);
            Assert.Equal(n, t.VirtualFilled);
            Assert.Equal(0, t.OpenTracked);                                // ★미체결 대사 잔존 0
            Assert.Equal(0, t.InterlockVetoes + t.RouteVetoes + t.LatencyVetoes);
            Assert.Equal(0, h.Broker.Rejected);
            Assert.True(h.Gate.MarketConverted >= Codes.Length * 20);
            Assert.Equal(n, t.T2TCount);
            Assert.True(t.T2TP50Micros <= t.T2TP99Micros && t.T2TP99Micros <= t.T2TP999Micros, $"P50 {t.T2TP50Micros} P99 {t.T2TP99Micros} P99.9 {t.T2TP999Micros}");
            Assert.True(t.T2TP999Micros > 0);
            Assert.Equal(0L, t.InterlockMask);
        }

        [Fact]
        public void E2E_ZeroAlloc_SteadyState_TickBookOrderFillReconcileTelemetry()
        {
            using var h = Ready();
            var books = Codes.Select(c => Book(c)).ToArray();
            Action body = () =>
            {
                for (int k = 0; k < 25; k++)
                {
                    for (int i = 0; i < Codes.Length; i++)
                    {
                        h.OnTossTick(Codes[i], Px);
                        h.OnKiwoomBook(in books[i]);
                        h.Submit(Codes[i], (k & 1) == 0 ? OrderSide.Buy : OrderSide.Sell, 1, (k & 1) == 0 ? Px + Tick : Px, 1);
                    }
                    h.Pump();
                }
            };
            body();   // 워밍업(JIT · 종목 문자열 캐시 · 정적 초기화)
            long misses = KseSymbolText.Misses;
            Assert.Equal(0, MinAllocated(body));
            Assert.Equal(misses, KseSymbolText.Misses);                    // 정상 상태 캐시 미스 0
            Assert.Equal(0, h.Execution.Tracked);
        }

        // ════════════ §2 7대 인터락 ════════════

        public static IEnumerable<object[]> Layers() => Enum.GetValues<InterlockLayer>().Select(l => new object[] { l });

        [Theory]
        [MemberData(nameof(Layers))]
        public void SingleLayer_Trip_ExactBit_ForensicDisarm_NoFurtherPlacement(InterlockLayer layer)
        {
            using var h = Ready();
            var bit = SevenLayerInterlockInjector.Sources[(int)layer];
            long raisedBefore = h.Recorder.RaisedEvents, headBefore = h.Forensic.WriteHead;
            h.Injector.Inject(layer);
            Assert.True(h.Injector.IsBlocked(layer));
            Assert.Equal(1L << (int)bit, h.Interlocks.Mask);               // ★정확히 그 비트만
            Assert.True(h.Interlocks.Blocked);                              // 격발 반환 즉시 차단 가시(비동기 지연 0)
            Assert.Equal(raisedBefore + 1, h.Recorder.RaisedEvents);
            var link = h.Forensic.ReadLink(h.Forensic.WriteHead);
            Assert.True(h.Forensic.WriteHead > headBefore);
            Assert.Equal(AuditEventKind.Disarm, link.Kind);
            Assert.Equal(1L << (int)bit, link.PayloadTag);                  // ★체인에 어느 인터락인지 각인
            Assert.True(h.Forensic.VerifyIntegrity());

            long placed = h.Broker.Placed;
            if (layer != InterlockLayer.RouterFeed) Feed(h);
            foreach (var c in Codes) h.Submit(c, OrderSide.Buy, 1, Px + Tick, 1);
            h.Pump();
            Assert.Equal(placed, h.Broker.Placed);                          // ★격발 후 가상 송출 0
            Assert.Equal(Codes.Length, h.Worker.InterlockVetoes);
        }

        [Fact]
        public void SevenLayers_ConcurrentTrip_ExactlyOncePerBit_ChainIntact_ZeroPlacementAfter()
        {
            for (int round = 0; round < 40; round++)
            {
                using var h = Ready();
                long baseHead = h.Forensic.WriteHead, baseCleared = h.Recorder.ClearedEvents;   // 기동 전이(결속 raise/clear · 봉인) 이후 기준
                using var go = new Barrier(8);                              // 7 격발 + 1 펌프
                var stop = 0;
                var injectors = Enum.GetValues<InterlockLayer>().Select(l => new Thread(() => { go.SignalAndWait(); h.Injector.Inject(l); })).ToList();
                // 펌프 스레드: 격발과 동시에 발주 압력(단일 소비자 계약 유지 — 펌프는 이 스레드만)
                var pump = new Thread(() =>
                {
                    go.SignalAndWait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        foreach (var c in Codes) { h.OnTossTick(c, Px); h.Submit(c, OrderSide.Buy, 1, Px + Tick, 1); }
                        h.Pump();
                    }
                });
                injectors.ForEach(t => t.Start()); pump.Start();
                injectors.ForEach(t => t.Join());
                Volatile.Write(ref stop, 1);
                pump.Join();

                long all = SevenLayerInterlockInjector.Sources.Aggregate(0L, (m, s) => m | (1L << (int)s));
                Assert.Equal(all, h.Interlocks.Mask & all);
                Assert.Equal(all, h.Recorder.RaisedUnion & all);
                // ★비트당 전이 정확히 1회: 기준 이후 Disarm 레코드의 비트 합 = 정확히 7(동시 Interlocked.Or 경합에서도 중복·누락 0)
                long raisedBitsTotal = 0, other = 0;
                for (long s = baseHead + 1; s <= h.Forensic.WriteHead; s++)
                {
                    var l = h.Forensic.ReadLink(s);
                    Assert.True(l.Live);
                    if (l.Kind == AuditEventKind.Disarm) { raisedBitsTotal += System.Numerics.BitOperations.PopCount((ulong)l.PayloadTag); Assert.Equal(0L, l.PayloadTag & ~all); }
                    else other++;
                }
                Assert.Equal(7, raisedBitsTotal);
                Assert.Equal(0, other);                                     // sticky — 해제(Arm) 레코드 0
                Assert.Equal(baseCleared, h.Recorder.ClearedEvents);
                Assert.Equal(0, h.Interlocks.SinkFaults);
                Assert.True(h.Forensic.VerifyIntegrity());

                long placed = h.Broker.Placed;
                Feed(h);
                foreach (var c in Codes) h.Submit(c, OrderSide.Buy, 1, Px + Tick, 1);
                h.Pump();
                Assert.Equal(placed, h.Broker.Placed);                      // ★동시 격발 이후 송출 0
                Assert.Equal(0, h.Execution.Tracked);                       // 경합 중 송출분도 전량 체결·대사(잔존 0)
                Assert.Equal(0, h.Broker.Rejected);
            }
        }

        [Fact]
        public void Bitmask_TransitionSink_ExactlyOnce_FaultsDoNotUnblock_NoReplace()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KiwoomServerError);
            var src = new KiwoomServerErrorInterlock();
            var thrower = new ThrowingSink();
            mask.AttachTransitionSink(thrower);                              // 결속 시점 차단 비트(필수 미결속+샌니티) 1회 통지
            Assert.Equal(1, thrower.Calls);
            Assert.Throws<InvalidOperationException>(() => mask.AttachTransitionSink(new ThrowingSink()));
            src.BindInterlock(mask, ArmingInterlockSource.KiwoomServerError);
            Assert.True(mask.Seal(out _));
            Assert.False(mask.Blocked);
            int before = thrower.Calls;
            src.OnError(new ErrorPayload { ErrorCode = KiwoomServerErrorCodes.Margin });
            src.OnError(new ErrorPayload { ErrorCode = KiwoomServerErrorCodes.Password });   // 이미 선 비트 — 전이 아님
            Assert.True(mask.Blocked);                                       // ★관측자가 throw 해도 차단 유지
            Assert.Equal(before + 1, thrower.Calls);
            Assert.True(mask.SinkFaults >= 1);
            Assert.True(src.TryRearm(true, true));
            Assert.False(mask.Blocked);
            Assert.Equal(before + 2, thrower.Calls);                         // cleared 전이 1회
        }

        private sealed class ThrowingSink : IInterlockTransitionSink
        {
            public int Calls;
            public void OnTransition(long raisedBits, long clearedBits, long maskAfter) { Calls++; throw new InvalidOperationException("sink"); }
        }

        // ════════════ §3 [방향 A] 텔레메트리 ════════════

        [Fact]
        public void Telemetry_Seqlock_NoTornReads_UnderConcurrentPublish()
        {
            var ch = new SimulationTelemetryChannel();
            int stop = 0; long torn = 0, reads = 0;
            var readers = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    if (!ch.TryRead(out var t)) continue;
                    Interlocked.Increment(ref reads);
                    if (t.Ticks != t.Books || t.Books != t.Submitted || t.Submitted != t.T2TP999Micros || t.T2TP999Micros != t.ManagedHeapBytes) Interlocked.Increment(ref torn);
                }
            })).ToList();
            readers.ForEach(r => r.Start());
            for (long k = 1; k <= 200_000; k++)
            {
                var t = new SimulationTelemetry { Ticks = k, Books = k, Submitted = k, T2TP999Micros = k, ManagedHeapBytes = k };
                ch.Publish(in t);
            }
            Volatile.Write(ref stop, 1);
            readers.ForEach(r => r.Join());
            Assert.Equal(0, Interlocked.Read(ref torn));
            Assert.True(Interlocked.Read(ref reads) > 0);
            Assert.True(ch.TryRead(out var last));
            Assert.Equal(200_000, last.Version);
        }

        [Fact]
        public void Telemetry_10kTicks_RenderGovernorCoalesces_RenderReadsConsistentSnapshot()
        {
            long renders = 0, lastVersion = 0, regress = 0;
            PaperTradingSimulationHarness? hh = null;
            using var gov = new MS2E.UI.Portable.Infrastructure.SnapshotRenderGovernor(() =>
            {
                if (hh!.Telemetry.TryRead(out var t))
                {
                    if (t.Version < Interlocked.Read(ref lastVersion)) Interlocked.Increment(ref regress);
                    Interlocked.Exchange(ref lastVersion, t.Version);
                    Interlocked.Increment(ref renders);
                }
            }, TimeSpan.FromMilliseconds(50));
            using var h = new PaperTradingSimulationHarness(renderSignal: gov.Signal);
            hh = h;
            Feed(h);
            h.Pump();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10_000; i++)
            {
                h.OnTossTick(Codes[i & 7], Px);
                if ((i & 7) == 7) h.Submit(Codes[i & 7], OrderSide.Buy, 1, Px + Tick, 1);
                if (i % 50 == 49) h.Pump();                                 // 200 게시
            }
            sw.Stop();
            Thread.Sleep(200);                                              // 후행 창 렌더
            Assert.True(gov.Signals >= 200);
            Assert.True(gov.Renders >= 1);
            Assert.True(gov.Renders <= 2 + (sw.ElapsedMilliseconds / 50 + 2) * 2, $"renders={gov.Renders} elapsed={sw.ElapsedMilliseconds}ms");   // 창당 ≤ 선행+후행
            Assert.True(gov.Renders < gov.Signals / 2, $"합치기 실패 renders={gov.Renders} signals={gov.Signals}");
            Assert.Equal(0, Interlocked.Read(ref regress));
            Assert.True(h.Telemetry.TryRead(out var final));
            Assert.Equal(10_000 + Codes.Length, final.Ticks);
        }

        // ════════════ §4 [방향 B] 송출 우회 백업 인터락 ════════════

        private sealed class CountingBroker : IBroker
        {
            public int Places, Cancels;
            public string Name => "counting";
            public bool IsLive => true;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice) { Places++; return (true, "777"); }
            public void Cancel(string orderId) => Cancels++;
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        [Fact]
        public void EgressSentinel_LiveArmedFalse_ZeroForward_Evidence_Sticky_DualKeyRearm()
        {
            bool armed = false; string? freeze = null;
            var inner = new CountingBroker();
            var s = new LiveArmedEgressSentinel(inner, () => armed, onBypass: why => freeze = why);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.EgressBypass);
            s.BindInterlock(mask, ArmingInterlockSource.EgressBypass);
            Assert.True(mask.Seal(out _));
            var (ok, _) = s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 7m, 70_000m, 70_000m);
            Assert.False(ok);
            Assert.Equal(0, inner.Places);                                  // ★실 브로커 전달 0
            Assert.Equal((1L, 5930L, 7_000L), (s.BypassAttempts, s.LastSymbolCode, s.LastQtyMilli));
            Assert.True(s.Tripped && mask.IsRaised(ArmingInterlockSource.EgressBypass));
            Assert.NotNull(freeze);
            armed = true;                                                   // 무장돼도 sticky — 재무장 전 전달 금지
            Assert.False(s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1m, 70_000m, 70_000m).ok);
            Assert.Equal(0, inner.Places);
            s.Cancel("1");
            Assert.Equal(1, inner.Cancels);                                 // 취소는 노출 축소 — 통과
            Assert.False(s.TryRearm(true, false));
            Assert.True(s.TryRearm(true, true));
            Assert.False(mask.Blocked);
            Assert.True(s.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1m, 70_000m, 70_000m).ok);
            Assert.Equal(1, inner.Places);
            Assert.Throws<ArgumentNullException>(() => new LiveArmedEgressSentinel(inner, null!));
            var throwing = new LiveArmedEgressSentinel(inner, () => throw new InvalidOperationException());
            Assert.False(throwing.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1m, 1m, 1m).ok);   // 판독 예외 = 미무장
        }

        [Fact]
        public void EgressSentinel_UpstreamMalfunction_DrainWorker_ZeroLeak_Freeze()
        {
            // 상류 오작동 모사: 디스패처 무장 술어가 (버그로) true · 인터락 미주입 → Place 도달 → 센티널이 차단
            var att = new OrderAttestationService();
            var inner = new CountingBroker();
            var s = new LiveArmedEgressSentinel(inner, () => false, onBypass: why => att.ForceFreeze(why));
            var ring = new OrderDispatchRing(64);
            var disp = new GatedOrderDispatcher(ring, () => true, att);
            using var worker = new OrderDrainWorker(disp, s, wired: false, attestation: att);
            for (int i = 0; i < 5; i++) ring.Publish(0, 5930, OrderSide.Buy, 1, new QPrice(70_000L * 100_000_000L), new QQty(100_000_000L), 0);
            worker.DrainOnce();
            Assert.Equal(0, inner.Places);                                  // ★유출 0
            Assert.Equal(1, s.BypassAttempts);                              // 첫 시도에서 동결 → 나머지 미처리
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            worker.DrainOnce();
            Assert.Equal(0, inner.Places);
        }

        // ════════════ §5 유출 0 · δ=0 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void ZeroRealOrderLeakage_HarnessObjectGraph_HasNoRealBroker()
        {
            using var h = Ready();
            var forbidden = new[] { typeof(ExecutionBridgeBroker), typeof(KiwoomBridgeClient), typeof(LiveBrokerStub) };
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(object o, int depth)>();
            stack.Push((h, 0));
            int visited = 0;
            while (stack.Count > 0)
            {
                var (o, d) = stack.Pop();
                if (!seen.Add(o) || d > 4) continue;
                visited++;
                Assert.DoesNotContain(o.GetType(), forbidden);
                if (o is IBroker b) Assert.False(b.IsLive, "하네스 그래프에 IsLive 브로커: " + o.GetType().Name);
                if (o.GetType().Namespace is not { } ns || !(ns.StartsWith("StockManager") || ns.StartsWith("MS2E"))) continue;
                foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (!f.FieldType.IsValueType && f.GetValue(o) is { } v && v is not string && v is not Delegate && v is not Array) stack.Push((v, d + 1));
            }
            Assert.True(visited > 10, "그래프 탐색 범위 부족: " + visited);
        }

        [Fact]
        public void KseSymbolText_HitIsZeroAlloc_AndCorrect()
        {
            Assert.Equal("005930", KseSymbolText.D6(5930));
            Assert.Equal("000660", KseSymbolText.D6(660));
            Assert.Equal("005930", KseSymbolText.D6(5930 + 4096 * 0));
            Assert.Equal("009026", KseSymbolText.D6(5930 + 4096 - 1000));   // 다른 코드(충돌 슬롯 교체 정확성)
            Assert.Equal("005930", KseSymbolText.D6(5930));
            Action body = () => { for (int i = 0; i < 1000; i++) _ = KseSymbolText.D6(5930); };
            body();
            Assert.Equal(0, MinAllocated(body));
        }

        [Fact]
        public void Host_Wires_ForensicRecorder_And_EgressSentinel()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(ArmingInterlockBitmask).GetMethod(nameof(ArmingInterlockBitmask.AttachTransitionSink))!,
                typeof(InterlockForensicRecorder).GetConstructors()[0],
                typeof(LiveArmedEgressSentinel).GetConstructors()[0],
                typeof(LiveArmedEgressSentinel).GetMethod(nameof(LiveArmedEgressSentinel.BindInterlock))!,
            };
            var found = new bool[targets.Length];
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var ty in types)
            {
                var all = new List<MethodBase>(ty.GetMethods(AllF));
                all.AddRange(ty.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("new OrderDrainWorker(ringDispatcher, egressSentinel,", src);   // 실 브로커 직결 금지 — 센티널 경유
            Assert.Contains("MS2E.Core.ArmingInterlockSource.EgressBypass);", src);          // 필수 비트
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void FiveBuild_Regression()
        {
            string sln = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager.sln"));
            foreach (var p in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj", @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(p, sln);
            Assert.Equal(15, (int)ArmingInterlockSource.EgressBypass);
            Assert.Equal(7, SevenLayerInterlockInjector.Sources.Distinct().Count());
            string drain = File.ReadAllText(Path.Combine(RepoRoot(), "src", "02_Infrastructure", "Engine", "OrderDrainWorker.cs"));
            Assert.DoesNotContain("SymbolCode.ToString(\"D6\")", drain);   // 주문당 문자열 할당 회귀 차단
        }
    }
}

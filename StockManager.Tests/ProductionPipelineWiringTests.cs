// ============================================================================
// ProductionPipelineWiringTests — [실배선 · 추천 A] 원자 인터락 집계기(방향 B) + 기동 샌니티 봉인(방향 A) + 송출 직전 최종 veto
//
//  §1 ArmingInterlockBitmask — 미결속 = 차단 · 봉인 누락 진단 · 재결속 거부 · 판독 예외 = 차단 · Trip/재무장 경합 수렴.
//  §2 엔진 푸시 결속 — 부팅 무결성·자격증명 유출·포렌식 체인·이상 시세·카오스 가드가 상태 전이 즉시 비트를 올리고/내린다.
//  §3 E2E 발주 파이프라인 — 링 → GatedOrderDispatcher(무장 판정) → OrderDrainWorker(최종 veto) → IBroker.Place.
//      판정↔송출 사이 Trip · 미봉인 · 필수 소스 미결속이면 실 브로커 호출 0.
//  §4 호스트 조립 IL 실측 — BlazorHomeForm 이 5개 엔진을 결속하고 Seal 후 드레인 워커에 집계기를 주입하는지.
//  §5 δ=0 — Blocked 판독 · Resync · 최종 veto 경로 힙 0(Release).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    internal sealed class WiringFlagSource : IArmingBlockSource
    {
        public volatile bool Block;
        public bool Throw;
        public bool ArmingBlocked => Throw ? throw new InvalidOperationException("probe fault") : Block;
    }

    internal sealed class WiringCountingBroker : IBroker
    {
        public int Calls;
        public string Name => "Counting";
        public bool IsLive => false;
        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind,
                                           decimal qty, decimal limitPrice, decimal currentPrice)
        { Interlocked.Increment(ref Calls); return (true, "ok"); }
        public void Cancel(string orderId) { }
        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    [Collection("ConcurrencyStress")]
    public sealed class ProductionPipelineWiringTests : IDisposable
    {
        private static readonly ArmingInterlockSource[] All5 =
        {
            ArmingInterlockSource.BootIntegrity, ArmingInterlockSource.CredentialLeak, ArmingInterlockSource.ForensicChain,
            ArmingInterlockSource.MarketSpike, ArmingInterlockSource.ChaosProgress,
        };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "gm_wire_" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 무관 */ } }

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

        private static (ArmingInterlockBitmask Mask, WiringFlagSource[] Sources) HealthySealed()
        {
            var m = new ArmingInterlockBitmask(All5);
            var src = new WiringFlagSource[All5.Length];
            for (int i = 0; i < All5.Length; i++) { src[i] = new WiringFlagSource(); m.Bind(All5[i], src[i]); }
            Assert.True(m.Seal(out long missing));
            Assert.Equal(0L, missing);
            return (m, src);
        }

        // ════════════ §1 ArmingInterlockBitmask ════════════

        [Fact]
        public void Fresh_IsBlocked_UntilAllRequiredBound_AndSealed()
        {
            var m = new ArmingInterlockBitmask(All5);
            Assert.True(m.Blocked);                                         // 미결속 + 미봉인 = 차단(기본 fail-closed)
            Assert.True(m.IsRaised(ArmingInterlockSource.StartupSanity));
            var a = new WiringFlagSource(); var b = new WiringFlagSource();
            m.Bind(ArmingInterlockSource.BootIntegrity, a);
            m.Bind(ArmingInterlockSource.CredentialLeak, b);
            Assert.False(m.Seal(out long missing));                          // 3개 누락 → 봉인 거부
            long expected = (1L << 2) | (1L << 3) | (1L << 4);
            Assert.Equal(expected, missing);
            Assert.True(m.Blocked);
            Assert.True(m.IsRaised(ArmingInterlockSource.StartupSanity));   // 샌니티 비트 유지 = 무장 불가
            Assert.False(m.IsSealed);

            m.Bind(ArmingInterlockSource.ForensicChain, new WiringFlagSource());
            m.Bind(ArmingInterlockSource.MarketSpike, new WiringFlagSource());
            m.Bind(ArmingInterlockSource.ChaosProgress, new WiringFlagSource());
            Assert.True(m.Blocked);                                         // 결속 완료여도 봉인 전엔 차단
            Assert.True(m.Seal(out missing));
            Assert.Equal(0L, missing);
            Assert.False(m.Blocked);
            Assert.Equal(0L, m.Mask);
            Assert.True(m.Seal(out _));                                     // 멱등
        }

        [Fact]
        public void Bind_AlreadyBlockedSource_KeepsBit_AndRebindRejected()
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.MarketSpike);
            var s = new WiringFlagSource { Block = true };
            m.Bind(ArmingInterlockSource.MarketSpike, s);
            Assert.True(m.Seal(out _));
            Assert.True(m.Blocked);
            Assert.True(m.IsRaised(ArmingInterlockSource.MarketSpike));
            Assert.Throws<InvalidOperationException>(() => m.Bind(ArmingInterlockSource.MarketSpike, new WiringFlagSource()));   // 소스 바꿔치기 금지
            Assert.True(m.Blocked);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ArmingInterlockBitmask(ArmingInterlockSource.StartupSanity));
        }

        [Fact]
        public void SourceProbeFault_IsBlocked()
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.ChaosProgress);
            var s = new WiringFlagSource();
            var link = m.Bind(ArmingInterlockSource.ChaosProgress, s);
            Assert.True(m.Seal(out _));
            Assert.False(m.Blocked);
            s.Throw = true;
            link.Resync();
            Assert.True(m.Blocked);                                         // 판독 불가 = 차단
            s.Throw = false;
            link.Resync();
            Assert.False(m.Blocked);
        }

        [Fact]
        public void ConcurrentTripAndRearm_ConvergesToFinalSourceState()
        {
            // Trip 측(플래그 → Resync) 과 재무장 측(Clear → 재확인)이 경합해도 최종 비트 = 소스 최종 상태.
            for (int round = 0; round < 200; round++)
            {
                var m = new ArmingInterlockBitmask(ArmingInterlockSource.MarketSpike);
                var s = new WiringFlagSource();
                var link = m.Bind(ArmingInterlockSource.MarketSpike, s);
                Assert.True(m.Seal(out _));
                using var go = new Barrier(2);
                var tripper = new Thread(() => { go.SignalAndWait(); for (int i = 0; i < 2_000; i++) { s.Block = (i & 1) == 0; link.Resync(); } s.Block = true; link.Resync(); });
                var rearmer = new Thread(() => { go.SignalAndWait(); for (int i = 0; i < 2_000; i++) link.Resync(); });
                tripper.Start(); rearmer.Start();
                Assert.True(tripper.Join(30_000)); Assert.True(rearmer.Join(30_000));
                Assert.True(m.Blocked, $"round {round}: 최종 소스 차단인데 비트 내려감(fail-open)");
            }
        }

        // ════════════ §2 엔진 푸시 결속 ════════════

        [Fact]
        public void MarketSpike_Trip_RaisesImmediately_Rearm_Clears()
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.MarketSpike);
            var spike = new MarketDataSpikeInterlock();
            spike.BindInterlock(m, ArmingInterlockSource.MarketSpike);
            Assert.True(m.Seal(out _));
            Assert.False(m.Blocked);
            Assert.Equal(SpikeVerdict.Accepted, spike.Observe(5930, 100_000));
            Assert.Equal(SpikeVerdict.TickJump, spike.Observe(5930, 200_000));   // +100% 점프
            Assert.True(m.Blocked);                                               // ★수동 Resync 없이 즉시 반영(푸시)
            Assert.False(spike.TryRearm(true, false));
            Assert.True(m.Blocked);
            Assert.True(spike.TryRearm(true, true));
            Assert.False(m.Blocked);
        }

        [Fact]
        public void ChaosGuard_Hold_Raises_Reset_Clears()
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.ChaosProgress);
            var g = new ChaosResilientProgressGuard(spinCeiling: 8);
            g.BindInterlock(m, ArmingInterlockSource.ChaosProgress);
            Assert.True(m.Seal(out _));
            Assert.False(m.Blocked);
            g.OnLivelockEscape();
            Assert.True(m.Blocked);
            g.Reset();
            Assert.False(m.Blocked);
        }

        [Fact]
        public void CredentialLeak_Trip_RaisesPermanently()
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.CredentialLeak);
            var leak = new RawCredentialLeakInterlock();
            leak.RegisterCredential("SK-live-7f3a9c21b8e4d6f0");
            leak.BindInterlock(m, ArmingInterlockSource.CredentialLeak);
            Assert.True(m.Seal(out _));
            Assert.False(m.Blocked);
            Assert.False(leak.Inspect("ordinary log line".AsSpan()));
            Assert.False(m.Blocked);
            leak.Inspect("token=SK-live-7f3a9c21b8e4d6f0".AsSpan());
            Assert.True(leak.Tripped);
            Assert.True(m.Blocked);
        }

        [Fact]
        public void ForensicChain_Tamper_Raises()
        {
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.ForensicChain);
            var stream = new ZeroAllocForensicAuditStream(64);
            long seq = stream.Append(AuditEventKind.Arm, 1, DateTime.UtcNow.Ticks);
            stream.Append(AuditEventKind.Disarm, 2, DateTime.UtcNow.Ticks);
            using var fc = new ForensicChainDisarmInterlock(stream);
            fc.BindInterlock(m, ArmingInterlockSource.ForensicChain);
            Assert.True(m.Seal(out _));
            Assert.True(fc.Check().Intact);
            Assert.False(m.Blocked);
            stream.__AdversarialCorruptForTest(seq, 0);
            Assert.False(fc.Check().Intact);
            Assert.True(m.Blocked);
        }

        [Fact]
        public void BootIntegrity_Unsealed_KeepsBit_Sealed_Clears_Dispose_Raises()
        {
            string host = Path.Combine(_root, "host"), proxy = Path.Combine(_root, "proxy");
            Directory.CreateDirectory(host); Directory.CreateDirectory(proxy);
            File.WriteAllBytes(Path.Combine(host, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(host, "MS2E.StockSuite.exe"), new byte[] { 5, 6 });
            File.WriteAllBytes(Path.Combine(proxy, "MS2E.KiwoomProxyHost.exe"), new byte[] { 7 });

            var m1 = new ArmingInterlockBitmask(ArmingInterlockSource.BootIntegrity);
            using (var unsealed = BootIntegrityGate.Start(host, runAsync: false))
            {
                unsealed.BindInterlock(m1, ArmingInterlockSource.BootIntegrity);
                Assert.True(m1.Seal(out _));
                Assert.True(m1.Blocked);                                     // manifest.sig 없음 = 무장 차단
                Assert.True(m1.IsRaised(ArmingInterlockSource.BootIntegrity));
            }

            AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", host, proxy, Path.Combine(_root, BootIntegrityGate.ManifestFileName));
            var m2 = new ArmingInterlockBitmask(ArmingInterlockSource.BootIntegrity);
            var g = BootIntegrityGate.Start(host, runAsync: false);
            g.BindInterlock(m2, ArmingInterlockSource.BootIntegrity);
            Assert.True(m2.Seal(out _));
            Assert.Equal(BootIntegrityStatus.Sealed, g.Status);
            Assert.False(m2.Blocked);
            g.Dispose();
            Assert.True(m2.Blocked);                                         // 처분 = 차단 푸시
        }

        [Fact]
        public void BootIntegrity_AsyncConverge_BeforeBind_StillSynced()
        {
            // 비동기 검증이 결속 전에 끝나도(링크 null 시점 Set) Bind 동기화가 최종 상태를 반영.
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.BootIntegrity);
            using var g = BootIntegrityGate.Start(Path.Combine(_root, "nowhere"), runAsync: false);
            g.BindInterlock(m, ArmingInterlockSource.BootIntegrity);
            Assert.True(m.Seal(out _));
            Assert.True(m.Blocked);
        }

        // ════════════ §3 E2E 발주 파이프라인 ════════════

        private static (OrderDispatchRing Ring, WiringCountingBroker Broker) Publish(int n)
        {
            var ring = new OrderDispatchRing(64);
            for (int i = 0; i < n; i++)
                ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)1, QPrice.Of(70_000m), QQty.Of(1m), 100 + i);
            return (ring, new WiringCountingBroker());
        }

        [Fact]
        public void E2E_AllInterlocksHealthy_Sealed_OrdersReachBroker()
        {
            var (m, _) = HealthySealed();
            var (ring, broker) = Publish(3);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, interlocks: m);
            w.DrainOnce();
            Assert.Equal(3, broker.Calls);
            Assert.Equal(0L, w.InterlockVetoes);
        }

        [Fact]
        public void E2E_Unsealed_OrMissingSource_ZeroOrdersReachBroker()
        {
            // 디스패처가 '무장' 이라 판정해도(isArmed=true) 필수 소스 미결속/미봉인이면 실 브로커 호출 0.
            var m = new ArmingInterlockBitmask(All5);
            m.Bind(ArmingInterlockSource.BootIntegrity, new WiringFlagSource());      // 4개 미결속
            Assert.False(m.Seal(out _));
            var (ring, broker) = Publish(5);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, interlocks: m);
            w.DrainOnce();
            Assert.Equal(0, broker.Calls);
            Assert.Equal(5L, w.InterlockVetoes);
            Assert.Equal(0L, disp.Forwarded);
        }

        [Fact]
        public void E2E_TripBetweenArmDecisionAndSend_Vetoed()
        {
            // 판정↔송출 TOCTOU: 무장 판정 순간(isArmed 호출 안)에 이상 시세 Trip → 판정은 true 로 통과하지만 송출 직전 veto.
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.MarketSpike);
            var spike = new MarketDataSpikeInterlock();
            spike.BindInterlock(m, ArmingInterlockSource.MarketSpike);
            Assert.True(m.Seal(out _));
            spike.Observe(5930, 100_000);
            var (ring, broker) = Publish(1);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => { spike.Observe(5930, 300_000); return true; }, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, interlocks: m);
            w.DrainOnce();
            Assert.True(spike.Tripped);
            Assert.Equal(0, broker.Calls);                                   // ★실 브로커 0
            Assert.Equal(1L, w.InterlockVetoes);
        }

        [Fact]
        public void E2E_LegacyWorkerWithoutInterlocks_Unchanged()
        {
            var (ring, broker) = Publish(2);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false);   // 옵션 미주입 = 기존 동작 1비트 불변
            w.DrainOnce();
            Assert.Equal(2, broker.Calls);
        }

        [Fact]
        public void E2E_Disarmed_ZeroOrders_EvenWithHealthyInterlocks()
        {
            var (m, _) = HealthySealed();
            var (ring, broker) = Publish(4);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => false, new OrderAttestationService());   // liveArmed:false
            using var w = new OrderDrainWorker(disp, broker, wired: false, interlocks: m);
            w.DrainOnce();
            Assert.Equal(0, broker.Calls);
            Assert.Equal(4L, disp.GatedOut);
        }

        // ════════════ §4 호스트 조립 IL 실측 ════════════

        private static bool HostCalls(MethodBase target)
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var t in types)
            {
                var all = new List<MethodBase>(t.GetMethods(AllF));
                all.AddRange(t.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try { if (m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == target) return true; } catch (ArgumentException) { }
                }
            }
            return false;
        }

        [Fact]
        public void Host_BindsAllFiveEngines_Seals_AndInjectsIntoDrainWorker()
        {
            foreach (var t in new[] { typeof(BootIntegrityGate), typeof(RawCredentialLeakInterlock), typeof(ForensicChainDisarmInterlock),
                                      typeof(MarketDataSpikeInterlock), typeof(ChaosResilientProgressGuard) })
                Assert.True(HostCalls(t.GetMethod("BindInterlock")!), $"BlazorHomeForm 이 {t.Name}.BindInterlock 을 호출하지 않음");
            Assert.True(HostCalls(typeof(ArmingInterlockBitmask).GetMethod(nameof(ArmingInterlockBitmask.Seal))!), "Seal 미호출(샌니티 봉인 공백)");
            Assert.True(HostCalls(typeof(ArmingInterlockBitmask).GetProperty(nameof(ArmingInterlockBitmask.Blocked))!.GetMethod!), "무장 술어가 집계 비트 미판독");
            var ctor = typeof(OrderDrainWorker).GetConstructors()[0];
            Assert.Contains(ctor.GetParameters(), p => p.Name == "interlocks" && p.ParameterType == typeof(ArmingInterlockBitmask));
            Assert.True(HostCalls(ctor), "OrderDrainWorker 생성 미확인");
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var (m, src) = HealthySealed();
            var spike = new MarketDataSpikeInterlock();
            var m2 = new ArmingInterlockBitmask(ArmingInterlockSource.MarketSpike);
            var link = m2.Bind(ArmingInterlockSource.MarketSpike, src[3]);
            m2.Seal(out _);
            long sink = 0;
            _ = m.Blocked; link.Resync();
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    if (!m.Blocked) sink++;
                    link.Resync();
                    sink += m2.Mask;
                }
            }));
            Assert.True(sink > 0);

            // 최종 veto 경로(차단 상태 드레인 — 송출 없이 거부) δ=0: 사전 발행 후 DrainOnce 만 계측.
            var blocked = new ArmingInterlockBitmask(ArmingInterlockSource.MarketSpike);   // 미결속 = 차단
            var ring = new OrderDispatchRing(1024);
            var broker = new WiringCountingBroker();
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, interlocks: blocked);
            for (int i = 0; i < 10; i++) ring.Publish(0, 5930, OrderSide.Buy, (byte)1, QPrice.Of(70_000m), QQty.Of(1m), i);
            w.DrainOnce();
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 0; i < 200; i++) ring.Publish(0, 5930, OrderSide.Buy, (byte)1, QPrice.Of(70_000m), QQty.Of(1m), i);
                w.DrainOnce();
            }));
            Assert.Equal(0, broker.Calls);
            Assert.True(w.InterlockVetoes >= 210);
        }
    }
}

// ============================================================================
// Stage1CanaryVetoSimulationTests — v11.4.2 Stage 1 카나리 드라이런 검증(Veto & Latency Simulation)
//
//   docs/CANARY_DEPLOYMENT_RUNBOOK.md Stage 1(Mock Staging & Zero-Leak Audit) 규격을 결정론적으로 실증한다:
//     ★ liveArmed:false 기본 무장 해제 상태에서, 이중 브로커(키움 x86 COM · 토스 REST)가 실시간 잔고/시세를 수신하는
//       와중에 가상 실물 매수/매도 발주가 인입되어도 발주 파이프라인에서 100% Veto — 단 1비트도 사출되지 않음.
//     ★ 실통신 레이턴시(Ping/Pong · HTTP RTT)를 δ=0 으로 수집(UI 스레드 패닉 불가).
//     ★ Veto 감사 다이제스트로 '누수 미세 틈새 0'(모든 Veto 기록·무변조)을 증명.
//
//   검증 대상: ZeroLeakVetoInterlock(신규 Veto 초크) · CanaryVetoLatencyGovernor(신규 레이턴시 거버너) +
//             CanaryHealthSentinel(기존 프로덕션 인가 게이트) — Stage 1 전 사슬.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Stage1CanaryVetoSimulationTests
    {
        // ── §1 기본 자세: liveArmed:false ──────────────────────────────────────
        [Fact]
        public void Default_Posture_Is_LiveArmed_False()
        {
            var interlock = new ZeroLeakVetoInterlock();
            Assert.False(interlock.IsArmed);                       // 신규 Veto 초크 기본 미무장

            var canary = new CanaryHealthSentinel();
            Assert.False(canary.IsArmed);                          // 프로덕션 인가 게이트 기본 미무장
            Assert.Equal(0L, canary.TryAuthorize(1_000_000L));    // 물리 이중 키 없이 발주 인가 0
        }

        // ── §2 헤드라인: 가상 매수/매도 100% Veto · 누수 0 ──────────────────────
        [Fact]
        public void VirtualBuySellOrders_100pct_Veto_ZeroLeak()
        {
            var interlock = new ZeroLeakVetoInterlock();           // 미무장(liveArmed:false)
            const int N = 5_000;
            int admitted = 0, vetoed = 0;

            for (int i = 0; i < N; i++)
            {
                // 가상 매수(양수 명목)·매도(양수 명목) 교대 인입 — 실물 발주 요청을 모사.
                long notional = 10_000L + (i % 7) * 3_137L;
                bool ok = interlock.TryAdmit(notional, out var reason);
                if (ok) admitted++; else { vetoed++; Assert.Equal(VetoReason.Disarmed, reason); }
            }

            Assert.Equal(0, admitted);                             // ★단 1건도 인가 안 됨(사출 0비트)
            Assert.Equal(N, vetoed);                               // ★100% Veto
            Assert.Equal(0L, interlock.AdmittedCount);
            Assert.Equal(N, interlock.VetoedCount);
            Assert.Equal(VetoReason.Disarmed, interlock.LastReason);
        }

        [Fact]
        public void CanaryHealthSentinel_Unarmed_Authorizes_Zero()
        {
            var canary = new CanaryHealthSentinel();
            for (int i = 0; i < 1_000; i++)
                Assert.Equal(0L, canary.TryAuthorize(1L + i));     // 미무장 → 전량 0(프로덕션 게이트 Veto)
        }

        // ── §3 Stage 1 시나리오: 이중 브로커 수신 中 발주 Veto ──────────────────
        [Fact]
        public void DualBroker_Receiving_Quotes_While_Orders_Vetoed()
        {
            var governor = new CanaryVetoLatencyGovernor();        // 500ms 스파이크 임계(기본)
            var interlock = new ZeroLeakVetoInterlock(governor);   // 미무장

            const int ticks = 1_000;
            // 키움 x86 COM pong RTT(≈8~12ms) + 토스 HTTP RTT(≈40~60ms) 이중 수신 모사(정상 레이턴시).
            for (int i = 0; i < ticks; i++)
            {
                governor.Record(8_000 + (i % 5) * 1_000);          // Kiwoom pong RTT ≈ 8~12ms (마이크로초)
                governor.Record(40_000 + (i % 21) * 1_000);        // Toss HTTP RTT ≈ 40~60ms
                // 수신 와중에 가상 발주 인입 → 미무장이므로 Veto.
                Assert.False(interlock.TryAdmit(50_000L, out var r));
                Assert.Equal(VetoReason.Disarmed, r);
            }

            var lat = governor.Snapshot();
            Assert.Equal(ticks * 2, lat.Count);                    // 이중 브로커 표본 모두 집계
            Assert.False(lat.Breached);                            // 정상 레이턴시 — 스파이크 없음
            Assert.True(lat.MaxMicros <= 60_000);                  // 최대 ≤ 60ms
            Assert.Equal(0L, interlock.AdmittedCount);             // ★수신 中에도 발주 사출 0
            Assert.Equal(ticks, interlock.VetoedCount);
        }

        // ── §4 레이턴시 스파이크 → 카나리 진입 차단(융합) ───────────────────────
        [Fact]
        public void LatencySpike_Blocks_Canary_Entry_Even_If_Armed()
        {
            var governor = new CanaryVetoLatencyGovernor(spikeThresholdMicros: 500_000);
            var interlock = new ZeroLeakVetoInterlock(governor);
            interlock.SetArmedForSimulation(true);                 // (시뮬레이션) 무장했다고 가정

            Assert.True(interlock.TryAdmit(10_000L, out _));       // breach 전에는 인가 가능

            governor.Record(750_000);                              // 750ms 스파이크 → breach 래치
            Assert.True(governor.Breached);

            bool ok = interlock.TryAdmit(10_000L, out var reason);
            Assert.False(ok);                                      // ★무장돼 있어도 레이턴시 breach 로 Veto
            Assert.Equal(VetoReason.LatencyBreach, reason);
        }

        // ── §5 변조불가 감사: Veto 누수 미세 틈새 0 ─────────────────────────────
        [Fact]
        public void TamperEvident_Audit_Digest_Matches_Independent_Recompute()
        {
            var interlock = new ZeroLeakVetoInterlock();           // 미무장 → 전량 Disarmed
            const int N = 777;
            var reasons = new VetoReason[N];
            for (int i = 0; i < N; i++)
            {
                interlock.TryAdmit(1_000L, out var r);
                reasons[i] = r;
            }
            // 독립 재계산 다이제스트와 일치 → 모든 Veto 가 정확히 기록됨(누락/변조 0).
            Assert.Equal(ZeroLeakVetoInterlock.ExpectedDigest(reasons), interlock.AuditDigest);
            Assert.Equal(N, interlock.VetoedCount);
            // 사건 1건이라도 누락됐다면(틈새 누수) 다이제스트가 어긋난다 — 음성 대조.
            Assert.NotEqual(ZeroLeakVetoInterlock.ExpectedDigest(new VetoReason[N - 1]), interlock.AuditDigest);
        }

        // ── §6 레이턴시 집계 정확성 ────────────────────────────────────────────
        [Fact]
        public void Latency_Snapshot_Mean_Max_Correct()
        {
            var g = new CanaryVetoLatencyGovernor();
            g.Record(10_000); g.Record(20_000); g.Record(30_000);  // 10·20·30ms
            var s = g.Snapshot();
            Assert.Equal(3, s.Count);
            Assert.Equal(20_000, s.MeanMicros);                    // 평균 20ms
            Assert.Equal(30_000, s.MaxMicros);                     // 최대 30ms
            Assert.False(s.Breached);
        }

        // ── §7 방어적 인가 규칙 ────────────────────────────────────────────────
        [Fact]
        public void NonPositiveNotional_Vetoed_When_Armed()
        {
            var interlock = new ZeroLeakVetoInterlock();
            interlock.SetArmedForSimulation(true);
            Assert.False(interlock.TryAdmit(0L, out var r0));
            Assert.Equal(VetoReason.NonPositiveNotional, r0);
            Assert.False(interlock.TryAdmit(-5L, out var rn));
            Assert.Equal(VetoReason.NonPositiveNotional, rn);
        }

        [Fact]
        public void Armed_And_Healthy_Admits()   // 게이트가 '항상 닫힘'이 아님을 실증(무장 시에만 인가)
        {
            var interlock = new ZeroLeakVetoInterlock();
            interlock.SetArmedForSimulation(true);
            Assert.True(interlock.TryAdmit(10_000L, out var r));
            Assert.Equal(VetoReason.None, r);
            Assert.Equal(1L, interlock.AdmittedCount);
        }
    }

    // ── §8 적대검증(동시 폭주 → 직렬화·δ=0) ──────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Stage1CanaryVetoConcurrencyTests
    {
        [Fact]
        public void Concurrent_OrderBarrage_No_Micro_Gap_Leak()
        {
            // ★다중 스레드 동시 발주 폭주에도 미무장 인터락은 '누수 틈새 0' — admitted 0, vetoed 정확히 총량.
            var interlock = new ZeroLeakVetoInterlock();           // 미무장
            const int threads = 8, perThread = 50_000;
            var ths = new Thread[threads];
            Exception? err = null;
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    try { for (int i = 0; i < perThread; i++) interlock.TryAdmit(10_000L, out _); }
                    catch (Exception e) { err = e; }
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, interlock.AdmittedCount);             // ★단 1건도 누수되지 않음
            Assert.Equal((long)threads * perThread, interlock.VetoedCount);   // 전량 정확히 Veto(카운트 무손실)
        }

        [Fact]
        public void Interlock_TryAdmit_ZeroAllocation()
        {
            // ★Veto 인가 판정 δ=0(GC 0바이트) — 발주 인가 핫패스에 할당 미추가.
            long alloc = 0; Exception? err = null;
            var interlock = new ZeroLeakVetoInterlock();
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) interlock.TryAdmit(10_000L, out _);          // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) interlock.TryAdmit(10_000L, out _);
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        [Fact]
        public void Governor_Record_ZeroAllocation()
        {
            // ★레이턴시 기록 δ=0 — 락/할당 0 → UI 스레드에서 호출해도 GC 정지/블록에 의한 패닉 불가.
            long alloc = 0; Exception? err = null;
            var g = new CanaryVetoLatencyGovernor();
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) g.Record(10_000 + (i & 1023));               // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) g.Record(10_000 + (i & 1023));
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        [Fact]
        public void Governor_Snapshot_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var g = new CanaryVetoLatencyGovernor();
            for (int i = 0; i < 100; i++) g.Record(10_000 + i);
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { var s = g.Snapshot(); if (s.Count < 0) throw new Exception("warm"); }
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) { var s = g.Snapshot(); if (s.Count < 0) throw new Exception("hot"); }
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}

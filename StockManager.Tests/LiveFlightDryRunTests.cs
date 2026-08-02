// ============================================================================
// LiveFlightDryRunTests — v10.1.0 실전 점화 드라이런 & 최종 인증 패브릭 적대적 검증(100% 단언)
//   🛩️ MockExecutionOrderBookMirror: 봉인 경로 통과 후 미러 루프백·슬리피지·부분·RTT·exactly-once·δ=0
//   🛡️ LiveFlightCertificationSealer: 전역 불변식 채점 0~100·100점 봉인·키드-FNV·무장 차단·δ=0
//   🔀 ScopeSealedTransitionBarrier(Think Beyond): 에폭 펜스·퍼지·쿨다운·스테일 거부·δ=0
//   🔒 liveArmed:false: 드라이런 100% 가상 체결이어도 실 브로커 소켓 0비트(실전 경로 end-to-end)
// ============================================================================
using System;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class LiveFlightDryRunTests
    {
        private const long Scale = 100_000_000L;
        private const long Freq  = 10_000_000L;

        private sealed class RecordingBroker : IBroker
        {
            public int Placed;
            public string Name => "REC"; public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
            { Placed++; return (true, "ok"); }
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        // 인증 100점용 건강 프레임(핵심 필드만 세팅·나머지 0=건강).
        private static RingTelemetry HealthyFrame()
            => new RingTelemetry(1L, 0L, 0L, 0L, 0L, 0.0, infraCouplingCount: 117L, liquidityAuthenticityPermille: 1000L);

        // ══ 🛩️ MockExecutionOrderBookMirror ════════════════════════════════
        [Fact]
        public void Mirror_SealAndVirtualFill_Slippage_Partial_ExactlyOnce_Reset()
        {
            var m = new MockExecutionOrderBookMirror(slippageRatioRaw: 100_000L, baseRttNanos: 500_000L);
            Assert.True(m.SealAndVirtualFill(orderId: 1, symbolCode: 5930, side: 0, kind: 1, priceRaw: 100L * Scale, qtyRaw: 5L * Scale));
            Assert.Equal(1L, m.VirtualFills);
            Assert.True(m.LastFillPriceRaw >= 100L * Scale);   // 매수 adverse(≥ 지정가)
            Assert.True(m.LastFilledQtyRaw > 0L);
            Assert.True(m.LastFillRttNanos > 0L);
            Assert.True(m.ResidualVirtualQtyRaw > 0L);

            // ★exactly-once: 동일 orderId 재사출 차단(실전과 동일 봉인 경로).
            Assert.False(m.SealAndVirtualFill(1, 5930, 0, 1, 100L * Scale, 5L * Scale));
            Assert.Equal(1L, m.VirtualFills);
            Assert.Equal(1L, m.DoubleDispatchBlocks);

            // 매도 adverse(≤ 지정가) + 부분 체결.
            Assert.True(m.SealAndVirtualFill(2, 5930, 1, 1, 200L * Scale, 500L * Scale));   // 대량 → 부분
            Assert.True(m.LastFillPriceRaw <= 200L * Scale);
            Assert.True(m.PartialFills >= 1L);

            // ★Reset = 가상 잔량 O(1) 제로화(전환 시 실전 오염 차단).
            m.Reset();
            Assert.Equal(0L, m.ResidualVirtualQtyRaw);
            Assert.Equal(0L, m.LastFilledQtyRaw);
        }

        [Fact]
        public void Mirror_ZeroAllocation()
        {
            var m = new MockExecutionOrderBookMirror();
            for (int i = 0; i < 10_000; i++) m.SealAndVirtualFill(i + 1, 5930, 0, 1, 100L * Scale, Scale);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) m.SealAndVirtualFill(i + 100_000, 5930, 0, 1, 100L * Scale, Scale); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★가상 체결 핫패스 힙 0
        }

        // ══ 🛡️ LiveFlightCertificationSealer ═══════════════════════════════
        [Fact]
        public void Cert_HealthyFrame_100_Seals_UnhealthyFrame_Blocks()
        {
            var sealer = new LiveFlightCertificationSealer(audit: new IgnitionAuditTrailRecorder(nowTicks: () => 1L, tickFrequency: Freq));
            var healthy = HealthyFrame();
            long score = sealer.Certify(in healthy);
            Assert.Equal(100L, score);                  // ★전 항목 PASS
            Assert.True(sealer.SealValid);
            Assert.False(sealer.CertificationBlocking); // 유효 봉인 → 무장 허용
            Assert.NotEqual(0L, sealer.Seal);
            Assert.Equal(0L, sealer.FailMask);
            Assert.True(sealer.TotalItems >= 40L);

            // ★불건강(sendErrors) → <100·봉인 무효·무장 차단.
            var bad = new RingTelemetry(1L, 0L, 0L, 0L, 0L, 0.0, sendErrors: 5L, infraCouplingCount: 117L, liquidityAuthenticityPermille: 1000L);
            sealer.Certify(in bad);
            Assert.True(sealer.Score < 100L);
            Assert.False(sealer.SealValid);
            Assert.True(sealer.CertificationBlocking);  // ★유효 봉인 없으면 무장 즉각 차단
            Assert.NotEqual(0L, sealer.FailMask);
        }

        [Fact]
        public void Cert_KeyedSeal_Deterministic_And_KeyDependent()
        {
            long s1 = LiveFlightCertificationSealer.ComputeSeal(100L, 0x0FFFFL, 1L, 0xABCDEF01UL);
            long s2 = LiveFlightCertificationSealer.ComputeSeal(100L, 0x0FFFFL, 1L, 0xABCDEF01UL);
            Assert.Equal(s1, s2);                                   // 결정론(동일 입력=동일 봉인)
            Assert.NotEqual(s1, LiveFlightCertificationSealer.ComputeSeal(100L, 0x0FFFFL, 1L, 0x99999999UL));   // ★키 의존(위조 방지)
            Assert.NotEqual(s1, LiveFlightCertificationSealer.ComputeSeal(100L, 0x0FFFFL, 2L, 0xABCDEF01UL));   // nonce 의존(리플레이 방지)
        }

        [Fact]
        public void Cert_ZeroAllocation()
        {
            var sealer = new LiveFlightCertificationSealer();
            var f = HealthyFrame();
            for (int i = 0; i < 10_000; i++) sealer.Certify(in f);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) sealer.Certify(in f); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★인증 채점 핫패스 힙 0(로컬 함수 무할당)
        }

        [Fact]
        public void Cert_FreshnessLease_StaleSampler_Blocks_StickyOpen()
        {
            long clock = 1_000_000_000L;
            var sealer = new LiveFlightCertificationSealer(nowTicks: () => clock, tickFrequency: Freq, leaseMillis: 100);
            var healthy = HealthyFrame();
            Assert.Equal(100L, sealer.Certify(in healthy));
            Assert.True(sealer.SealValid);
            Assert.False(sealer.CertificationBlocking);   // 신선 인증 → 무장 허용

            // ★샘플러 정지 모사: Certify 미호출·시계만 리스 창 초과 전진 → sticky-open 방지(latch 는 1이나 stale → fail-closed).
            clock += 2_000_000L;   // 0.2s > 0.1s 리스
            Assert.True(sealer.SealValid);                // 봉인 latch 자체는 유효(마지막 인증 성공)
            Assert.True(sealer.CertificationBlocking);    // ★신선도 만료 → 무장 차단(샘플러 죽으면 stale 인증 fail-closed)

            // 갱신 인증 → 다시 허용(리스 리셋).
            Assert.Equal(100L, sealer.Certify(in healthy));
            Assert.False(sealer.CertificationBlocking);
        }

        // ══ 🔀 ScopeSealedTransitionBarrier (Think Beyond) ═════════════════
        [Fact]
        public void Scope_Transition_Epoch_Purge_Cooldown_StaleReject()
        {
            long clock = 1_000_000_000L;
            int purged = 0;
            var b = new ScopeSealedTransitionBarrier(onPurge: () => purged++, cooldownMillis: 100,
                nowTicks: () => clock, tickFrequency: Freq);
            b.SetInitialMode(ScopeSealedTransitionBarrier.MODE_DRYRUN);
            Assert.True(b.DryRunActive);
            Assert.Equal(ScopeSealedTransitionBarrier.Route.DryRunMirror, b.RouteNow());
            long epoch0 = b.ScopeEpoch;

            // ★전환: 에폭 전진 + 퍼지 + 쿨다운(자체시계 — 외부 ticks 미수령·도메인 강제).
            b.Transition(ScopeSealedTransitionBarrier.MODE_LIVE);
            Assert.Equal(1, purged);                                // ★가상 상태 퍼지
            Assert.True(b.ScopeEpoch > epoch0);                     // ★에폭 전진(스코프 식별)
            Assert.Equal(ScopeSealedTransitionBarrier.Route.Bounce, b.RouteNow());   // ★쿨다운 중 Bounce(유령 폭주 차단)
            Assert.True(b.TransitionBlocking);

            // ★스테일 에폭(전환 이전 스코프 주문) → 튕김. 쿨다운 후에도.
            clock += 2_000_000L;   // 0.2s > 0.1s 쿨다운
            Assert.False(b.TransitionBlocking);
            Assert.Equal(ScopeSealedTransitionBarrier.Route.LiveBroker, b.RouteNow());   // 쿨다운 후 Live
            Assert.False(b.VerifyScope(epoch0));                    // ★전환 이전 스코프 → 물리 튕김
            Assert.True(b.VerifyScope(b.ScopeEpoch));               // 현재 스코프 → 통과
            Assert.True(b.StaleRejects >= 1L);
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_DryRun_RealDrainPath_NoBrokerLeak()
        {
            // ★실전 프로덕션 경로(OrderDrainWorker.DrainOnce→Dispatch): 드라이런 스코프면 실 브로커 대신 미러로 봉인 루프백 — broker.Place 0.
            long clock = 1_000_000_000L;
            var att   = new OrderAttestationService();
            var ring  = new OrderDispatchRing(64);
            var disp  = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att);   // 드라이런 급전 시뮬(armed)
            var broker = new RecordingBroker();
            var mirror = new MockExecutionOrderBookMirror();
            var barrier = new ScopeSealedTransitionBarrier(onPurge: () => mirror.Reset(), nowTicks: () => clock, tickFrequency: Freq);
            barrier.SetInitialMode(ScopeSealedTransitionBarrier.MODE_DRYRUN);
            using var worker = new OrderDrainWorker(disp, broker, wired: false, mockMirror: mirror, scopeBarrier: barrier);

            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)1, QPrice.Of(100m), QQty.Of(5m), 100);
            worker.DrainOnce();
            Assert.Equal(0, broker.Placed);            // ★드라이런 100% 가상 체결이어도 실 브로커 소켓 0비트
            Assert.True(mirror.VirtualFills >= 1L);    // 미러 가상 체결 성립

            // ★전환 쿨다운 중 → Bounce(어느 소켓으로도 0).
            barrier.Transition(ScopeSealedTransitionBarrier.MODE_LIVE);
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)1, QPrice.Of(100m), QQty.Of(3m), 101);
            worker.DrainOnce();
            Assert.Equal(0, broker.Placed);            // ★전환 쿨다운 — 실 사출 0(유령 폭주 차단)
            Assert.Equal(0L, mirror.ResidualVirtualQtyRaw);   // ★전환 퍼지 — 가상 잔량 오염 0
        }
    }
}

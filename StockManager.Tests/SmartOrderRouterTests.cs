// ============================================================================
// SmartOrderRouterTests — v9.0.0 SOR & 빙산호가 포획 적대적 검증(100% 단언)
//   🎯 SmartOrderRouterEngine: 알파/스프레드 라우팅·슬라이싱·δ=0·fail-closed
//   🧊 IcebergHunterSentinel + ReloadSignatureDetector: 재전개 서명·숨은 물량·δ=0
//   📐 QueuePositionEstimator: FIFO 큐 위치·체결 임접률·δ=0
//   🔥 LiquidityAuthenticityVerifier(Think Beyond): 진짜 빙산 vs 가짜 스푸핑·δ=0
//   🔒 liveArmed:false: 강한 라우팅이어도 이중키 전 발주 0
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class SmartOrderRouterTests
    {
        private const long Freq = 10_000_000L;
        private static long[] L(params long[] v) => v;
        // 10호가 헬퍼(내림 매수·오름 매도).
        private static long[] BidPx() => L(1000, 999, 998, 997, 996, 995, 994, 993, 992, 991);
        private static long[] AskPx() => L(1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009, 1010);
        private static long[] Qty(long q) => L(q, q, q, q, q, q, q, q, q, q);

        // ══ 📐 QueuePositionEstimator ═══════════════════════════════════════
        [Fact]
        public void Queue_Place_Trade_Cancel_Imminence()
        {
            var q = new QueuePositionEstimator();
            q.Place(aheadQtyRaw: 1000, ticks: 100);
            Assert.True(q.Active);
            Assert.Equal(0L, q.FillImminencePermille);      // 아직 전량 앞
            q.OnTrade(400);                                 // 400 소진 → 앞 600
            Assert.Equal(600L, q.RemainingAheadRaw);
            Assert.Equal(400L, q.FillImminencePermille);    // 40% 소진
            q.OnCancelAhead(300);                           // 앞 300 취소 → 앞 300
            Assert.Equal(300L, q.RemainingAheadRaw);
            q.OnTrade(500);                                 // 300 소진(포화) → 앞 0
            Assert.Equal(0L, q.RemainingAheadRaw);
            Assert.Equal(1000L, q.FillImminencePermille);   // ★다음 체결이 내 것
            q.Clear(); Assert.False(q.Active);
        }

        [Fact]
        public void Queue_ZeroAllocation()
        {
            var q = new QueuePositionEstimator();
            q.Place(1_000_000, 0);
            for (int i = 0; i < 10_000; i++) { q.OnTrade(1); _ = q.FillImminencePermille; }
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) { q.OnTrade(1); _ = q.FillImminencePermille; _ = q.RemainingAheadRaw; } return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));
        }

        // ══ 🧊 Iceberg reload + hunter ══════════════════════════════════════
        [Fact]
        public void Iceberg_ReloadSignature_And_HiddenVolume()
        {
            long now = 1_000_000;
            var det = new IcebergReloadSignatureDetector(reloadThresholdMicros: 500, tickFrequency: Freq);
            det.Observe(100, now);                          // 기준
            for (int k = 0; k < 4; k++)                     // 4회 dip→즉시 재전개(빙산 서명)
            {
                now += Freq / 100; det.Observe(50, now);    // dip(체결)
                now += Freq / 5000; det.Observe(100, now);  // 즉시 재전개(2000ticks ≤ 5000 임계·100≥80)
            }
            Assert.True(det.Reloads >= 4);
            Assert.True(det.ConfirmationPermille > 0);

            // Hunter: 체결과 함께 재전개 반복 → 빙산 포획·숨은 물량 추정.
            long t = 1_000_000;
            var hunter = new IcebergHunterSentinel(confirmThresholdPermille: 300, reloadThresholdMicros: 500, tickFrequency: Freq);
            hunter.ObserveAsk(100, 0, t);
            for (int k = 0; k < 6; k++)
            {
                t += Freq / 100; hunter.ObserveAsk(40, 60, t);     // 60 체결 → dip
                t += Freq / 5000; hunter.ObserveAsk(100, 0, t);    // 즉시 재전개
            }
            Assert.True(hunter.AskIcebergActive);
            Assert.True(hunter.EstimatedHiddenAskQtyRaw > 0);      // ★숨은 물량 역추적
            Assert.True(hunter.IcebergsDetected >= 1);
        }

        [Fact]
        public void Iceberg_Hunter_ZeroAllocation()
        {
            long t = 1_000_000;
            var hunter = new IcebergHunterSentinel(tickFrequency: Freq);
            for (int i = 0; i < 10_000; i++) { t += 100; hunter.ObserveBid(100, 1, t); }
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); long tt = t; for (int i = 0; i < 200_000; i++) { tt += 100; hunter.ObserveBid(100, 1, tt); } return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));
        }

        // ══ 🔥 LiquidityAuthenticityVerifier (Think Beyond) ═════════════════
        [Fact]
        public void Authenticity_RealIceberg_vs_Spoof()
        {
            long t = 1_000_000;
            var v = new LiquidityAuthenticityVerifier(windowSeconds: 2, spoofRatioPermille: 3000, tickFrequency: Freq);
            // 매도측: 체결 지배(진짜 유동성) → 진위 높음·비스푸핑·라우팅 허용.
            for (int k = 0; k < 10; k++) { t += Freq / 10; v.RecordAsk(fillQty: 100, cancelQty: 10, ticks: t); }
            Assert.True(v.AskAuthenticityPermille >= 800);
            Assert.False(v.AskSpoofed);
            Assert.True(v.AllowRoute(isBuy: true));          // 매수는 매도 유동성 히트 → 진짜라 허용

            // 매도측: 취소 지배(허수 스푸핑) → 스푸핑·라우팅 차단.
            long t2 = 5_000_000;
            var s = new LiquidityAuthenticityVerifier(windowSeconds: 2, spoofRatioPermille: 3000, tickFrequency: Freq);
            for (int k = 0; k < 10; k++) { t2 += Freq / 10; s.RecordAsk(fillQty: 5, cancelQty: 500, ticks: t2); }
            Assert.True(s.AskSpoofed);                       // ★취소>>체결 = 스푸핑
            Assert.True(s.AskAuthenticityPermille < 200);
            Assert.False(s.AllowRoute(isBuy: true));         // ★가짜 → 차단
            Assert.True(s.SpoofBlocks >= 1);
        }

        [Fact]
        public void Authenticity_ZeroAllocation()
        {
            long t = 1_000_000;
            var v = new LiquidityAuthenticityVerifier(tickFrequency: Freq);
            for (int i = 0; i < 10_000; i++) { t += 100; v.RecordAsk(100, 10, t); }
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); long tt = t; for (int i = 0; i < 200_000; i++) { tt += 100; v.RecordAsk(100, 10, tt); _ = v.AllowRoute(true); } return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));
        }

        // ══ 🎯 SmartOrderRouterEngine ═══════════════════════════════════════
        [Fact]
        public void Sor_Routes_Directional_Aggressive_And_FailClosed()
        {
            var sor = new SmartOrderRouterEngine(alphaThresholdMilli: 50, aggressiveMilli: 500, tightSpreadPermille: 20);
            // 강한 매수 알파 + 타이트 스프레드(1/1000≈1‰≤20) → 공격적 시장가 라우팅.
            var r = sor.ComputeRoute(0.8f, BidPx(), Qty(100), AskPx(), Qty(100), targetQtyRaw: 1000);
            Assert.True(r.Route);
            Assert.Equal(0, r.SideByte);                    // buy
            Assert.Equal(0, r.KindByte);                    // market(공격적)
            Assert.True((r.RoutingCode & SmartOrderRouterEngine.R_MARKET_AGGRESSIVE) != 0);
            Assert.True(r.Slices >= 1);

            // 약한 알파 → 무라우팅(fail-closed).
            var none = sor.ComputeRoute(0.01f, BidPx(), Qty(100), AskPx(), Qty(100), 1000);
            Assert.False(none.Route);
            Assert.True((none.RoutingCode & SmartOrderRouterEngine.R_NO_ALPHA) != 0);

            // 불완전 호가 → 무라우팅.
            Assert.False(sor.ComputeRoute(0.8f, Array.Empty<long>(), Qty(100), AskPx(), Qty(100), 1000).Route);
            // NaN 알파 → 무라우팅.
            Assert.False(sor.ComputeRoute(float.NaN, BidPx(), Qty(100), AskPx(), Qty(100), 1000).Route);
        }

        [Fact]
        public void Sor_SpoofBlocked_And_IcebergAbsorb()
        {
            // 스푸핑 매도측 → 매수 라우팅 차단.
            long t = 1_000_000;
            var auth = new LiquidityAuthenticityVerifier(spoofRatioPermille: 3000, tickFrequency: Freq);
            for (int k = 0; k < 10; k++) { t += Freq / 10; auth.RecordAsk(5, 500, t); }
            var sorS = new SmartOrderRouterEngine(auth: auth, alphaThresholdMilli: 50);
            var blocked = sorS.ComputeRoute(0.9f, BidPx(), Qty(100), AskPx(), Qty(100), 1000);
            Assert.False(blocked.Route);
            Assert.True((blocked.RoutingCode & SmartOrderRouterEngine.R_SPOOF_BLOCKED) != 0);
            Assert.True(sorS.SpoofBlocks >= 1);

            // 빙산 숨은 물량 → 흡수 슬라이스 확대.
            long ti = 1_000_000;
            var hunter = new IcebergHunterSentinel(confirmThresholdPermille: 300, reloadThresholdMicros: 500, tickFrequency: Freq);
            hunter.ObserveAsk(100, 0, ti);
            for (int k = 0; k < 6; k++) { ti += Freq / 100; hunter.ObserveAsk(40, 60, ti); ti += Freq / 5000; hunter.ObserveAsk(100, 0, ti); }
            var sorI = new SmartOrderRouterEngine(iceberg: hunter, alphaThresholdMilli: 50, aggressiveMilli: 5000, tightSpreadPermille: 1);   // 비공격(지정가)
            var r = sorI.ComputeRoute(0.2f, BidPx(), Qty(50), AskPx(), Qty(50), 100_000);
            Assert.True(r.Route);
            Assert.True((r.RoutingCode & SmartOrderRouterEngine.R_ICEBERG_ABSORB) != 0);   // ★빙산 흡수
            Assert.True(sorI.HiddenIcebergTotalRaw > 0);
        }

        [Fact]
        public void Sor_ComputeRoute_ZeroAllocation()
        {
            var sor = new SmartOrderRouterEngine(alphaThresholdMilli: 50);
            long[] bp = BidPx(), bq = Qty(100), ap = AskPx(), aq = Qty(100);
            for (int i = 0; i < 10_000; i++) sor.ComputeRoute(0.8f, bp, bq, ap, aq, 1000);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) sor.ComputeRoute(0.8f, bp, bq, ap, aq, 1000); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★라우팅 계산 핫패스 힙 0
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_StrongRoute_NoEmission_UntilArmed()
        {
            // ★SOR 이 강한 라우팅 계획을 산출해도, 그것을 제안으로 바꿔 미무장 디스패처로 드레인하면 onSend 0.
            var sor = new SmartOrderRouterEngine(alphaThresholdMilli: 50);
            var r = sor.ComputeRoute(0.95f, BidPx(), Qty(100), AskPx(), Qty(100), 1000);
            Assert.True(r.Route);                            // 강한 라우팅

            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => false, attestation: att);   // 미무장
            // 라우팅 계획 → 제안 발행(SOR 은 ring 참조 0 — 외부 생산자가 변환).
            ring.Publish(genomeId: 0, symbolCode: 5930, r.SideByte == 0 ? OrderSide.Buy : OrderSide.Sell,
                         r.KindByte, QPrice.Of(100m), QQty.Of(1m), 100);
            int forwarded = 0;
            disp.Drain(_ => forwarded++);
            Assert.Equal(0, forwarded);                      // ★onSend 미호출 — 라우팅이 아무리 강해도 이중키 전 발주 0
            Assert.Equal(0L, disp.Forwarded);
            Assert.True(disp.GatedOut >= 1);
        }
    }
}

// ============================================================================
// LiveAccountingMeshTests — v4.96.0: 실전 자본 영점 조절 및 실시간 청산 메쉬
//
//  §1 수수료/세금 매트릭스     — 국내 매수(수수료)·매도(수수료+거래세+농특세 분리)·해외 혼합·코인.
//  §2 정밀 잔여 캐리 가드       — 절사 잔차 이월로 누적오차 0 수렴(naive floor 대비).
//  §3 2단계 청산·예수금 정합    — 매수 차감·매도 credit·예수금 보존.
//  §4 fail-closed                — 예수금 초과 인출·명목 오버플로 → ForceFreeze.
//  §5(별도) δ=0 OnFill·링 추월 fail-closed(ConcurrencyStress).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class LiveAccountingMeshTests
    {
        private static long P(decimal price) => QPrice.Of(price).Raw;
        private static long Q(decimal qty) => QQty.Of(qty).Raw;
        private static long M(decimal won) => QMoney.Of(won).Raw;

        private static bool SpinUntil(Func<bool> cond, int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(2); }
            return cond();
        }

        // ── §1 수수료/세금 매트릭스 ────────────────────────────────────────────

        [Fact]
        public void Domestic_Buy_CommissionOnly_NoTax()
        {
            using var m = new LiveAccountingMesh(M(1_000_000_000m));
            m.Start();
            // 명목 1,000,000원(가격 100 × 수량 10,000). 수수료 0.015% = 150원. 매수엔 세금 없음.
            Assert.True(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(10_000m), 1));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.Equal(M(150m), m.CumFeeKrwRaw);      // 수수료 150원
            Assert.Equal(0L, m.CumStampTaxRaw);         // 거래세 0(매수)
            Assert.Equal(0L, m.CumFarmTaxRaw);          // 농특세 0(매수)
            Assert.False(m.IsFrozen);
        }

        [Fact]
        public void Domestic_Sell_Commission_Plus_StampAndFarmTax_Separated()
        {
            using var m = new LiveAccountingMesh(M(1_000_000_000m));
            m.Start();
            m.SeedPosition(5930, Q(20_000m), M(2_000_000m));   // 사전 보유 적재(유령 대금 방지 가드 대응)
            // 명목 1,000,000원 매도: 수수료 0.015%=150·거래세 0.05%=500·농특세 0.15%=1500 → 총 2150원.
            Assert.True(m.OnFill(5930, OrderSide.Sell, AssetKind.Domestic, P(100m), Q(10_000m), 1));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.Equal(M(500m), m.CumStampTaxRaw);    // ★거래세 분리
            Assert.Equal(M(1500m), m.CumFarmTaxRaw);    // ★농특세 분리
            Assert.Equal(M(2150m), m.CumFeeKrwRaw);     // 합산 총 제비용
        }

        [Fact]
        public void Foreign_MixedFee_RatioPlusFixedCents_BothSides()
        {
            using var m = new LiveAccountingMesh(M(1_000_000_000m), seedUsdRaw: M(1_000_000_000m));   // USD 예수금 시드
            m.Start();
            // 해외 매수 $10,000(가격 100 × 100): 비율 0.025%=$2.5 + 고정 $0.01 = $2.51.
            Assert.True(m.OnFill(1, OrderSide.Buy, AssetKind.Foreign, P(100m), Q(100m), 1));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.Equal(M(2.51m), m.CumFeeUsdRaw);     // ★혼합(비율+센트 고정)
            Assert.Equal(0L, m.CumFeeKrwRaw);           // KRW 미변동
        }

        // ── §2 정밀 잔여 캐리 가드 ─────────────────────────────────────────────

        [Fact]
        public void CarryGuard_ConvergesCumulativeToExact_NotNaiveFloorZero()
        {
            using var m = new LiveAccountingMesh(long.MaxValue / 4);
            m.Start();
            // 명목 333raw(가격 333raw × 수량 1.0)·수수료율 0.00015 → 건당 prod=333×15000=4,995,000, floor=0, 잔여 4,995,000.
            //   naive(건당 floor)면 100건 누적 = 0. 캐리 가드면 floor(100×4,995,000/10^8)=floor(4.995)=4 로 수렴.
            for (int i = 0; i < 100; i++) Assert.True(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, 333L, Q(1m), i));
            Assert.True(SpinUntil(() => m.FillsFinalized == 100));
            Assert.Equal(4L, m.CumFeeKrwRaw);           // ★누적오차 0 수렴(naive 0 이 아님)
            Assert.Equal(99_500_000L, m.CarryErrorRaw); // 잔여 이월분(< Scale)
            Assert.True(m.CarryErrorRaw < 100_000_000L);
        }

        // ── §3 2단계 청산 · 예수금 정합 ────────────────────────────────────────

        [Fact]
        public void Buy_Then_Sell_DepositConservation()
        {
            long seed = M(10_000_000m);
            using var m = new LiveAccountingMesh(seed);
            m.Start();
            // 매수 1,000,000원 명목 + 수수료 150 → 예수금 = seed - 1,000,150.
            Assert.True(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(10_000m), 1));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.Equal(seed - M(1_000_150m), m.AvailableDepositRaw);
            // 동일 명목 매도: 대금 +1,000,000, 제비용 2150 → 예수금 += (1,000,000 - 2150).
            Assert.True(m.OnFill(5930, OrderSide.Sell, AssetKind.Domestic, P(100m), Q(10_000m), 2));
            Assert.True(SpinUntil(() => m.FillsFinalized == 2));
            Assert.Equal(seed - M(1_000_150m) + M(1_000_000m) - M(2_150m), m.AvailableDepositRaw);
            Assert.False(m.IsFrozen);
        }

        [Fact]
        public void Buy_UpdatesBook_AvgPrice()
        {
            using var m = new LiveAccountingMesh(M(1_000_000_000m));
            m.Start();
            m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(10m), 1);
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.True(m.TryGetAvgPrice(5930, out long avg));
            // 평단 = (명목 1000 + 수수료)/수량 10. 명목 1000 + 수수료 floor(1000×0.00015)=0(작아서) → ~100원.
            Assert.True(avg >= P(100m));   // 수수료 포함이라 매수단가 이상
        }

        // ── §4 fail-closed ─────────────────────────────────────────────────────

        [Fact]
        public void OverSpend_ExceedsDeposit_Freezes()
        {
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(M(100m), att);   // 시드 100원
            m.Start();
            // 명목 1,000,000원 매수 → 예수금 100원 초과 인출 → fail-closed 동결.
            Assert.False(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(10_000m), 1));
            Assert.True(m.IsFrozen);
            Assert.Equal(AccountingState.Frozen, m.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void PoisonFill_NegativePriceOrZeroQty_Freezes()
        {
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(M(1_000_000m), att);
            m.Start();
            Assert.False(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, -1L, Q(1m), 1));
            Assert.True(m.IsFrozen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void SellExceedsHeld_PhantomCash_Freezes()
        {
            // ★적대검증 회귀(C#2): 보유 없는/부족한 매도가 명목 전액을 credit 하는 유령 현금 → fail-closed 동결.
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(M(1_000_000_000m), att);
            m.Start();
            Assert.True(m.OnFill(5930, OrderSide.Sell, AssetKind.Domestic, P(100m), Q(10_000m), 1));   // OnFill 수락(핫)
            Assert.True(SpinUntil(() => m.IsFrozen));                    // 회계 확정 시 보유 초과 → 동결
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FullInvest_SellToRaiseCash_DoesNotFalseFreeze()
        {
            // ★적대검증 회귀(C#3): 가용이 수수료보다 작은 풀-인베스트 상태에서 현금 확보용 매도가 오탐 동결되면 안 됨.
            long seed = M(1_000_150m);   // 매수 1건(명목 1,000,000+수수료 150) 후 가용 ≈ 0.
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(seed, att);
            m.Start();
            Assert.True(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(10_000m), 1));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.Equal(0L, m.AvailableDepositRaw);                     // 가용 소진(수수료보다 작음)
            // 이 상태에서 매도 → 핫패스가 수수료 선차감하지 않으므로 오탐 동결 없이 수락·정산.
            Assert.True(m.OnFill(5930, OrderSide.Sell, AssetKind.Domestic, P(100m), Q(10_000m), 2));
            Assert.True(SpinUntil(() => m.FillsFinalized == 2));
            Assert.False(m.IsFrozen);                                    // ★오탐 동결 없음
            // 명목 상쇄(동일 매수/매도) → 순변동 = -매수수수료(150) -매도세(2150) = seed - 2300.
            Assert.Equal(seed - M(2_300m), m.AvailableDepositRaw);
        }

        [Fact]
        public void RejectedFill_ZeroQty_NoOp_NoFreeze()
        {
            // ★적대검증 회귀(C#5): OrderRejected(FillQty=0)는 회계 이벤트 아님 — 동결 없이 no-op(freeze-storm 방지).
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(M(1_000_000m), att);
            m.Start();
            var reject = new FillPayload { OrderId = 1, SymbolCode = 5930, FillPrice = 100.0, FillQty = 0, Side = +1, TimestampTicks = 1 };
            Assert.False(m.OnFillPayload(reject));
            Assert.False(m.IsFrozen);                                    // ★동결 안 됨
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void SeparateTaxCarry_ConvergesPerItem()
        {
            // ★적대검증 회귀(C#1): 거래세/농특세 항목별 독립 캐리 → 각 세목 누적이 자기값으로 0 수렴(공유 캐리 오귀속 없음).
            using var m = new LiveAccountingMesh(long.MaxValue / 4);
            m.Start();
            m.SeedPosition(5930, Q(1_000_000m), M(1_000_000m));
            // 명목 333raw 매도 1000건. 거래세율 50000·농특세율 150000 → 각 항목 독립 spill.
            for (int i = 0; i < 1000; i++) Assert.True(m.OnFill(5930, OrderSide.Sell, AssetKind.Domestic, 333L, Q(1m), i));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1000, 10000));
            // 거래세 = floor(1000×333×50000/10^8)=floor(166.5)=166, 농특세=floor(1000×333×150000/10^8)=floor(499.5)=499.
            Assert.Equal(166L, m.CumStampTaxRaw);
            Assert.Equal(499L, m.CumFarmTaxRaw);
        }

        [Fact]
        public void ExternalFreeze_ReflectedInState_SuppressesGreenIndicator()
        {
            // ★적대검증 회귀(C#8): 타 서브시스템 ForceFreeze 시 State 가 Frozen 반영 → 거짓 녹색 인디케이터 억제.
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(M(1_000_000m), att);
            m.Start();
            Assert.Equal(AccountingState.Active, m.State);
            att.ForceFreeze("타 서브시스템 동결");                       // 메쉬가 아닌 외부가 동결
            Assert.Equal(AccountingState.Frozen, m.State);               // ★외부 동결 반영
            Assert.True(m.IsFrozen);
            Assert.False(m.DepositZeroSynced);                           // 녹색 영점 인디케이터 억제
        }

        [Fact]
        public void AlreadyFrozen_RejectsFill()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("외부 동결");
            using var m = new LiveAccountingMesh(M(1_000_000m), att);
            m.Start();
            Assert.False(m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(1m), 1));
        }

        // ── liveArmed:false ────────────────────────────────────────────────────

        [Fact]
        public void LiveArmed_AccountingNeverEnablesLiveRouting()
        {
            // ★메쉬는 내부 원장만 영점 조절 — IBroker/소켓 참조 0. 하류 무장 술어는 여전히 수학적 0.
            var att = new OrderAttestationService();
            using var m = new LiveAccountingMesh(M(1_000_000_000m), att);
            m.Start();
            for (int i = 0; i < 50; i++) m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, P(100m), Q(1m), i);
            Assert.True(SpinUntil(() => m.FillsFinalized == 50));
            Assert.Equal(GatingMode.Normal, att.Mode);          // 정상 정산은 동결 없음
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.False(DispatchArming.ResolvePredicate(wired: false, gate)());   // 실주문 술어 수학적 0
        }

        [Fact]
        public void OnFillPayload_ConvertsAndSettles()
        {
            using var m = new LiveAccountingMesh(M(1_000_000_000m));
            m.Start();
            var fp = new FillPayload { OrderId = 1, SymbolCode = 5930, FillPrice = 100.0, FillQty = 10_000, Side = +1, TimestampTicks = 1 };
            Assert.True(m.OnFillPayload(fp));
            Assert.True(SpinUntil(() => m.FillsFinalized == 1));
            Assert.Equal(M(150m), m.CumFeeKrwRaw);
        }
    }

    // ── §5 δ=0 OnFill · 링 추월 fail-closed (부하/타이밍 민감 → 직렬화 컬렉션) ───
    [Collection("ConcurrencyStress")]
    public sealed class LiveAccountingMeshConcurrencyTests
    {
        [Fact]
        public void OnFill_HotPath_ZeroAllocation()
        {
            // ★핫패스 OnFill(δ=0): 회계 스레드 미기동(링 발행만) — CAS 델타 + QMath 정수 + RingBufferCore 슬롯 복사, 힙 0.
            var m = new LiveAccountingMesh(long.MaxValue / 4);   // 거대 시드(고갈 없음)
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    long priceRaw = QPrice.Of(100m).Raw, qtyRaw = QQty.Of(1m).Raw;
                    for (int i = 0; i < 300_000; i++) m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, priceRaw, qtyRaw, i);   // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, priceRaw, qtyRaw, i);
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★OnFill 힙 할당 0(δ=0)
            m.Dispose();
        }

        [Fact]
        public void RingOverrun_UndrainedFills_FailClosed()
        {
            // 회계 스레드 미기동 상태로 용량 초과 발행 → Start 시 추월(누락) 감지 → fail-closed 동결.
            var att = new OrderAttestationService();
            var m = new LiveAccountingMesh(long.MaxValue / 4, att, capacity: 8);
            long priceRaw = QPrice.Of(100m).Raw, qtyRaw = QQty.Of(1m).Raw;
            for (int i = 0; i < 40; i++) m.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, priceRaw, qtyRaw, i);   // 용량 8 초과(추월)
            m.Start();   // 회계 스레드 드레인 → Dropped>0 감지 → 동결
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && !m.IsFrozen) Thread.Sleep(2);
            Assert.True(m.IsFrozen);                              // ★누락 감지 fail-closed
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(m.DroppedFills > 0);
            m.Dispose();
        }
    }
}

// ============================================================================
// RealTimeBasketNetter — v5.4.0 과제2: 실시간 포지션 네팅 & 바스켓 리밸런서
//
//  복수 전략이 동일 틱·동일 심볼에서 경합 격발한 대향 주문(A 삼성전자 +100 매수, B -100 매도)을 브로커 사출 직전
//  나노초 인터리빙 대조로 내부 상쇄(Netting)한다. 상쇄분(Crossed)은 물리 사출 0 — 왕복 수수료·거래세 중복 누수를
//  차단하고, 상쇄 후 남은 순 잔여(Residual)만 하류 50ms 드레인 링으로 사출된다. 각 전략 장부는 자기 의도대로 가상 갱신.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  QMath.Notional(Int128 중간·checked) 로 명목·절감 산술 재사용. StrategyIntent(mesh 팬아웃 산물)를 스팬으로 순회 —
//  힙 0. 기존 네팅/상쇄 로직은 코드베이스에 전무(MultiBrokerShadowRing.ExecutePair 는 페어 발주·롤백이지 상쇄 아님) → 신설.
//
//  δ=0: Net 은 ReadOnlySpan 순회 + 정수 산술 + Interlocked 누적(new/박싱/LINQ/클로저 0). 결정론: 벽시계/RNG 무사용,
//  refPrice·정수 bps 만. 상쇄 절감은 '상쇄 명목 × 왕복 비용 bps' 반사실 추정(실 체결 회피분).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>동일 심볼·동일 틱 대향 주문 상쇄 결과(blittable·δ=0). 잔여만 물리 사출.</summary>
    public readonly struct NetResult
    {
        public readonly long ResidualSignedQtyRaw;   // 잔여 순 물리 주문(+매수/-매도/0)
        public readonly long CrossedQtyRaw;          // 내부 상쇄 수량(양방향 min) — 물리 사출 안 됨(수수료·세 절감)
        public readonly long GrossBuyQtyRaw;         // 총 매수 의도
        public readonly long GrossSellQtyRaw;        // 총 매도 의도
        public readonly long SavedCostRaw;           // 상쇄분 왕복 수수료+세 절감(raw)

        public NetResult(long residual, long crossed, long grossBuy, long grossSell, long savedCost)
        {
            ResidualSignedQtyRaw = residual; CrossedQtyRaw = crossed;
            GrossBuyQtyRaw = grossBuy; GrossSellQtyRaw = grossSell; SavedCostRaw = savedCost;
        }

        public OrderSide ResidualSide => ResidualSignedQtyRaw >= 0 ? OrderSide.Buy : OrderSide.Sell;
        public long ResidualAbsQtyRaw => ResidualSignedQtyRaw >= 0 ? ResidualSignedQtyRaw : -ResidualSignedQtyRaw;
    }

    /// <summary>실시간 바스켓 네터 — 대향 주문 δ=0 내부 상쇄, 잔여만 하류. 왕복 비용 절감 계량.</summary>
    public sealed class RealTimeBasketNetter
    {
        // 상쇄 1주당 절감되는 왕복 거래비용(bps·정수·결정론). 한국 관례 근사: 거래세 0.18%(매도) + 왕복 수수료 ≈ 46bps.
        private readonly long _roundTripCostBpsRaw;   // bps 를 raw 분수로: bps × Scale / 10000
        private long _netEvents, _crossedQtyCum, _grossQtyCum, _savedCostCum;

        public RealTimeBasketNetter(long roundTripCostBps = 46)
        {
            long bps = roundTripCostBps < 0 ? 0 : roundTripCostBps;
            _roundTripCostBpsRaw = bps * (QMath.Scale / 10000L);   // 46bps → 0.0046 raw
        }

        public long NetEvents       => Interlocked.Read(ref _netEvents);
        public long CrossedQtyCum   => Interlocked.Read(ref _crossedQtyCum);
        public long GrossQtyCum     => Interlocked.Read(ref _grossQtyCum);
        public long SavedCostRawCum => Interlocked.Read(ref _savedCostCum);

        /// <summary>
        /// 네팅 절감 효율 bps = 시장 회피 물량(2×상쇄=매수측+매도측) / 누적 총물량(매수+매도) × 10000(콜드 읽기·텔레메트리).
        /// 완전 상쇄(A+100/B-100)=100%, 동일측 전량=0%. 2×min(a,b) ≤ a+b 이므로 ≤10000(100%) 유계.
        /// </summary>
        public long NettingEfficiencyBps
        {
            // ★적대검증 교정 — crossedCum×20000 은 long 오버플로 가능(누적). Int128 중간(QMath 관용구). 비율은 ≤10000 이라 long 복귀 안전.
            get { long g = Interlocked.Read(ref _grossQtyCum); return g <= 0 ? 0L : (long)((Int128)Interlocked.Read(ref _crossedQtyCum) * 20000L / g); }
        }

        /// <summary>
        /// ★δ=0 순수 상쇄 — intents 스팬을 훑어 총 매수/매도·상쇄(min)·잔여(순)·왕복 비용 절감을 산출(힙 0).
        /// 잔여 부호가 순 방향(+매수/-매도). 상쇄분은 물리 사출되지 않으므로 왕복 수수료·세가 절감된다.
        /// </summary>
        public NetResult Net(ReadOnlySpan<StrategyIntent> intents, long refPriceRaw)
        {
            long buy = 0, sell = 0;
            for (int i = 0; i < intents.Length; i++)
            {
                long s = intents[i].SignedQtyRaw;
                if (s > 0) buy = checked(buy + s);
                else if (s < 0) sell = checked(sell - s);   // -s = |매도|
            }
            long crossed  = buy < sell ? buy : sell;        // 양방향 상쇄 = min
            long residual = buy - sell;                     // 순 잔여(signed)
            long gross    = checked(buy + sell);

            // 절감 = 상쇄 명목 × 왕복 비용 bps. refPrice 로 명목화(refPrice≤0 이면 계량 생략).
            long saved = 0L;
            if (crossed > 0 && refPriceRaw > 0)
            {
                long crossedNotional = QMath.Notional(crossed, refPriceRaw, roundUp: false);
                saved = QMath.Notional(crossedNotional, _roundTripCostBpsRaw, roundUp: false);
            }

            Interlocked.Increment(ref _netEvents);
            Interlocked.Add(ref _grossQtyCum, gross);
            if (crossed > 0) { Interlocked.Add(ref _crossedQtyCum, crossed); Interlocked.Add(ref _savedCostCum, saved); }
            return new NetResult(residual, crossed, buy, sell, saved);
        }
    }
}

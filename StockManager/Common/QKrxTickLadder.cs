// ============================================================================
// QKrxTickLadder — 과제 2: 자산별 QTickSpec 완성 (KRX 가격대별 호가격자 + 해외 USD 격자/FX)
//
//  S27-e BuiltInTickSpecs 의 'symbol→단일 QTickSpec' 근사 한계를 보완한다. KRX 호가단위는 심볼당 단일이
//  아니라 '현재 가격대'에 따라 계단식(1/5/10/50/100/500/1000원)으로 바뀌므로, 가격을 받아 그 가격대의
//  호가단위를 결정하는 '격자 사다리(ladder)'가 필요하다.
//
//  ── 설계 결정(정직 표기) ────────────────────────────────────────────────────
//   · 기존 sealed 시그니처(QTickRegistry/BuiltInTickSpecs/ShadowCutoverGate/QFx*) 는 일절 건드리지 않는다
//     (recon 의존). 본 파일은 '추가만' 한다 — CS0101 이름충돌·CS8019 미사용 using 회피.
//   · KrxTickLadder 는 순수 함수(가격대→호가단위). 무할당·결정론(테이블 룩업, DateTime 무관). KRX 유가/코스닥
//     호가규칙(2023 개편 반영 근사)을 정적 테이블로 둔다.
//   · ResolveSpec(priceDec) 은 '현재 가격대'의 QTickSpec 을 매 호출 산출 → BuiltInTickSpecs 의 등록시점
//     근사가 가격대 이탈 시 fail-closed 거부되는 함정을, '가격대별 정확 격자'로 대체할 경로를 제공한다.
//   · USD 해외주식은 0.01(센트) 고정 단일격자라 사다리 불요 — Usd() 그대로 재사용.
//   · ★보수 편향은 QPrice.ConservativeAlign(매수 ceil·매도 floor)·QQty.AlignDown 이 이미 담당. 본 파일은
//     '어떤 tick 으로 정렬할지'(격자 선택)만 정한다 — 정렬 방향/FX floor 는 QLedgerGuard·QFxGuard 가 권위.
//
//  ── fail-closed 경로 ───────────────────────────────────────────────────────
//   · 음수/비정상 가격 → ladder 가 ArgumentOutOfRangeException(상위 게이트가 try/catch→ForceFreeze).
//   · 미등록 심볼/환율역전(Ask<Bid)/오버플로 → QAcl·TryClearAligned·QFxGuard 가 ForceFreeze.
//   · 본 파일의 KrxFxClearing.TryClearUsdToKrwBase 는 그 흐름을 한 진입점으로 묶어 닫는다.
//   · ★적대검증 교정(critical): fail-closed 는 게이트 enable-state·att 인스턴스에 의존하지 않는다.
//     ShadowCutoverGate.ClearBase 는 기본 INERT(_enabled=false && LegacyPrimary)에서 FX 검증을 통째로
//     건너뛰고 naive 값을 반환하므로(QClearing.cs), 거기에 위임하면 기본 구성에서 fail-open 이 된다.
//     따라서 QAcl+TryClearAligned(enable 무관 항상 검증)+QFxGuard.TryConvertOrFreeze 의 bool 을 직접 AND 결합한다.
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    /// <summary>
    /// KRX 가격대별 호가단위(틱) 사다리 — 순수 함수·무할당·결정론. 가격(원)을 받아 그 가격대에 적용되는
    /// 호가단위를 반환한다. 유가증권/코스닥 공통 근사(2023 호가단위 개편 반영).
    /// <para>경계 규칙: KRX 는 '미만(&lt;)' 기준(예 2,000원 미만 1원, 2,000원 이상~5,000원 미만 5원 …).</para>
    /// </summary>
    public static class KrxTickLadder
    {
        /// <summary>가격대 상한(미만, 원) → 그 구간 호가단위(원). 오름차순·마지막은 무한대 상한.</summary>
        private static readonly (decimal UpperExclusiveWon, decimal TickWon)[] Bands =
        {
            (2_000m,     1m),      // ~2천원 미만        : 1원
            (5_000m,     5m),      // 2천~5천 미만       : 5원
            (20_000m,    10m),     // 5천~2만 미만       : 10원
            (50_000m,    50m),     // 2만~5만 미만       : 50원
            (200_000m,   100m),    // 5만~20만 미만      : 100원
            (500_000m,   500m),    // 20만~50만 미만     : 500원
            (decimal.MaxValue, 1_000m), // 50만 이상     : 1000원
        };

        /// <summary>주어진 가격(원)의 호가단위(원)를 반환. 음수/비유한 가격은 fail-closed(예외 → 상위 게이트 동결).</summary>
        public static decimal TickWonFor(decimal priceWon)
        {
            if (priceWon < 0m)
                throw new ArgumentOutOfRangeException(nameof(priceWon), "KRX 사다리: 음수 가격(poison) — fail-closed");
            for (int i = 0; i < Bands.Length; i++)
                if (priceWon < Bands[i].UpperExclusiveWon) return Bands[i].TickWon;
            return Bands[^1].TickWon;   // 도달 불가(마지막이 MaxValue) — 방어적 폴백
        }

        /// <summary>
        /// 가격대 정확 QTickSpec 산출(국내주식·1주 스텝). BuiltInTickSpecs 의 '등록시점 근사' 대신, 현재 가격에서
        /// 정확한 호가단위 격자를 만든다. 핫패스(주문 진입)에서 정렬 tick 을 가격대로 동기화할 때 사용.
        /// </summary>
        public static QTickSpec SpecFor(decimal priceWon)
            => new(QPrice.Of(TickWonFor(priceWon)), QQty.Of(1m));   // 주식 = 1주 정수 스텝

        /// <summary>해외주식(USD) 단일 센트 격자 — 사다리 불요(0.01 고정·1주 스텝). 대칭 API 제공.</summary>
        public static QTickSpec UsdSpec() => new(QPrice.Of(0.01m), QQty.Of(1m));
    }

    /// <summary>
    /// 과제 2 확장 — BuiltInTickSpecs(sealed·불변경) 위에 '가격대별 KRX 격자'를 얹는 보강 엔트리/레지스트리 빌더.
    /// 기존 DefaultEntries() 를 '교체하지 않고' 합성해, 데모/부팅 시드에 KRX 대표주를 '입력 가격 기준' 정확
    /// 격자로 추가할 수 있게 한다(여전히 symbol→단일 spec 모델이지만, 가격대를 명시 입력해 근사를 제거).
    /// </summary>
    public static class KrxTickSpecsExtensions
    {
        /// <summary>국내주식 1건 — (심볼, 참조가격) → 가격대 정확 KRX 격자 엔트리. 1주 스텝.</summary>
        public static (string symbol, QTickSpec spec) KrxEntry(string symbol, decimal referencePriceWon)
            => (symbol, KrxTickLadder.SpecFor(referencePriceWon));

        /// <summary>해외주식 1건 — (심볼) → USD 센트 격자 엔트리. 1주 스텝.</summary>
        public static (string symbol, QTickSpec spec) UsdEntry(string symbol)
            => (symbol, KrxTickLadder.UsdSpec());

        /// <summary>
        /// 기본 엔트리(BuiltInTickSpecs.DefaultEntries) + 가격대 정확 KRX 보강 엔트리를 합성한 레지스트리.
        /// 동일 심볼이 양쪽에 있으면 QTickRegistry ctor 가 FNV 키 충돌로 fail-fast 하므로, 보강분은 신규 심볼만
        /// 추가하거나, BuiltInTickSpecs 를 대체(아래 BuildKrxAccurateRegistry)할 때 쓴다.
        /// </summary>
        public static QTickRegistry BuildAugmentedRegistry(
            IEnumerable<(string symbol, decimal referencePriceWon)> krxExtra)
        {
            var entries = new List<(string, QTickSpec)>(BuiltInTickSpecs.DefaultEntries());
            foreach (var (symbol, refPx) in krxExtra)
                entries.Add(KrxEntry(symbol, refPx));
            return new QTickRegistry(entries);   // 충돌/중복/무효 격자 → ctor fail-fast
        }

        /// <summary>
        /// '근사 제거' 레지스트리 — KRX 종목을 등록시점 근사 대신 '명시 참조가격의 가격대 정확 격자'로,
        /// USD 종목은 센트 격자로 빌드한다. 프로덕션은 전체 유니버스를 이렇게 등록(가격대 이탈 시 재등록).
        /// </summary>
        public static QTickRegistry BuildKrxAccurateRegistry(
            IEnumerable<(string symbol, decimal referencePriceWon)> krxUniverse,
            IEnumerable<string> usdUniverse)
        {
            var entries = new List<(string, QTickSpec)>();
            foreach (var (symbol, refPx) in krxUniverse) entries.Add(KrxEntry(symbol, refPx));
            foreach (var symbol in usdUniverse)           entries.Add(UsdEntry(symbol));
            return new QTickRegistry(entries);
        }
    }

    /// <summary>
    /// 과제 2 — 이종통화(해외 USD 자산 → base KRW) 청산 진입점.
    /// ★적대검증 교정(critical fail-open): fail-closed 가 게이트 enable-state·att 인스턴스에 의존하지 않도록
    /// '독립 검증'한다. ShadowCutoverGate.ClearBase 의 INERT(기본 OFF) 경로는 FX 검증을 건너뛰므로 거기에
    /// 위임하지 않는다. QAcl(경계)·TryClearAligned(미등록/오염, enable 무관)·QFxGuard.TryConvertOrFreeze
    /// (바운드·Ask&lt;Bid·비양수·오버플로 직접)를 AND 결합해 닫는다.
    /// <para>★보수 편향: 매수=환율 Ask 올림, 매도=환율 Bid 내림(QFxGuard 권위·base floor).</para>
    /// </summary>
    public static class KrxFxClearing
    {
        /// <summary>
        /// 해외(USD) 자산 1건을 base(KRW) 명목으로 보수 청산. 어느 단계든 오염이면 false(+ForceFreeze)·out=0.
        /// 성공 시 baseNotionalKrw = 보수 정렬 자산명목 × (매수 Ask 올림·매도 Bid 내림). 게이트 enabled 여부·
        /// att 인스턴스 결합과 무관하게 fail-closed(모호한 통과 금지).
        /// </summary>
        public static bool TryClearUsdToKrwBase(
            ShadowCutoverGate gate, OrderAttestationService? att,
            string symbol, decimal priceUsd, decimal qty, OrderSide side,
            QFxQuote usdKrw, QFxBounds bounds, out decimal baseNotionalKrw)
        {
            baseNotionalKrw = 0m;
            if (gate is null) throw new ArgumentNullException(nameof(gate));
            try
            {
                // (1) 경계 ACL — 음수/오버플로 가격·수량은 여기서 ForceFreeze + false.
                if (!QAcl.TryImportPrice(priceUsd, att, out _)) return false;
                if (!QAcl.TryImportQty(qty, att, out _))        return false;

                // (2) 자산통화(USD) 보수 정렬 — TryClearAligned 는 enable 무관 '항상' 검증(미등록/음수/오버플로 동결).
                //     게이트 INERT(기본 OFF) 경로의 검증 우회를 회피하는 핵심.
                if (!gate.TryClearAligned(symbol, priceUsd, qty, side, out var ap, out var aq))
                    return false;

                // (3) 보수 FX 환산 — QFxGuard 가 바운드·Ask<Bid·비양수·오버플로를 '직접' 검증(enable·att 결합 무관).
                //     src = 정렬 자산명목(operator* = floor QMoney); 보수 base 편향은 roundUp=(side==Buy) 가 복원.
                QMoney srcUsd = ap * aq;
                if (!QFxGuard.TryConvertOrFreeze(srcUsd, usdKrw, side, bounds, att, out var baseMoney))
                    return false;

                baseNotionalKrw = baseMoney.ToDecimal();
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or DivideByZeroException)
            {
                // 잔여 산술 트랩도 fail-closed 로 닫는다(확산 금지).
                try { att?.ForceFreeze($"KRX FX 청산: 연산 트랩({ex.GetType().Name}) — fail-closed"); } catch { }
                baseNotionalKrw = 0m;
                return false;
            }
        }
    }
}

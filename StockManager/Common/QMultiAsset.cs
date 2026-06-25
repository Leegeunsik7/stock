// ============================================================================
// QMultiAsset — S27-d: 멀티-자산 척추 (자산별 QTickSpec 룩업 + 보수 FX 환산)
//
//  S27-c 의 '단일 전역 spec' 한계를 깨고, 자산별 격자를 무할당·무잠금으로 조회하며 이종 통화 환산을
//  보수(거래소 유리) 편향으로 수행한다. 라이브/ShadowCutoverGate 미배선 — 코어 스캐폴드(정직 표기).
//
//  ── Mission 1 무할당·무잠금 룩업 ────────────────────────────────────────────
//  심볼 → FNV-1a long 키 → FrozenDictionary(불변·읽기 전용·락 없음). 값에 원본 심볼을 함께 저장해, TryGet(string)
//  은 키 조회 후 심볼을 string.Equals 로 '검증'한다 → 미등록 심볼이 등록 키와 FNV 충돌(2^-64)해도 잘못된 spec 을
//  반환하지 않고 false(fail-closed). TryGet(long) 은 사전 해시된 신뢰 키 전용(심볼 검증 없음). 빌드 시 등록셋 내
//  키 충돌/중복은 fail-fast. 미등록 → false(호출자 fail-closed 책임).
//
//  ── Mission 2 보수 FX 펜싱 ──────────────────────────────────────────────────
//  매수 환산은 Ask 올림(비용↑), 매도 환산은 Bid 내림(수령↓) — 플랫폼 불리·거래소 유리. 방어 분담(전부 비공허):
//   · 타당성 바운드(★필수) — [min,max]·최대 스프레드. wrong-scale·비정상 스프레드의 유일 방어이므로 비활성
//     (degenerate) 바운드는 fail-closed 로 거부(무방어 잠복 차단).
//   · 게이트 — 음수 원금·비양수 환율·역전(Ask<Bid).
//   · catch — 곱 오버플로.
//  ★적대적 리뷰 정정: 과거의 '동일 환율 forward+back 역산 검사'는 |back−src|<Scale/rate+1≤band 가 '항상' 성립해
//  어떤 입력도 freeze 못 시키는 죽은 항등 검사였다 → 제거. wrong-scale 방어는 전적으로 타당성 바운드가 담당.
// ============================================================================
using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace StockManager.Common
{
    /// <summary>Mission 1 — 자산별 격자 레지스트리. 불변·락-프리·무할당. TryGet(string)은 심볼 검증으로 충돌 안전.</summary>
    public sealed class QTickRegistry
    {
        private readonly FrozenDictionary<long, (string Symbol, QTickSpec Spec)> _map;

        /// <summary>심볼→FNV-1a long 키(JournalRecord.HashId 재사용 — 블리터블 안정 해시).</summary>
        public static long Key(string symbol) => JournalRecord.HashId(symbol);

        public QTickRegistry(IEnumerable<(string symbol, QTickSpec spec)> entries)
        {
            if (entries is null) throw new ArgumentNullException(nameof(entries));
            var map  = new Dictionary<long, (string, QTickSpec)>();
            var seen = new Dictionary<long, string>();
            foreach (var (symbol, spec) in entries)
            {
                if (string.IsNullOrEmpty(symbol)) throw new ArgumentException("QTickRegistry: 빈 심볼");
                if (!spec.IsValid)                throw new ArgumentException($"QTickRegistry: 무효 격자 '{symbol}'");
                long k = Key(symbol);
                if (seen.TryGetValue(k, out var prev))   // 빌드 시 등록셋 내 키 충돌/중복 fail-fast
                    throw new InvalidOperationException($"QTickRegistry: FNV 키 충돌/중복 — '{prev}' vs '{symbol}' (key={k})");
                seen[k] = symbol; map[k] = (symbol, spec);
            }
            _map = map.ToFrozenDictionary();
        }

        public int Count => _map.Count;

        /// <summary>심볼로 격자 조회. 키 조회 후 심볼 검증(충돌·미등록 → false, fail-closed). 무할당(string.Equals 순수).</summary>
        public bool TryGet(string symbol, out QTickSpec spec)
        {
            if (_map.TryGetValue(Key(symbol), out var e) && string.Equals(e.Symbol, symbol, StringComparison.Ordinal))
            { spec = e.Spec; return true; }
            spec = default; return false;
        }

        /// <summary>사전 해시된 신뢰 키 직접 조회(심볼 검증 없음 — 외부 심볼은 TryGet(string) 사용). 무할당.</summary>
        public bool TryGet(long key, out QTickSpec spec)
        {
            if (_map.TryGetValue(key, out var e)) { spec = e.Spec; return true; }
            spec = default; return false;
        }
    }

    /// <summary>
    /// v4.56.0 — 대표 자산군 '기본 격자' 엔트리셋. KRW 정수 호가(가격대별 틱)·USD 센트(0.01)·코인 사토시 스텝(1e-8)이
    /// 동일 QFixed(10^8) 위에서 자산별 격자로 공존하게 한다(ShadowCutoverGate/QAdmissionGate 의 registry ctor 에 주입).
    /// <para>
    /// ⚠️ 정직 한계(스펙이 가린 함정): ① KRX 호가단위는 '심볼당 단일'이 아니라 '현재 가격대별'(예: 2천원 미만 1원,
    /// 2천~5천 5원, 5천~2만 10원, 2만~5만 50원, 5만~20만 100원, 20만~50만 500원, 50만↑ 1000원)이다. 본 맵은
    /// 'symbol→단일 QTickSpec' 모델상 등록 시점 가격대 기준 '근사'다. 가격대 이탈 시 — AdmissionGate 의 '정확 정렬'
    /// 검문이 비정렬 서브틱을 <b>스냅하지 않고 fail-closed 거부</b>하므로 침묵 왜곡(silent skew)은 발생하지 않는다
    /// (거부될 뿐 틀린 명목이 새지 않음). ② ETF 는 5원 틱(가격 무관). ③ 코인 KRW-* 가격 틱도 업비트 가격대별이라 근사.
    /// 프로덕션은 전체 유니버스를 가격대별로 등록해야 하며, 본 셋은 데모·테스트·부팅 시드용 '대표 표본'이다.
    /// </para>
    /// </summary>
    public static class BuiltInTickSpecs
    {
        /// <summary>대표 엔트리로 불변 레지스트리를 빌드(중복/충돌은 ctor 가 fail-fast).</summary>
        public static QTickRegistry BuildDefaultRegistry() => new(DefaultEntries());

        private static QTickSpec Krw(decimal tickWon) => new(QPrice.Of(tickWon), QQty.Of(1m));            // 정수 호가·1주 스텝
        private static QTickSpec Usd()                 => new(QPrice.Of(0.01m), QQty.Of(1m));             // 센트 틱·1주 스텝
        private static QTickSpec Coin(decimal tickWon) => new(QPrice.Of(tickWon), QQty.Of(0.00000001m));  // KRW 가격 틱·사토시 스텝

        /// <summary>대표 표본(가격대 근사·정직 표기). 등록 외 심볼은 게이트에서 fail-closed(미통과·필요 시 동결).</summary>
        public static IEnumerable<(string symbol, QTickSpec spec)> DefaultEntries()
        {
            // ── KRW 코스피 대형주(현재 가격대 호가단위 근사) ──
            yield return ("005930", Krw(100m));    // 삼성전자  (~8만원대 → 5만~20만 → 100원)
            yield return ("000660", Krw(500m));    // SK하이닉스 (~20만↑ → 20만~50만 → 500원)
            yield return ("035420", Krw(100m));    // NAVER     (~5만~20만 → 100원)
            yield return ("035720", Krw(50m));     // 카카오    (~2만~5만 → 50원)
            yield return ("005380", Krw(500m));    // 현대차    (~20만~50만 → 500원)
            yield return ("069500", Krw(5m));      // KODEX 200 ETF (ETF 고정 5원 틱·가격 무관)
            // ── USD 나스닥(센트 틱) ──
            yield return ("AAPL", Usd());
            yield return ("NVDA", Usd());
            yield return ("MSFT", Usd());
            yield return ("TSLA", Usd());
            // ── 코인(업비트 KRW 마켓·가격대 근사·사토시 스텝) ──
            yield return ("KRW-BTC", Coin(1000m)); // ≥200만원 가격대 → 1000원 틱 근사
            yield return ("KRW-ETH", Coin(1000m));
        }
    }

    /// <summary>환율(통화 A→B). 스케일 10^8 raw(예 1330.50 → 133050000000).</summary>
    public readonly record struct QFxRate(long Raw)
    {
        public static QFxRate Of(decimal v) => new(QMath.FromDecimal(v));
        public decimal ToDecimal()          => QMath.ToDecimal(Raw);
    }

    /// <summary>매수/매도 양면 환율 호가. 정상은 Ask ≥ Bid(양의 스프레드).</summary>
    public readonly record struct QFxQuote(QFxRate Bid, QFxRate Ask);

    /// <summary>환율 타당성 바운드(필수) — [min,max]·최대 스프레드(raw). wrong-scale·비정상 스프레드 방어.</summary>
    public readonly record struct QFxBounds(long MinRateRaw, long MaxRateRaw, long MaxSpreadRaw)
    {
        public static QFxBounds Of(decimal min, decimal max, decimal maxSpread)
            => new(QMath.FromDecimal(min), QMath.FromDecimal(max), QMath.FromDecimal(maxSpread));
    }

    /// <summary>Mission 2 — 보수 FX 환산 게이트. 무효 바운드·스큐·오버플로 시 ForceFreeze. 정상 무할당.</summary>
    public static class QFxGuard
    {
        public static bool TryConvertOrFreeze(QMoney src, QFxQuote quote, OrderSide side, QFxBounds bounds,
                                              OrderAttestationService? attestation, out QMoney dst)
        {
            dst = default;
            try
            {
                long bid = quote.Bid.Raw, ask = quote.Ask.Raw;
                // ★바운드 필수 — 비활성(degenerate) 바운드는 wrong-scale 의 유일 방어를 무력화 → fail-closed 거부.
                if (bounds.MinRateRaw <= 0 || bounds.MaxRateRaw < bounds.MinRateRaw || bounds.MaxSpreadRaw <= 0)
                { Freeze(attestation, "FX: 무효 바운드(degenerate) — wrong-scale 무방어 차단, fail-closed"); return false; }
                if (src.Raw < 0)             { Freeze(attestation, "FX: 음수 원금"); return false; }
                if (bid <= 0 || ask <= 0)    { Freeze(attestation, "FX: 비양수 환율"); return false; }
                if (ask < bid)               { Freeze(attestation, "FX: 매수/매도 환율 역전(Ask<Bid) — 스큐"); return false; }
                if (bid < bounds.MinRateRaw || ask > bounds.MaxRateRaw)
                                             { Freeze(attestation, "FX: 환율 타당성 바운드 이탈(wrong-scale 의심)"); return false; }
                if (ask - bid > bounds.MaxSpreadRaw)
                                             { Freeze(attestation, "FX: 비정상 스프레드 확장 — 스큐"); return false; }

                long rate = side == OrderSide.Buy ? ask : bid;       // 매수=Ask·매도=Bid (거래소 유리)
                dst = new QMoney(QMath.Notional(src.Raw, rate, roundUp: side == OrderSide.Buy));
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or DivideByZeroException or ArgumentOutOfRangeException)
            { Freeze(attestation, "FX: 연산 트랩 — fail-closed"); dst = default; return false; }
        }

        private const decimal OneRaw = 0.00000001m;   // 1 raw = 10^-8 (정준 스케일 최소 단위)

        /// <summary>
        /// S27-e Mission 2 — FX 환산 보수 수렴 검문(★비항등 한계 역치). 신 보수 명목 got(base raw)을 '독립 정확값'
        /// exact = alignedNotionalDec × rateDec 와 대조한다(got 의 자기파생이 아님 → 항등식이 아니다):
        ///   · 방향(보수 편향): 매수 got≥exact(ceil)·매도 got≤exact(floor). 역전(플랫폼 유리)이면 ForceFreeze.
        ///   · 한계 역치: |got−exact| 이 누적 라운딩 한계(청산 1raw×rate + FX 1raw)를 '1 raw라도' 초과하면 ForceFreeze.
        /// 정당 경로(신 QFixed 청산→FX)는 두 단계 보수 라운딩이라 항상 방향 보존 + |Δ|≤band 이나, 환율/스케일 오염은
        /// band 를 finite 하게 초과해 반드시 발화한다(과거의 forward+back 동일환율 역산은 |Δ|&lt;band 가 항상 성립하던
        /// 죽은 항등식이라 제거됨 — 본 검문은 정렬입력·환율로부터 '독립' 산출한 exact 와 대조해 그 함정을 회피).
        /// 순수 decimal 값연산 — 무할당(동결 경로만 att 감사 할당).
        /// </summary>
        public static bool VerifyConservativeFxOrFreeze(QMoney got, decimal alignedNotionalDec, decimal rateDec,
                                                        OrderSide side, OrderAttestationService? attestation)
        {
            // ★적대적 리뷰(minor) — public 독립 API 자기방어: 음수 환율은 방향 검문(매수 g≥exact·매도 g≤exact)을
            //   뒤집어 보수성을 무력화한다. 상위 게이트(TryConvertOrFreeze)가 rate>0 을 선검증하지만, 이 메서드를
            //   직접 호출하는 경로(테스트·미래 확장)에서도 fail-closed 가 깨지지 않도록 부호를 자체 검문한다.
            if (rateDec <= 0m || alignedNotionalDec < 0m)
            { Freeze(attestation, "FX수렴: 비양수 환율/음수 명목 — 독립 검문 fail-closed"); return false; }
            decimal g = got.ToDecimal();
            decimal exact = alignedNotionalDec * rateDec;
            if (side == OrderSide.Buy  && g < exact) { Freeze(attestation, "FX수렴: 매수 보수 역전(ceil 위반·플랫폼 유리) — 동결"); return false; }
            if (side == OrderSide.Sell && g > exact) { Freeze(attestation, "FX수렴: 매도 보수 역전(floor 위반·플랫폼 유리) — 동결"); return false; }
            // 누적 라운딩 한계 = 청산 잔여(1raw)가 환율배 + FX 잔여(1raw). 환율은 위에서 양수 보증 — Abs 불요.
            decimal band = OneRaw * rateDec + OneRaw;
            if (Math.Abs(g - exact) > band)
            { Freeze(attestation, "FX수렴: 환산 오차 한계 역치 초과 — 스케일/환율 오염, 동결"); return false; }
            return true;
        }

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }
}

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

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }
}

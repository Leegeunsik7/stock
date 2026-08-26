// ============================================================================
// CartridgeEngine - 전략 카트리지 포맷 + 무신뢰 검증 (Season 7 #2) · 순수, 외부 의존 0
//  카트리지(.ms2cart) = 사용자가 교환하는 {커맨드라인 매크로 · 전략 스펙 · 프롬프트 템플릿} 묶음.
//  두 겹 신뢰:
//   ① ECDsa 서명 — 제작자 신원 + 무결성(필드 1개라도 바뀌면 서명 깨짐).
//   ② 결정론 백테스트 증명서 — 전략 스펙을 '시드 기준 시계열'에서 재실행해 주장 성과와 대조.
//      백테스트가 결정론이라 중앙 서버 없이 '신뢰하되 검증(verify-by-recompute)'이 성립.
//  ⚠️ 기준 시계열 성과는 합성 벤치마크이며 실거래 수익 보장이 아님(스펙↔성과 진위 검증용).
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public sealed record CartMacro(string Alias, string Command);
    public sealed record CartPrompt(string Id, string Template);

    public sealed record CartStrategy(
        string Name, int Short, int Long, int Seed,
        double AttestTotalReturnPct, double AttestMaxDrawdownPct, int AttestTrades);

    public sealed record Cartridge(
        int FormatVersion, string Id, string Name, string Author, string CreatedIso,
        string AuthorPubKey, string Description,
        List<CartMacro> Macros, List<CartStrategy> Strategies, List<CartPrompt> Prompts,
        string Signature);

    public sealed record CartridgeVerdict(bool SignatureOk, bool AttestationOk, int Strategies, int VerifiedStrategies, string Note)
    {
        public bool FullyTrusted => SignatureOk && AttestationOk;
    }

    public static class CartridgeEngine
    {
        public const int FormatVersion = 1;

        // 시드별 CandleFrame 배열 캐시 — ToFrameArray 일회성 변환 결과 보관
        private static readonly ConcurrentDictionary<int, CandleFrame[]> _frameCache = new();

        // 결정론 PRNG(SplitMix64) — System.Random 구현 변화/플랫폼에 독립적으로 동일 시퀀스 보장.
        //  (교차 기기·미래 .NET 버전에서도 증명서 재실행이 동일 결과를 내야 하므로 자체 PRNG 사용)
        private sealed class Prng
        {
            private ulong _s;
            public Prng(int seed) => _s = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL);
            public double NextUnit()   // [0,1)
            {
                _s = unchecked(_s + 0x9E3779B97F4A7C15UL);
                ulong z = _s;
                z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
                z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
                z ^= z >> 31;
                return (z >> 11) * (1.0 / 9007199254740992.0);
            }
        }

        // ── 결정론 기준 시계열(시드) — 검증자가 동일 시드로 동일 캔들 재생성 ──
        public static List<Candle> ReferenceSeries(int seed, int n = 300)
        {
            var rnd = new Prng(seed);
            var list = new List<Candle>(n);
            decimal price = 100m;
            var date = new DateTime(2000, 1, 1);
            for (int i = 0; i < n; i++)
            {
                double r = (rnd.NextUnit() - 0.49) * 0.04;       // 약 -2%~+2% (살짝 상방 편향)
                decimal open = price;
                price = Math.Max(1m, price * (1m + (decimal)r));
                decimal hi = Math.Max(open, price) * 1.01m, lo = Math.Min(open, price) * 0.99m;
                list.Add(new Candle { Date = date.AddDays(i), Open = open, High = hi, Low = lo, Close = price, Volume = 1000 });
            }
            return list;
        }

        public static (double ret, double mdd, int trades) Backtest(int shortP, int longP, int seed)
        {
            var r = BacktestEngine.RunMaCross(ReferenceSeries(seed), Market.KRX_KOSPI, shortP, longP);
            return ((double)r.TotalReturn, (double)r.MaxDrawdown, r.TradeCount);
        }

        /// <summary>시드 기준 CandleFrame 배열 — 최초 1회 변환 후 캐시 재사용.</summary>
        public static CandleFrame[] ReferenceFrames(int seed)
            => _frameCache.GetOrAdd(seed, s => ZeroAllocTickEngine.ToFrameArray(ReferenceSeries(s)));

        /// <summary>
        /// 고속 MA 크로스 백테스트 — GC 힙 할당 0바이트 (ZeroAllocTickEngine 위임).
        /// Attest/VerifyStrategy 는 decimal 기반 Backtest() 를 유지; 이 메서드는
        /// CartridgeGenome 등 반복 최적화 루프의 빠른 순위 비교용.
        /// </summary>
        public static ZeroAllocResult BacktestFast(int shortP, int longP, int seed)
            => ZeroAllocTickEngine.RunMaCross(ReferenceFrames(seed), shortP, longP);

        /// <summary>전략 스펙에 결정론 증명서(시드 기준 성과)를 부착.</summary>
        public static CartStrategy Attest(string name, int shortP, int longP, int seed)
        {
            var (ret, mdd, tr) = Backtest(shortP, longP, seed);
            // 4자리 반올림 — canonical 문자열(F4)과 정합·부동소수 잡음 제거(교차 기기 안정)
            return new CartStrategy(name, shortP, longP, seed, Math.Round(ret, 4), Math.Round(mdd, 4), tr);
        }

        /// <summary>증명서 재실행 검증 — 주장 성과(4자리)가 시드 기준 시계열에서 실제로 재현되는가.</summary>
        public static bool VerifyStrategy(CartStrategy s, double eps = 1e-6)
        {
            var (ret, mdd, tr) = Backtest(s.Short, s.Long, s.Seed);
            return Math.Abs(Math.Round(ret, 4) - s.AttestTotalReturnPct) < eps
                && Math.Abs(Math.Round(mdd, 4) - s.AttestMaxDrawdownPct) < eps
                && tr == s.AttestTrades;
        }

        // ── 서명 대상 canonical 문자열 — 불변 F4 정밀도·항목 정렬·CreatedIso(메타)·Signature 제외.
        //  JSON 기본 직렬화의 숫자/들여쓰기/런타임버전 비결정성을 피해 교차 기기·버전에서 동일 바이트 보장. ──
        public static string Canonical(Cartridge c)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("MS2CART|v").Append(c.FormatVersion).Append('|')
              .Append(c.Id).Append('|').Append(c.Name).Append('|').Append(c.Author).Append('|')
              .Append(c.AuthorPubKey).Append('|').Append(c.Description).Append("|M:");
            foreach (var m in (c.Macros ?? new()).OrderBy(x => x.Alias, StringComparer.Ordinal))
                sb.Append(m.Alias).Append('=').Append(m.Command).Append(';');
            sb.Append("|S:");
            foreach (var s in (c.Strategies ?? new()).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Short).ThenBy(x => x.Long).ThenBy(x => x.Seed))
                sb.Append(s.Name).Append(',').Append(s.Short).Append(',').Append(s.Long).Append(',').Append(s.Seed).Append(',')
                  .Append(s.AttestTotalReturnPct.ToString("F4", inv)).Append(',').Append(s.AttestMaxDrawdownPct.ToString("F4", inv)).Append(',').Append(s.AttestTrades).Append(';');
            sb.Append("|P:");
            foreach (var p in (c.Prompts ?? new()).OrderBy(x => x.Id, StringComparer.Ordinal))
                sb.Append(p.Id).Append('=').Append(p.Template).Append(';');
            return sb.ToString();
        }

        public static byte[] CanonicalBytes(Cartridge c) => System.Text.Encoding.UTF8.GetBytes(Canonical(c));

        public static Cartridge Sign(Cartridge c, string privB64)
            => c with { Signature = CartridgeCrypto.Sign(CanonicalBytes(c), privB64) };

        public static CartridgeVerdict Verify(Cartridge c)
        {
            bool sig = CartridgeCrypto.Verify(CanonicalBytes(c), c.Signature, c.AuthorPubKey);
            int total = c.Strategies?.Count ?? 0;
            int ok = c.Strategies?.Count(s => VerifyStrategy(s)) ?? 0;
            bool attest = total == 0 || ok == total;
            string note = !sig ? "서명 검증 실패(변조 또는 키 불일치)"
                        : attest ? "✅ 서명·증명서 검증 통과"
                        : $"서명 OK · 증명서 불일치 {total - ok}건(주장 성과 재현 실패)";
            return new CartridgeVerdict(sig, attest, total, ok, note);
        }
    }
}

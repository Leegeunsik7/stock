// ============================================================================
// PlutoLocalCandleSource — v12.18.86-DEV: IPlutoCandleSource 구현(LocalDatabase portfolio.db Candles 테이블 판독 전용 · 웜스타트 콜드 경로)
//
//   최근 갱신 순 종목(GROUP BY Symbol · MAX(Date) 내림차순) → 종목 코드로 자산군 판별(Classify) → 자산군당 최대 maxSymbolsPerAsset 종목 ·
//   종목당 최근 bars 봉 종가(오래된 순). 종가 = 양수 유한 실수/정수만 — 그 외(NULL · 문자열 오염 · 0 이하) 봉 = 버림 · 남은 봉 < MinBars = 종목 제외.
//   Classify: 'KRW-' / 'BTC-' / 'USDT-' 접두 = Upbit · 6자리 숫자(선택 .KS/.KQ) = KRX · 영문 대문자 1~6자(점 허용 · 예 BRK.B) = US · 그 외 = 판별 불가(제외).
//   ★기록 · 스키마 변경 0(SELECT 만) · 네트워크 0 · 캔들 0건 = 빈 목록(웜스타트 NoData 정직 표기 · 가짜 시계열 생성 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using MS2E.Infrastructure;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Adapters
{
    public sealed class PlutoLocalCandleSource : IPlutoCandleSource
    {
        private readonly LocalDatabase _db;

        public PlutoLocalCandleSource(LocalDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public IReadOnlyList<PlutoCandleSeries> LoadRecent(int maxSymbolsPerAsset, int bars)
        {
            if (maxSymbolsPerAsset <= 0 || bars <= 0) return Array.Empty<PlutoCandleSeries>();
            var symbols = _db.Query("SELECT Symbol, MAX(Date) AS D FROM Candles WHERE Symbol IS NOT NULL GROUP BY Symbol ORDER BY D DESC LIMIT @m",
                new() { ["@m"] = maxSymbolsPerAsset * PlutoCognitiveTensorLake.Assets * 4 });
            Span<int> taken = stackalloc int[PlutoCognitiveTensorLake.Assets];
            var result = new List<PlutoCandleSeries>();
            foreach (DataRow r in symbols.Rows)
            {
                if (r[0] is not string symbol || Classify(symbol) is not { } asset || taken[(int)asset] >= maxSymbolsPerAsset) continue;
                var closes = Closes(_db.Query("SELECT Close FROM (SELECT Date, Close FROM Candles WHERE Symbol = @s ORDER BY Date DESC LIMIT @n) ORDER BY Date",
                    new() { ["@s"] = symbol, ["@n"] = bars }));
                if (closes.Length < PlutoReplayWarmStarter.MinBars) continue;
                result.Add(new PlutoCandleSeries(symbol, asset, closes));
                taken[(int)asset]++;
            }
            return result;
        }

        private static double[] Closes(DataTable dt)
        {
            var list = new List<double>(dt.Rows.Count);
            foreach (DataRow r in dt.Rows)
            {
                double v = r[0] switch { double d => d, long l => l, int i => i, float f => f, decimal m => (double)m, _ => double.NaN };
                if (double.IsFinite(v) && v > 0) list.Add(v);
            }
            return list.ToArray();
        }

        public static PlutoAssetClass? Classify(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            string s = symbol.Trim().ToUpperInvariant();
            if (s.StartsWith("KRW-", StringComparison.Ordinal) || s.StartsWith("BTC-", StringComparison.Ordinal) || s.StartsWith("USDT-", StringComparison.Ordinal))
                return s.IndexOf('-') < s.Length - 1 ? PlutoAssetClass.Upbit : null;
            string core = s.EndsWith(".KS", StringComparison.Ordinal) || s.EndsWith(".KQ", StringComparison.Ordinal) ? s[..^3] : s;
            if (core.Length == 6 && AllDigits(core)) return PlutoAssetClass.Krx;
            if (s.Length is >= 1 and <= 6 && s[0] is >= 'A' and <= 'Z' && AllUsChars(s)) return PlutoAssetClass.Us;
            return null;
        }

        private static bool AllDigits(string s)
        {
            foreach (char c in s) if (c is < '0' or > '9') return false;
            return true;
        }

        private static bool AllUsChars(string s)
        {
            foreach (char c in s) if (c is not (>= 'A' and <= 'Z' or '.')) return false;
            return true;
        }
    }
}

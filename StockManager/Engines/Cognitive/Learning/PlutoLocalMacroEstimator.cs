// ============================================================================
// PlutoLocalMacroEstimator — v12.18.93-DEV: 로컬 거시 레짐 추정기(FRED API 키 미설정 폴백 · 로컬 일봉 → 3차원 거시 관측 → 12대 국면 자율 매칭 · 순수 함수)
//
//   목적: 외부 거시 피드 미결선(키 미설정 · 사용자 수집원 없음)이면 국면 판정기가 '판정 전' 에 멈추므로, 로컬 캔들만으로 근사 관측을 만든다.
//   입력 = IPlutoCandleSource 자산군별 최근 일봉 종가(오래된 순). 산출(PlutoMacroSnapshot 단위 그대로):
//     VIX 대용(Vix)        = 주식(KRX · US) 시계열별 최근 VolWindow(20)봉 일간 로그수익률 표본표준편차 × √252 × 100 → 연율 % × 100 의 중앙값
//                           (실현 변동성 = 내재 변동성 VIX 의 근사 · 정직 표기 '실현변동성 대용').
//     지수 추세(EquityTrend) = 주식 시계열별 TrendWindow(21)봉 수익률 bp 의 중앙값(≈ 1개월 · FRED S&P500 1개월 변화와 같은 단위).
//     심리(Sentiment)       = 코인 시계열별 21봉 수익률 bp 중앙값 ÷ 30(3,000bp = ±100 포화 · 위험선호 대용 'BTC 흐름').
//   차원 2개 미만(주식 캔들 없음 등) = 추정 불가(false · 가짜 관측 0). 범위 = PlutoMacroSnapshot 무결성 상한 포화.
//   ★콜드 경로(백그라운드 · 분 단위 주기) · 네트워크 · QuantumVault · 피어 0 · liveArmed:false — 국면 판정 입력만.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>추정 근거(UI 해설 · 진단).</summary>
    public readonly record struct PlutoLocalMacroBasis(int EquitySeries, int CryptoSeries, int RealizedVolX100, int EquityTrendBp, int CryptoTrendBp, int Dimensions);

    public static class PlutoLocalMacroEstimator
    {
        public const int VolWindow = 20, TrendWindow = 21, SentimentDivisorBp = 30;
        public const string SourceName = "로컬 캔들 추정";

        /// <summary>로컬 일봉 → 거시 관측(차원 ≥ 2 = true). nowUtcTicks = 관측 기준 시각.</summary>
        public static bool TryEstimate(IReadOnlyList<PlutoCandleSeries> series, long nowUtcTicks, out PlutoMacroSnapshot snapshot, out PlutoLocalMacroBasis basis)
        {
            ArgumentNullException.ThrowIfNull(series);
            snapshot = default;
            basis = default;
            if (nowUtcTicks <= 0) return false;
            var vols = new List<int>();
            var eqTrends = new List<int>();
            var cxTrends = new List<int>();
            foreach (var s in series)
            {
                if (s?.Closes is not { } c) continue;
                if (s.Asset == PlutoAssetClass.Upbit)
                {
                    if (TryTrendBp(c, out int t)) cxTrends.Add(t);
                    continue;
                }
                if (TryRealizedVolX100(c, out int v)) vols.Add(v);
                if (TryTrendBp(c, out int e)) eqTrends.Add(e);
            }
            var fields = PlutoMacroField.None;
            int vix = 0, trend = 0, sentiment = 0, cxTrend = 0;
            if (vols.Count > 0) { vix = Math.Clamp(Median(vols), 0, PlutoMacroSnapshot.MaxVixX100); fields |= PlutoMacroField.Vix; }
            if (eqTrends.Count > 0) { trend = Math.Clamp(Median(eqTrends), -PlutoMacroSnapshot.MaxChangeBp, PlutoMacroSnapshot.MaxChangeBp); fields |= PlutoMacroField.EquityTrend; }
            if (cxTrends.Count > 0)
            {
                cxTrend = Median(cxTrends);
                sentiment = Math.Clamp(cxTrend / SentimentDivisorBp, -PlutoMacroSnapshot.MaxSentiment, PlutoMacroSnapshot.MaxSentiment);
                fields |= PlutoMacroField.Sentiment;
            }
            int dims = System.Numerics.BitOperations.PopCount((uint)fields);
            basis = new PlutoLocalMacroBasis(eqTrends.Count, cxTrends.Count, vix, trend, cxTrend, dims);
            if (dims < PlutoRegimeVectorLake.MinDimensions) return false;
            snapshot = new PlutoMacroSnapshot(fields, vix, 0, 0, 0, trend, sentiment, nowUtcTicks);
            return snapshot.IsWellFormed;
        }

        /// <summary>최근 VolWindow 봉 일간 로그수익률 표본표준편차 × √252 × 10000(연율 % × 100). 봉 부족 · 비양수 = false.</summary>
        public static bool TryRealizedVolX100(double[] closes, out int volX100)
        {
            volX100 = 0;
            if (closes is null || closes.Length < VolWindow + 1) return false;
            int start = closes.Length - VolWindow;
            double sum = 0, sumSq = 0;
            for (int i = start; i < closes.Length; i++)
            {
                double a = closes[i - 1], b = closes[i];
                if (!(a > 0) || !(b > 0) || !double.IsFinite(a) || !double.IsFinite(b)) return false;
                double r = Math.Log(b / a);
                sum += r;
                sumSq += r * r;
            }
            double mean = sum / VolWindow;
            double var = Math.Max(0, (sumSq - VolWindow * mean * mean) / (VolWindow - 1));
            double annual = Math.Sqrt(var) * Math.Sqrt(252) * 10000;
            volX100 = (int)Math.Clamp(Math.Round(annual), 0, PlutoMacroSnapshot.MaxVixX100);
            return true;
        }

        /// <summary>TrendWindow 봉 수익률 bp(±MaxChangeBp 포화). 봉 부족 · 비양수 = false.</summary>
        public static bool TryTrendBp(double[] closes, out int bp)
        {
            bp = 0;
            if (closes is null || closes.Length < TrendWindow + 1) return false;
            double now = closes[^1], then = closes[^(TrendWindow + 1)];
            if (!(now > 0) || !(then > 0) || !double.IsFinite(now) || !double.IsFinite(then)) return false;
            bp = (int)Math.Clamp(Math.Round((now / then - 1) * 10000), -PlutoMacroSnapshot.MaxChangeBp, PlutoMacroSnapshot.MaxChangeBp);
            return true;
        }

        /// <summary>1회: 캔들 판독 → 추정 → 수집기 병합(성공 = true · 판독 장애/차원 부족 = false · 직전 안정값 유지).</summary>
        public static bool IngestOnce(IPlutoCandleSource source, PlutoGlobalDataIngestor ingestor, long nowUtcTicks, out PlutoLocalMacroBasis basis)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(ingestor);
            basis = default;
            IReadOnlyList<PlutoCandleSeries> series;
            try { series = source.LoadRecent(PlutoReplayWarmStarter.MaxSymbolsPerAsset, TrendWindow + 1 + VolWindow); }
            catch (Exception) { return false; }
            return TryEstimate(series, nowUtcTicks, out var snapshot, out basis) && ingestor.Ingest(in snapshot);
        }

        /// <summary>백그라운드 주기 루프(콜드 · 취소 = 종료). onEstimate = 성공 시 근거 통지(UI 문구 갱신).</summary>
        public static async Task RunAsync(IPlutoCandleSource source, PlutoGlobalDataIngestor ingestor, TimeSpan interval, Action<PlutoLocalMacroBasis>? onEstimate,
            CancellationToken ct, Func<long>? utcNowTicks = null)
        {
            var now = utcNowTicks ?? (static () => DateTime.UtcNow.Ticks);
            while (!ct.IsCancellationRequested)
            {
                if (IngestOnce(source, ingestor, now(), out var basis)) onEstimate?.Invoke(basis);
                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static int Median(List<int> v)
        {
            v.Sort();
            int n = v.Count;
            return n % 2 == 1 ? v[n / 2] : (int)(((long)v[n / 2 - 1] + v[n / 2]) / 2);
        }

        /// <summary>UI 문구: "로컬 캔들 추정 · 실현변동성 18.4% · 지수 추세 +3.2% · BTC 흐름 −5.1%".</summary>
        public static string Describe(in PlutoLocalMacroBasis b)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string text = SourceName + " · 주식 " + b.EquitySeries.ToString(inv) + "종 · 코인 " + b.CryptoSeries.ToString(inv) + "종";
            if (b.EquitySeries > 0)
                text += " · 실현변동성(VIX 대용) " + (b.RealizedVolX100 / 100.0).ToString("0.0", inv) + "% · 지수 추세 " + Pct(b.EquityTrendBp);
            if (b.CryptoSeries > 0) text += " · BTC 흐름 " + Pct(b.CryptoTrendBp);
            return text;
        }

        private static string Pct(int bp) => (bp > 0 ? "+" : "") + (bp / 100.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}

// ============================================================================
// StockManager.Common - 분석 서비스
// 추가기능 C: 세금 시뮬레이터
// 추가기능 E: 52주 신고가/신저가 스캐너
// 추가기능 F: 상관관계 매트릭스
// 추가기능 G: 섹터 로테이션
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    // =========================================================================
    // 52주 신고가/신저가 스캐너 (기능 E)
    // =========================================================================
    public static class ExtremeScanner
    {
        public static async Task<List<ExtremeEvent>> ScanAsync(
            IQuoteProvider provider,
            IEnumerable<(string sym, string name, Market mkt)> symbols,
            CancellationToken ct = default)
        {
            var results = new List<ExtremeEvent>();
            foreach (var (sym, name, mkt) in symbols)
            {
                try
                {
                    var candles = await provider.GetHistoryAsync(sym, mkt,
                        DateTime.Now.AddYears(-1), DateTime.Now, ct);
                    if (candles.Count < 50) continue;

                    // 직전까지의 52주 종가 기준 최고/최저 (당일 제외)
                    var prior = candles.SkipLast(1).ToList();
                    var high52 = prior.Max(c => c.Close);
                    var low52 = prior.Min(c => c.Close);
                    var last = candles.Last();

                    if (last.Close >= high52)
                    {
                        results.Add(new ExtremeEvent
                        {
                            Symbol = sym, Name = name, IsHigh = true,
                            CurrentPrice = last.Close, Extreme52 = high52,
                            ChangePct = high52 == 0 ? 0 : (last.Close - high52) / high52 * 100m
                        });
                    }
                    else if (last.Close <= low52)
                    {
                        results.Add(new ExtremeEvent
                        {
                            Symbol = sym, Name = name, IsHigh = false,
                            CurrentPrice = last.Close, Extreme52 = low52,
                            ChangePct = low52 == 0 ? 0 : (last.Close - low52) / low52 * 100m
                        });
                    }
                }
                catch (Exception ex) { Log.Warn($"[Extreme] {sym}: {ex.Message}"); }
            }
            return results;
        }
    }

    // =========================================================================
    // 상관관계 매트릭스 (기능 F)
    // =========================================================================
    public static class CorrelationAnalyzer
    {
        /// <summary>일수익률 간 Pearson 상관계수</summary>
        public static decimal Pearson(List<decimal> a, List<decimal> b)
        {
            if (a.Count != b.Count || a.Count < 2) return 0m;
            var n = a.Count;
            var avgA = a.Average();
            var avgB = b.Average();
            double num = 0, denA = 0, denB = 0;
            for (int i = 0; i < n; i++)
            {
                var da = (double)(a[i] - avgA);
                var db = (double)(b[i] - avgB);
                num += da * db;
                denA += da * da;
                denB += db * db;
            }
            var den = Math.Sqrt(denA * denB);
            return den == 0 ? 0m : (decimal)(num / den);
        }

        /// <summary>종목 리스트의 일수익률 matrix 계산</summary>
        public static async Task<Dictionary<string, List<decimal>>> CollectReturnsAsync(
            IQuoteProvider provider,
            IEnumerable<(string sym, Market mkt)> symbols,
            int days = 90, CancellationToken ct = default)
        {
            var map = new Dictionary<string, List<decimal>>();
            foreach (var (sym, mkt) in symbols)
            {
                try
                {
                    var candles = await provider.GetHistoryAsync(sym, mkt,
                        DateTime.Now.AddDays(-days - 20), DateTime.Now, ct);
                    if (candles.Count < 20) continue;
                    var rets = new List<decimal>();
                    for (int i = 1; i < candles.Count; i++)
                    {
                        if (candles[i - 1].Close == 0) continue;
                        rets.Add((candles[i].Close - candles[i - 1].Close) / candles[i - 1].Close);
                    }
                    map[sym] = rets;
                }
                catch (Exception ex) { Log.Warn($"[Correlation] {sym}: {ex.Message}"); }
            }
            // 길이 정규화 (최소 길이에 맞춤)
            if (map.Count > 0)
            {
                var minLen = map.Values.Min(x => x.Count);
                foreach (var k in map.Keys.ToList())
                    map[k] = map[k].TakeLast(minLen).ToList();
            }
            return map;
        }

        public static List<CorrelationPair> ComputeAll(Dictionary<string, List<decimal>> returns)
        {
            var pairs = new List<CorrelationPair>();
            var keys = returns.Keys.ToList();
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                    pairs.Add(new CorrelationPair
                    {
                        SymbolA = keys[i], SymbolB = keys[j],
                        Coefficient = Pearson(returns[keys[i]], returns[keys[j]])
                    });
            return pairs;
        }

        public static decimal[,] BuildMatrix(List<string> symbols, Dictionary<string, List<decimal>> returns)
        {
            var n = symbols.Count;
            var m = new decimal[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    m[i, j] = i == j ? 1m :
                        (returns.ContainsKey(symbols[i]) && returns.ContainsKey(symbols[j]))
                        ? Pearson(returns[symbols[i]], returns[symbols[j]]) : 0m;
            return m;
        }
    }

    // =========================================================================
    // 섹터 RS (기능 G)
    // =========================================================================
    public static class SectorAnalyzer
    {
        public static async Task<List<SectorStrength>> AnalyzeAsync(
            IQuoteProvider provider,
            IEnumerable<(string sym, Market mkt)> symbols,
            int days = 30, CancellationToken ct = default)
        {
            var sectorReturns = new Dictionary<Sector, List<decimal>>();
            var counts = new Dictionary<Sector, int>();

            foreach (var (sym, mkt) in symbols)
            {
                try
                {
                    var candles = await provider.GetHistoryAsync(sym, mkt,
                        DateTime.Now.AddDays(-days - 5), DateTime.Now, ct);
                    if (candles.Count < 2) continue;

                    var first = candles.First().Close;
                    var last = candles.Last().Close;
                    if (first == 0) continue;
                    var ret = (last - first) / first * 100m;

                    var sector = SectorMap.Resolve(sym);
                    if (!sectorReturns.ContainsKey(sector))
                    {
                        sectorReturns[sector] = new List<decimal>();
                        counts[sector] = 0;
                    }
                    sectorReturns[sector].Add(ret);
                    counts[sector]++;
                }
                catch (Exception ex) { Log.Warn($"[Sector] {sym}: {ex.Message}"); }
            }

            // 시장 평균
            var allRet = sectorReturns.Values.SelectMany(x => x).ToList();
            var marketAvg = allRet.Count > 0 ? allRet.Average() : 0m;

            return sectorReturns.Select(kv => new SectorStrength
            {
                Sector = kv.Key,
                Name = SectorMap.Korean(kv.Key),
                ReturnPct = kv.Value.Average(),
                RelativeStrength = kv.Value.Average() - marketAvg,
                MemberCount = counts[kv.Key]
            }).OrderByDescending(x => x.RelativeStrength).ToList();
        }
    }

    // =========================================================================
    // 세금 시뮬레이터 (기능 C)
    // =========================================================================
    public static class TaxSimulator
    {
        private const decimal AnnualDeduction = 2_500_000m;  // 해외주식 연 250만원
        private const decimal CgtRate = 0.20m;                // 양도세
        private const decimal LocalTaxRate = 0.02m;           // 지방소득세

        /// <summary>해외주식 매도 시 예상 세액 (올해 이미 실현한 수익도 반영)</summary>
        public static decimal EstimateUSCgt(decimal gainThisYear, decimal alreadyRealizedGain)
        {
            var totalGain = Math.Max(0, gainThisYear + alreadyRealizedGain);
            var taxable = Math.Max(0, totalGain - AnnualDeduction);
            // 이미 실현된 양도소득세는 이전에 냈을 것으로 가정하고 delta만 산정
            var prevTaxable = Math.Max(0, alreadyRealizedGain - AnnualDeduction);
            var prevTax = prevTaxable * (CgtRate + LocalTaxRate);
            var newTax = taxable * (CgtRate + LocalTaxRate);
            return Math.Max(0, newTax - prevTax);
        }

        /// <summary>보유 종목별 "지금 매도 시" 세후 수령액 시나리오</summary>
        public static TaxScenario SimulateSell(Holding h, decimal usdKrw, decimal alreadyRealizedGainKrw = 0)
        {
            var gainLocal = (h.CurrentPrice - h.AvgPrice) * h.Quantity;
            var grossProceeds = h.CurrentPrice * h.Quantity;
            decimal tax = 0;
            string rec;

            if (h.Currency == Currency.USD)
            {
                var gainKrw = gainLocal * usdKrw;
                tax = EstimateUSCgt(gainKrw, alreadyRealizedGainKrw);
                var usedDeduction = Math.Min(AnnualDeduction, Math.Max(0, AnnualDeduction - alreadyRealizedGainKrw));
                if (gainKrw <= usedDeduction)
                    rec = $"무세 구간 (공제 {Formatter.HumanizeNumber(usedDeduction - gainKrw)}원 여유)";
                else
                    rec = $"공제 초과, 예상 세액 {Formatter.HumanizeNumber(tax)}원";
            }
            else
            {
                // 국내 주식은 대주주 기준 미충족 가정 → 증권거래세만
                var cost = TradingCost.Default;
                tax = cost.SellTax(h.Market, grossProceeds);
                rec = $"국내 증권거래세 {tax:N0}원 (양도세 면제)";
            }

            return new TaxScenario
            {
                Symbol = h.Symbol,
                Quantity = h.Quantity,
                CurrentPrice = h.CurrentPrice,
                AvgCost = h.AvgPrice,
                UnrealizedGain = gainLocal,
                TaxIfSoldNow = tax,
                NetProceeds = (h.Currency == Currency.USD ? grossProceeds * usdKrw : grossProceeds) - tax,
                Recommendation = rec
            };
        }
    }
}

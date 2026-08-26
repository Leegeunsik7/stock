// ============================================================================
// HealthCheckService - 종합 건강검진 오케스트레이션 (Season 9 #1)
//  보유종목 일봉을 1회 수집해 기존 결정론 엔진을 모두 돌린다:
//   분산(RegimeDetector.AvgPairwiseCorrelation) · 리스크 기여(RiskAttribution) · 위기방어(BlackSwan 4종 최악)
//   · 미래전망(FutureCone) · 행동규율(RetroCoach) → HealthCheck.Compute로 종합 점수·등급·권고.
//  각 엔진은 독립 try로 감싸 일부 실패해도 나머지 축은 산출(부분 데이터는 중립으로 표기·거짓 점수 금지).
//  읽기전용·게이팅: 주문 경로 없음.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed record HealthView(bool IsValid, HealthScoreResult Score, RiskReport? Risk,
        BlackSwanResult? Worst, string WorstCrisis, FutureConeResult? Future, CoachReport? Behavior,
        int Priced, int Holdings, string Note);

    public static class HealthCheckService
    {
        public static async Task<HealthView> BuildAsync(LocalDatabase db, IQuoteProvider provider, FxConverter fx, CancellationToken ct = default)
        {
            decimal usdKrw = 1380m; try { usdKrw = await fx.GetUsdKrwAsync(); } catch { }

            // 보유 주식 종목별 합산(여러 계좌 수량 합치기 — First()로 떨구지 않음). 크립토 제외.
            var holdings = db.GetAllHoldings()
                .Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol) && h.Quantity > 0)
                .GroupBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(g => { var f = g.First(); return new Holding { Symbol = f.Symbol, Name = f.Name, Market = f.Market, Currency = f.Currency, CurrentPrice = f.CurrentPrice, Quantity = g.Sum(x => x.Quantity) }; })
                .ToList();
            int holdingCount = holdings.Count;
            if (holdingCount == 0)
                return new HealthView(false, Default(0), null, null, "", null, null, 0, 0, "보유 종목이 없습니다.");

            decimal Mv(Holding h) => h.CurrentPrice * h.Quantity * (h.Currency == Currency.USD ? usdKrw : 1m);

            // 종목별 일봉 수집(1회)
            var candleBySym = new Dictionary<string, List<Candle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in holdings)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var cs = (await provider.GetHistoryAsync(h.Symbol, h.Market, DateTime.Now.AddMonths(-18), DateTime.Now, ct))
                        .OrderBy(c => c.Date).ToList();
                    if (cs.Count >= 2) candleBySym[h.Symbol] = cs;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                await Task.Delay(120, ct);
            }

            var priced = holdings.Where(h => candleBySym.ContainsKey(h.Symbol)).ToList();
            int pricedCount = priced.Count;

            // 가중치(시세 있는 종목 기준, KRW MV 정규화)
            double totalMv = (double)priced.Sum(Mv);
            var weights = priced.Select(h => totalMv > 0 ? (double)Mv(h) / totalMv : 1.0 / Math.Max(1, pricedCount)).ToArray();
            var names = priced.Select(h => string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name).ToArray();

            // 자산 일수익률(%) — 혼합 캘린더 보정을 위해 '공통 거래일'로 정렬한 등길이 시리즈.
            // (KRX/US 휴장일이 달라 위치(index) 정렬은 부정확 → 날짜 교집합으로 정렬)
            double[][] retsPct;
            if (pricedCount == 0) retsPct = Array.Empty<double[]>();
            else
            {
                var maps = priced.Select(h => candleBySym[h.Symbol]
                    .GroupBy(c => c.Date.Date).ToDictionary(g => g.Key, g => g.Last().Close)).ToList();
                var common = new HashSet<DateTime>(maps[0].Keys);
                foreach (var m in maps.Skip(1)) common.IntersectWith(m.Keys);
                var commonDates = common.OrderBy(d => d).ToList();
                retsPct = commonDates.Count >= 2
                    ? maps.Select(m => RegimeDetector.ToReturns(commonDates.Select(d => m[d]).ToList())).ToArray()
                    : priced.Select(h => RegimeDetector.ToReturns(candleBySym[h.Symbol].Select(c => c.Close).ToList())).ToArray();
            }

            // ── 분산: 평균 페어 상관 ──
            double avgCorr = 0; bool hasRisk = false; RiskReport? risk = null;
            try { if (retsPct.Length >= 2) avgCorr = RegimeDetector.AvgPairwiseCorrelation(retsPct); } catch { }

            // ── 리스크 기여/집중 ──
            try
            {
                if (retsPct.Length >= 2 && retsPct.Min(r => r.Length) >= 2)
                {
                    risk = RiskAttribution.Analyze(retsPct, names, weights);
                    hasRisk = risk.IsValid;
                }
            }
            catch { }
            double topConc = hasRisk ? risk!.TopConcentrationPct : 0.0;   // 미가용이면 점수에 미반영(중립 sentinel 제거)
            double evenShare = pricedCount > 0 ? 100.0 / pricedCount : 100.0;

            // ── 위기 방어: 블랙스완 4종 중 최악(가장 깊은 낙폭) ──
            BlackSwanResult? worst = null; string worstName = ""; bool hasCrisis = false;
            try
            {
                var bsHoldings = priced.Select((h, i) => new BlackSwanHolding(h.Symbol, names[i], weights[i], 1.0)).ToList();
                if (bsHoldings.Count > 0)
                {
                    foreach (var prof in BlackSwan.Library)
                    {
                        var r = BlackSwan.Inject(prof, bsHoldings);
                        if (r.IsValid && (worst == null || r.MaxDrawdownPct < worst.MaxDrawdownPct)) { worst = r; worstName = prof.Name; }
                    }
                    hasCrisis = worst != null;
                }
            }
            catch { }

            // ── 미래 전망: 포트폴리오 일수익률(소수) 후행정렬 가중합 → FutureCone ──
            FutureConeResult? future = null; bool hasFuture = false;
            try
            {
                if (retsPct.Length >= 1)
                {
                    int t = retsPct.Min(r => r.Length);
                    if (t >= 20)
                    {
                        var port = new double[t];
                        for (int i = 0; i < retsPct.Length; i++)
                        {
                            var r = retsPct[i]; int off = r.Length - t;
                            for (int k = 0; k < t; k++) port[k] += weights[i] * (r[off + k] / 100.0);   // % → 소수
                        }
                        future = FutureCone.Project(port, 21, 500, 12345, goalPct: 0);
                        hasFuture = future.IsValid;
                    }
                }
            }
            catch { }

            // ── 행동 규율: 거래 이력 → 회고 편향(경고·주의 수) ──
            CoachReport? beh = null; int biasWarn = 0; bool hasBeh = false;
            try
            {
                var coachTxs = LoadCoachTxs(db, candleBySym);
                if (coachTxs.Count > 0)
                {
                    beh = RetroCoach.Compute(coachTxs);
                    hasBeh = beh.IsValid && beh.Stats.ClosedTrades > 0;
                    biasWarn = beh.Insights.Count(i => i.Severity is "high" or "warning");
                }
            }
            catch { }

            var inputs = new HealthInputs(holdingCount, pricedCount, avgCorr, topConc, evenShare,
                worst?.MaxDrawdownPct ?? 0, future?.ProbLossPct ?? 0, biasWarn,
                hasRisk, hasCrisis, hasFuture, hasBeh);
            var score = HealthCheck.Compute(inputs);

            string note = pricedCount < holdingCount ? $"{holdingCount}종목 중 {pricedCount}종목 시세 확보" : $"{holdingCount}종목";
            return new HealthView(true, score, risk, worst, worstName, future, beh, pricedCount, holdingCount, note);
        }

        private static List<CoachTx> LoadCoachTxs(LocalDatabase db, Dictionary<string, List<Candle>> candleBySym)
        {
            var rows = db.Query("SELECT Symbol,Type,Quantity,Price,Commission,Tax,ExecutedAt FROM Transactions ORDER BY ExecutedAt").Rows;
            double? Prior(string sym, DateTime when)
            {
                if (!candleBySym.TryGetValue(sym, out var cs)) return null;
                int idx = -1; for (int i = cs.Count - 1; i >= 0; i--) if (cs[i].Date.Date <= when.Date) { idx = i; break; }
                if (idx < 5) return null;
                decimal p0 = cs[idx - 5].Close, p1 = cs[idx].Close;
                return p0 > 0 ? (double)((p1 - p0) / p0) * 100.0 : (double?)null;
            }
            return rows.Cast<DataRow>().Select(r =>
            {
                var sym = Safe.Str(r["Symbol"]); var when = Safe.DT(r["ExecutedAt"], DateTime.MinValue);
                return new CoachTx(sym, (TransactionType)Safe.Int(r["Type"]), Safe.Int(r["Quantity"]),
                    Safe.Dec(r["Price"]), Safe.Dec(r["Commission"]) + Safe.Dec(r["Tax"]), when, Prior(sym, when));
            }).Where(t => t.ExecutedAt > DateTime.MinValue).ToList();
        }

        private static HealthScoreResult Default(int n) =>
            HealthCheck.Compute(new HealthInputs(n, 0, 0, 0, 100, 0, 0, 0, false, false, false, false));
    }
}

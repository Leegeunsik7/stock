// ============================================================================
// RetroCoachService - 회고 코치 오케스트레이션 (Season 8 #4)
//  Transactions 로드 → 종목별 일봉으로 '체결 직전 5일 수익률'(추격/패닉 판정)과 '현재가'(반사실) 산출
//  → CoachTx 빌드 → RetroCoach.Compute(순수) → 조기매도 기회손익(보유했다면, FX→KRW) → (옵트인)LLM 서술.
//  결정론 수치는 LLM 없이도 완전히 렌더된다(LLM은 ExplainDayAsync 패턴으로 '서술'만 · 환각 방화벽).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed record CoachView(CoachReport Report, decimal SoldHeldDeltaKrw,
        IReadOnlyList<MissedEntry> TopMissed, int SymbolsPriced, string Note);

    public sealed record MissedEntry(string Symbol, decimal DeltaKrw);

    public static class RetroCoachService
    {
        public static async Task<CoachView> BuildAsync(LocalDatabase db, IQuoteProvider provider, FxConverter fx, CancellationToken ct = default)
        {
            var rows = db.Query("SELECT Symbol,Type,Quantity,Price,Commission,Tax,ExecutedAt FROM Transactions ORDER BY ExecutedAt").Rows;
            var raw = rows.Cast<DataRow>().Select(r => new RawTx(
                Safe.Str(r["Symbol"]), (TransactionType)Safe.Int(r["Type"]), Safe.Int(r["Quantity"]),
                Safe.Dec(r["Price"]), Safe.Dec(r["Commission"]) + Safe.Dec(r["Tax"]),
                Safe.DT(r["ExecutedAt"], DateTime.MinValue))).Where(t => t.When > DateTime.MinValue).ToList();

            if (raw.Count == 0)
                return new CoachView(RetroCoach.Compute(Array.Empty<CoachTx>()), 0, Array.Empty<MissedEntry>(), 0, "거래 이력이 없습니다.");

            decimal usdKrw = 1380m; try { usdKrw = await fx.GetUsdKrwAsync(); } catch { }

            // 통화는 '심볼 모양 추측'이 아니라 보유 정보(Currency)에서 도출 — 비KRX·비USD 종목의 ~1380배 환산 오류 방지
            var curBySym = new Dictionary<string, Currency>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var h in db.GetAllHoldings()) curBySym[h.Symbol] = h.Currency; } catch { }

            // 종목별 일봉(직전5일 수익률 + 현재가). 주식만(6자리 숫자=KRX, 그 외 US). 실패 종목은 가격 맥락 없이 진행.
            var candleBySym = new Dictionary<string, List<Candle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in raw.Select(t => t.Symbol).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var from = raw.Where(t => t.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase)).Min(t => t.When).AddDays(-20);
                    var cs = (await provider.GetHistoryAsync(sym, Detect(sym), from, DateTime.Now, ct)).OrderBy(c => c.Date).ToList();
                    if (cs.Count > 0) candleBySym[sym] = cs;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                await Task.Delay(120, ct);   // API 한도 보호
            }

            double? PriorRet(string sym, DateTime when)
            {
                if (!candleBySym.TryGetValue(sym, out var cs)) return null;
                int idx = -1;
                for (int i = cs.Count - 1; i >= 0; i--) if (cs[i].Date.Date <= when.Date) { idx = i; break; }
                if (idx < 5) return null;
                decimal p0 = cs[idx - 5].Close, p1 = cs[idx].Close;
                return p0 > 0 ? (double)((p1 - p0) / p0) * 100.0 : (double?)null;
            }

            var coachTxs = raw.Select(t => new CoachTx(t.Symbol, t.Type, t.Qty, t.Price, t.Fees, t.When, PriorRet(t.Symbol, t.When))).ToList();
            var report = await Task.Run(() => RetroCoach.Compute(coachTxs), ct);

            // 반사실: 매도분을 지금까지 보유했다면? (+: 보유가 유리=조기매도 손해 / −: 매도가 손실 회피)
            decimal totalDelta = 0; var perSym = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var tr in report.Trades)
            {
                if (!candleBySym.TryGetValue(tr.Symbol, out var cs) || cs.Count == 0) continue;
                var last = cs[^1];
                if (last.Date.Date < tr.ExitDate.Date) continue;   // 최신 캔들이 청산일 이전(당일매도·상폐 데이터) → '현재가' 가정 무효, 제외
                decimal now = last.Close;
                bool usd = IsUsd(tr.Symbol, curBySym);
                decimal fxr = usd ? usdKrw : 1m;
                decimal delta = (tr.Qty * now - tr.Qty * tr.ExitPrice) * fxr;
                totalDelta += delta;
                perSym[tr.Symbol] = perSym.TryGetValue(tr.Symbol, out var e) ? e + delta : delta;
            }
            var top = perSym.OrderByDescending(kv => Math.Abs(kv.Value)).Take(6).Select(kv => new MissedEntry(kv.Key, kv.Value)).ToList();

            return new CoachView(report, totalDelta, top, candleBySym.Count, report.Note);
        }

        public static async Task<string> NarrateAsync(AiService ai, CoachReport report)
        {
            if (ai == null || !ai.HasLlm)
                return "⚠️ LLM이 설정되지 않았습니다. /settings에서 Gemini 키 또는 Ollama를 활성화하면 코칭 서술을 생성합니다.";
            if (!report.IsValid) return "분석할 거래 이력이 없습니다.";
            string prompt =
                "당신은 신중한 투자 코치입니다. 아래 '실제 거래 통계와 탐지된 패턴'에 적힌 사실만 근거로 사후 복기 코칭을 한국어로 작성하세요. " +
                "규칙: ①기록에 없는 수치·사건을 지어내지 마세요 ②단정 대신 '~로 보입니다/추정'으로 완화 ③행동→결과를 연결해 설명 " +
                "④표본이 적으면 '기록만으로는 단정하기 어렵다'고 명시 ⑤투자 권유가 아닌 행동 습관 개선 제안, 평문 3~5문장.\n\n" +
                RetroCoach.FactsText(report);
            return await ai.CompleteAsync(prompt);
        }

        private static Market Detect(string s) => s.Length == 6 && char.IsDigit(s[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ;

        // USD 여부: 보유 통화 우선. 청산되어 보유정보가 없으면 순수 알파벳 1~5자만 USD로 간주(숫자·하이픈·점은 KRW로 보고 FX 미적용).
        private static bool IsUsd(string sym, Dictionary<string, Currency> cur)
        {
            if (cur.TryGetValue(sym, out var c)) return c == Currency.USD;
            return sym.Length is >= 1 and <= 5 && sym.All(char.IsLetter);
        }

        private sealed record RawTx(string Symbol, TransactionType Type, int Qty, decimal Price, decimal Fees, DateTime When);
    }
}

// ============================================================================
// StrategySignalService - 전략 신호 모니터 오케스트레이션 (Season 9 #4)
//  저장된 카트리지 전략(MA크로스·컴파일러 스펙)을 모아, 현재 종목 히스토리에서 각 전략의 '지금 신호 상태'
//  (신규 진입/청산/보유/관망)를 결정론으로 평가한다. 내장 벤치마크는 스펙으로 표현 불가라 제외.
//  ⚠️ 정보 제공(전략 규칙의 현재 상태)일 뿐 매매 지시·자동 주문이 아니다(게이팅·읽기전용).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed record SignalRow(string Name, string Source, string State, string Detail, bool InPosition);
    public sealed record SignalView(bool IsValid, string Symbol, DateTime AsOf, IReadOnlyList<SignalRow> Rows, string Note);

    public static class StrategySignalService
    {
        public static async Task<SignalView> BuildAsync(LocalDatabase db, IQuoteProvider provider, string symbol, Market market, CancellationToken ct = default)
        {
            var sym = (symbol ?? "").Trim().ToUpperInvariant();
            var entrants = StrategyTournamentService.GatherEntrants(out var carts, out var from, out _)
                .Where(e => e.Kind != EntryKind.BuiltIn).ToList();   // 내장 벤치는 스펙 표현 불가 → 제외
            if (string.IsNullOrWhiteSpace(sym))
                return new SignalView(false, "", default, Array.Empty<SignalRow>(), "종목코드를 입력하세요.");
            if (entrants.Count == 0)
                return new SignalView(false, sym, default, Array.Empty<SignalRow>(), $"저장된 전략이 없습니다(카트리지 {carts}개). /compiler·/forge에서 만들어 서명하세요.");

            List<Candle> candles;
            try { candles = (await provider.GetHistoryAsync(sym, market, DateTime.Now.AddYears(-2), DateTime.Now, ct)).OrderBy(c => c.Date).ToList(); }
            catch (Exception ex) { return new SignalView(false, sym, default, Array.Empty<SignalRow>(), $"{sym} 시세 조회 실패: {ex.Message}"); }
            if (candles.Count < 30) return new SignalView(false, sym, default, Array.Empty<SignalRow>(), $"{sym} 캔들 부족(최소 30거래일).");

            var rows = await Task.Run(() => entrants.Select(e =>
            {
                var spec = e.Kind == EntryKind.MaCross ? StrategyCompiler.FromMaCross(e.Short, e.Long) : e.Spec;
                if (spec == null) return null;
                var sig = StrategyCompiler.CurrentSignal(spec, candles);
                return new SignalRow(e.Name, e.Source, sig.State, sig.Detail, sig.InPosition);
            }).Where(r => r != null).Select(r => r!)
              .OrderBy(r => StateRank(r.State)).ThenBy(r => r.Name, StringComparer.Ordinal).ToList(), ct);

            var asOf = candles[^1].Date;
            return new SignalView(true, sym, asOf, rows, $"전략 {rows.Count}개 · 기준일 {asOf:yyyy-MM-dd}");
        }

        // 정렬: 신규 진입/청산(행동 필요) 먼저, 그다음 보유, 관망
        private static int StateRank(string s) => s switch { "신규 진입" => 0, "청산 신호" => 1, "보유 중" => 2, _ => 3 };
    }
}

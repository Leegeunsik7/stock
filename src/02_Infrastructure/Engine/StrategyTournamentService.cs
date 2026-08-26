// ============================================================================
// StrategyTournamentService - 전략 토너먼트 오케스트레이션 (Season 9 #2)
//  저장된 .ms2cart 카트리지에서 출전 전략을 모은다: CartStrategy(MA크로스) + CartPrompt(컴파일러 스펙, 디코드)
//  + 내장 벤치마크 5종. 현재 종목 2년 일봉으로 워크포워드 OOS 리그를 돌려 랭킹한다. 읽기전용·게이팅.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed record TournamentView(LeagueResult League, int Cartridges, int FromCartridges, string Note);

    public static class StrategyTournamentService
    {
        public static async Task<TournamentView> BuildAsync(LocalDatabase db, IQuoteProvider provider, string symbol, Market market, CancellationToken ct = default)
        {
            var sym = (symbol ?? "").Trim().ToUpperInvariant();
            var entrants = GatherEntrants(out int carts, out int fromCarts, out int decodeFailed);
            if (string.IsNullOrWhiteSpace(sym))
                return new TournamentView(new LeagueResult(false, Array.Empty<LeagueRow>(), 0, entrants.Count, "종목코드를 입력하세요."), carts, fromCarts, "");

            List<Candle> candles;
            try { candles = (await provider.GetHistoryAsync(sym, market, DateTime.Now.AddYears(-2), DateTime.Now, ct)).OrderBy(c => c.Date).ToList(); }
            catch (Exception ex) { return new TournamentView(new LeagueResult(false, Array.Empty<LeagueRow>(), 0, entrants.Count, $"{sym} 시세 조회 실패: {ex.Message}"), carts, fromCarts, ""); }

            var league = await Task.Run(() => StrategyTournament.Run(entrants, candles, market), ct);
            string note = $"카트리지 {carts}개 → 전략 {fromCarts}개 + 내장 벤치 {BacktestEngine.StrategyNames.Length}개"
                + (decodeFailed > 0 ? $" · 디코드 실패 {decodeFailed}건" : "");
            return new TournamentView(league, carts, fromCarts, note);
        }

        /// <summary>저장 카트리지(MA크로스·스펙) + 내장 벤치마크를 출전자로 수집(중복 제거). MA로 환원되는 스펙은 MA 엔진으로 단일화.</summary>
        public static List<Entrant> GatherEntrants(out int cartridges, out int fromCartridges, out int decodeFailed)
        {
            var list = new List<Entrant>();
            var seenMa = new HashSet<(int, int)>();
            var seenSpec = new HashSet<string>();
            cartridges = 0; fromCartridges = 0; decodeFailed = 0;

            foreach (var path in CartridgeService.ListFiles())
            {
                if (!CartridgeService.TryImport(path, out var cart, out _) || cart == null) continue;
                cartridges++;
                foreach (var s in cart.Strategies)
                {
                    if (s.Short < 1 || s.Long <= s.Short) continue;
                    if (seenMa.Add((s.Short, s.Long)))
                    { list.Add(new Entrant($"MA {s.Short}/{s.Long}", Short(cart.Name), EntryKind.MaCross, s.Short, s.Long, -1, null)); fromCartridges++; }
                }
                foreach (var p in cart.Prompts)
                {
                    if (string.IsNullOrEmpty(p.Id) || !p.Id.StartsWith("strategy-spec:v1:", StringComparison.Ordinal)) continue;
                    if (!StrategyCompiler.TryParseCanonical(p.Template, out var spec) || spec == null) { decodeFailed++; continue; }
                    // MA 골든/데드크로스로 환원되는 스펙은 동일 전략 → MA 엔진으로 단일화(이중 출전·이중 엔진 방지)
                    if (StrategyCompiler.ReducesToMaCross(spec, out var sp, out var lp))
                    {
                        if (!seenMa.Add((sp, lp))) continue;
                        list.Add(new Entrant($"MA {sp}/{lp}", Short(cart.Name), EntryKind.MaCross, sp, lp, -1, null)); fromCartridges++;
                    }
                    else if (seenSpec.Add(spec.Id))
                    { list.Add(new Entrant($"스펙 {spec.Id}", Short(cart.Name), EntryKind.Spec, 0, 0, -1, spec)); fromCartridges++; }
                }
            }

            // 내장 벤치마크(비교 기준)
            for (int i = 0; i < BacktestEngine.StrategyNames.Length; i++)
                list.Add(new Entrant($"내장: {BacktestEngine.StrategyNames[i]}", "벤치마크", EntryKind.BuiltIn, 0, 0, i, null));

            return list;
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "카트리지" : (s.Length <= 18 ? s : s.Substring(0, 18));
    }
}

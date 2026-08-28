// ============================================================================
// StrategyForgeAdapter — Phase 94-E: 전략 단조소 Port 호스트 어댑터 (DIP 구현)
//   순수 UI 의 IStrategyForgePort 를 콘크리트(StrategyForge·CartridgeEngine·CartridgeService)와 시세/DB
//   (IQuoteProvider·LocalDatabase)에 결속. UI 는 시세/엔진/카트리지/DB 콘크리트를 0개 참조 — 어댑터가 격리.
//   ★liveArmed:false: 순수 발굴/서명/내보내기 — 발주·소켓 무접촉(StrategyForge 는 결정론 발굴만).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Strategy;

namespace MS2E.Infrastructure
{
    public sealed class StrategyForgeAdapter : IStrategyForgePort
    {
        private const int AttestSeed = 7;
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;

        public StrategyForgeAdapter(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
        }

        // 6자리 숫자 → KRX, 그 외 → US(원본 ForgePage.Detect 룰 계승).
        private static Market Detect(string s) =>
            s.Length == 6 && char.IsDigit(s[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ;

        public async Task<IForgeReport> ForgeAsync(string symbol, int folds, int years = 2, CancellationToken ct = default)
        {
            var sym = (symbol ?? "").Trim().ToUpperInvariant();
            if (sym.Length == 0)
                return new ForgeResult(false, Array.Empty<ForgeCandidate>(), 0, folds, "종목코드가 비었습니다.");
            int y = years <= 0 ? 2 : years;
            var candles = await _provider
                .GetHistoryAsync(sym, Detect(sym), DateTime.Now.AddYears(-y), DateTime.Now, ct)
                .ConfigureAwait(false);
            candles = candles.OrderBy(c => c.Date).ToList();
            return await Task.Run<IForgeReport>(() => StrategyForge.Run(candles, folds), ct).ConfigureAwait(false);
        }

        public Task<string> ExportCartridgeAsync(string cartridgeName, string description,
            IReadOnlyList<ForgePick> picks, CancellationToken ct = default)
        {
            if (picks is null || picks.Count == 0) return Task.FromResult("");
            var strategies = picks
                .Select(k => CartridgeEngine.Attest($"MA {k.Short}/{k.Long}", k.Short, k.Long, AttestSeed))
                .ToList();
            var cart = CartridgeService.BuildSigned(_db, cartridgeName, description, strategies);
            var path = CartridgeService.ExportToFile(cart);
            return Task.FromResult(Path.GetFileName(path));
        }
    }
}

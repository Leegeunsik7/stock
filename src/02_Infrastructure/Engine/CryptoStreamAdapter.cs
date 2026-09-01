// ============================================================================
// CryptoStreamAdapter — Phase 94-O: ICryptoStreamPort 호스트 어댑터 (CoinPage 이주 기반)
//   업비트·빗썸·바이낸스·환율·모의보유를 병합해 포터블 스냅샷 산출(CoinPage.RefreshAsync 로직 이관).
//   각 소스 독립 try/catch → 실패 소스는 직전 값 유지(fail-closed). ★모의주문 전용(실 발주 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Realtime;
using StockManager.Blazor;

namespace MS2E.Infrastructure
{
    public sealed class CryptoStreamAdapter : ICryptoStreamPort
    {
        private readonly DashboardService _svc;
        private readonly BithumbProvider _bithumb;
        private readonly FxConverter _fx;
        private readonly UpbitProvider _upbit = new();
        private readonly BinanceProvider _binance = new();

        // 직전 값 유지(fail-closed) 캐시 — 원본 CoinPage 필드 룰 계승.
        private decimal _usdKrw = 1380m;
        private Dictionary<string, BinanceTick> _bn = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _krwMarkets = new();
        private Dictionary<string, string> _nameMap = new(StringComparer.OrdinalIgnoreCase);

        public CryptoStreamAdapter(DashboardService svc, BithumbProvider bithumb, FxConverter fx)
        { _svc = svc; _bithumb = bithumb; _fx = fx; }

        public async Task<ICryptoBoardSnapshot> LoadBoardAsync(CancellationToken ct)
        {
            // 환율 + 바이낸스 USDT(김프 교차) — 실패 시 직전 값 유지.
            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { }
            try { var bn = await _binance.GetAllUsdt24hAsync(); if (bn.Count > 0) _bn = bn; } catch { }

            // 빗썸: 전체 종목(인증 불필요·기준 데이터).
            List<BithumbTick> bithumb = new();
            try { bithumb = await _bithumb.GetAllTickersAsync(); } catch { }

            // 업비트: 실제 KRW 마켓 목록으로만 조회. 마켓 목록은 1회 로드 후 캐싱.
            List<CryptoTick> upbit = new();
            try
            {
                if (_krwMarkets.Count == 0)
                {
                    var markets = await _upbit.GetMarketsAsync();
                    if (markets.Count > 0)
                    {
                        _nameMap = markets.ToDictionary(m => m.Market.Replace("KRW-", ""), m => m.KoreanName, StringComparer.OrdinalIgnoreCase);
                        _krwMarkets = markets.Select(m => m.Market).ToList();
                    }
                }
                if (_krwMarkets.Count > 0)
                {
                    var ticks = new List<CryptoTick>();
                    for (int i = 0; i < _krwMarkets.Count; i += 100)
                        ticks.AddRange(await _upbit.GetTickersAsync(_krwMarkets.Skip(i).Take(100)));
                    upbit = ticks;
                }
            }
            catch { }

            // 모의 보유(코인 상태).
            var held = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var state = _svc.GetCryptoState();
                held = state.Holdings.Where(kv => kv.Value.Qty > 0)
                                     .ToDictionary(k => k.Key, v => v.Value.Qty, StringComparer.OrdinalIgnoreCase);
            }
            catch { }

            // ── 병합: 업비트 ∪ 빗썸 심볼, 거래소별 가격/등락/거래대금 + 바이낸스 USDT(김프 교차) ──
            var uMap = new Dictionary<string, CryptoTick>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in upbit) uMap[u.Market.Replace("KRW-", "")] = u;
            var bMap = new Dictionary<string, BithumbTick>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bithumb) bMap[b.Symbol] = b;

            var quotes = new List<CoinQuoteRow>();
            foreach (var sym in uMap.Keys.Union(bMap.Keys, StringComparer.OrdinalIgnoreCase))
            {
                uMap.TryGetValue(sym, out var u);
                bMap.TryGetValue(sym, out var b);
                decimal up = u?.Price ?? 0m, bp = b?.Price ?? 0m;
                // 원본 룰: 업비트 changeRate 는 소수(×100), 빗썸 changeRate 는 이미 %.
                decimal change = u != null ? u.ChangeRate * 100m : (b?.ChangeRate ?? 0m);
                decimal tradeVal = Math.Max(u?.AccTradePrice24h ?? 0m, b?.TradeValueKrw ?? 0m);
                decimal bnUsdt = _bn.TryGetValue(sym + "USDT", out var bt) ? bt.Price : 0m;
                var name = u?.KoreanName is { Length: > 0 } kn ? kn
                         : (_nameMap.TryGetValue(sym, out var nm) && nm.Length > 0 ? nm : sym);
                quotes.Add(new CoinQuoteRow(sym, name, up, bp, bnUsdt, change, tradeVal));
            }

            int allSymbols = uMap.Keys.Union(bMap.Keys, StringComparer.OrdinalIgnoreCase).Count();
            return new CryptoBoardSnapshot(quotes, held, _usdKrw, upbit.Count, bithumb.Count, _bn.Count, allSymbols);
        }

        public (bool ok, string message) QuickPaper(string market, decimal krw, bool isBuy)
            => _svc.QuickPaper(market, krw, isBuy ? PaperSide.Buy : PaperSide.Sell);

        // ── 포터블 뷰 구현(공변) ──
        private sealed record CoinQuoteRow(string Symbol, string Name, decimal UpbitPx, decimal BithumbPx,
            decimal BnUsdt, decimal Change, decimal TradeVal) : ICoinQuoteView;

        private sealed record CryptoBoardSnapshot(
            IReadOnlyList<ICoinQuoteView> Quotes, IReadOnlyDictionary<string, decimal> Held,
            decimal UsdKrw, int UpbitCount, int BithumbCount, int BinanceCount, int AllSymbols) : ICryptoBoardSnapshot;
    }
}

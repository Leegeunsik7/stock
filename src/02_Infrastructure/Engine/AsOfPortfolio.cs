// ============================================================================
// AsOfPortfolio - "그 시점의 보유 포지션" 재구성 · 순수 코어 (Season 6 #3)
//  거래 이력을 특정 시점(asOf)까지만 시간순 재집계해 그때의 수량·평단·매입원가를 복원한다.
//  룩어헤드 차단: ExecutedAt <= asOf 거래만 사용. 평단 규약은 PortfolioService.RecomputeAverage와 동일:
//   · 매수: 원가 += 단가×수량, 수량 += 수량
//   · 매도: 보유 0이면 무시 / 전량매도면 원가·수량 0 / 부분매도면 평단 유지·원가 비례 차감
//  외부 의존 0 · 결정론적 → 단위 테스트로 고정.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public sealed record AsOfTx(string Symbol, TransactionType Type, decimal Quantity, decimal Price, DateTime ExecutedAt);

    public sealed record AsOfHolding(string Symbol, decimal Quantity, decimal AvgCost, decimal CostBasis)
        : MS2E.UI.Portable.Ports.Portfolio.IAsOfHoldingView;   // Phase 94-J: 공변 구현(전 멤버 위치속성 일치)

    public static class AsOfPortfolio
    {
        public static List<AsOfHolding> Reconstruct(IReadOnlyList<AsOfTx> txs, DateTime asOf)
        {
            var bySym = new Dictionary<string, (decimal qty, decimal cost)>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in txs.Where(x => x.ExecutedAt <= asOf).OrderBy(x => x.ExecutedAt))
            {
                var cur = bySym.TryGetValue(t.Symbol, out var v) ? v : (qty: 0m, cost: 0m);

                if (t.Type == TransactionType.Buy)
                {
                    cur.cost += t.Price * t.Quantity;
                    cur.qty += t.Quantity;
                }
                else if (t.Type == TransactionType.Sell)
                {
                    if (cur.qty == 0) continue;                                 // 매도할 수량 없음 → 무시
                    if (t.Quantity >= cur.qty) { cur.cost = 0; cur.qty = 0; }   // 전량(또는 초과) 매도 → 평탄
                    else { cur.cost = cur.cost * (cur.qty - t.Quantity) / cur.qty; cur.qty -= t.Quantity; }  // 이동평균: 평단 유지·원가 비례 차감
                }
                // Dividend/Split/Deposit/Withdraw 등은 보유 수량/평단에 영향 없음(단순화)

                bySym[t.Symbol] = cur;
            }

            return bySym
                .Where(kv => kv.Value.qty > 0)
                .Select(kv => new AsOfHolding(kv.Key, kv.Value.qty, kv.Value.cost / kv.Value.qty, kv.Value.cost))
                .OrderBy(h => h.Symbol, StringComparer.Ordinal)
                .ToList();
        }
    }
}

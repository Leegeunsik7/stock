// ============================================================================
// PaperBroker - 모의투자(Paper Trading) 브로커 엔진 (v3.0)
//  · 가상 시드머니(KRW) 기반. 실거래 API 미연동 — 자금 이동 없음.
//  · 시장가/지정가 주문, 매수/매도/취소, 포지션 가중평단, 주문 이력.
//  · Settings 'Paper.Account' JSON 영속. 주식(KRW 환산) + 코인 공통.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MS2E.Infrastructure
{
    public enum PaperAsset { Stock, Coin }
    public enum PaperSide { Buy, Sell }
    public enum PaperKind { Market, Limit }
    public enum PaperStatus { Pending, Filled, Canceled, Rejected }

    public class PaperOrder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime Time { get; set; } = DateTime.Now;
        public PaperAsset Asset { get; set; }
        public Market StockMarket { get; set; } = Market.KRX_KOSPI;  // 주식 시세 조회용
        public string Symbol { get; set; } = "";   // 코인: "KRW-BTC", 주식: 종목코드
        public string Name { get; set; } = "";
        public PaperSide Side { get; set; }
        public PaperKind Kind { get; set; }
        public decimal Qty { get; set; }
        public decimal LimitPrice { get; set; }    // 지정가
        public PaperStatus Status { get; set; } = PaperStatus.Pending;
        public decimal FillPrice { get; set; }
        public DateTime? FillTime { get; set; }
    }

    public class PaperPosition
    {
        public PaperAsset Asset { get; set; }
        public Market StockMarket { get; set; } = Market.KRX_KOSPI;
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal Avg { get; set; }            // 가중 평단 (KRW)
    }

    public class PaperAccount
    {
        public decimal Seed { get; set; } = 10_000_000m;   // 초기 시드 (기본 1,000만)
        public decimal Cash { get; set; } = 10_000_000m;
        public List<PaperPosition> Positions { get; set; } = new();
        public List<PaperOrder> Orders { get; set; } = new();
    }

    public class PaperBroker : IBroker
    {
        public const decimal FeeRate = 0.00015m;   // 0.015% 수수료(모의)
        public string Name => "모의투자";
        public bool IsLive => false;
        private readonly LocalDatabase _db;
        public PaperAccount Account { get; private set; } = new();

        public PaperBroker(LocalDatabase db)
        {
            _db = db;
            Load();
        }

        private void Load()
        {
            try
            {
                var j = _db.GetSetting("Paper.Account");
                if (!string.IsNullOrWhiteSpace(j))
                    Account = JsonSerializer.Deserialize<PaperAccount>(j) ?? new PaperAccount();
            }
            catch { Account = new PaperAccount(); }
        }

        public void Save()
        {
            try { _db.SetSetting("Paper.Account", JsonSerializer.Serialize(Account)); } catch { }
        }

        /// <summary>Settings('Paper.Account')를 다시 읽어 메모리 상태를 최신화.
        /// 모든 변경이 즉시 Save되므로 다중 인스턴스(WinForms 창·백그라운드 엔진·Blazor) 간
        /// 데이터 일관성은 "재로드 후 작업" 패턴으로 보장한다.</summary>
        public void Reload() => Load();

        public void ResetSeed(decimal seed)
        {
            Account = new PaperAccount { Seed = seed, Cash = seed };
            Save();
        }

        private PaperPosition? Pos(PaperAsset a, string sym) =>
            Account.Positions.FirstOrDefault(p => p.Asset == a && p.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));

        public decimal HeldQty(PaperAsset a, string sym) => Pos(a, sym)?.Qty ?? 0m;

        /// <summary>주문 접수. 시장가는 즉시 체결, 지정가는 대기 등록.</summary>
        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            if (qty <= 0) return (false, "수량이 올바르지 않습니다.");
            var order = new PaperOrder
            {
                Asset = asset, Symbol = symbol, Name = name, Side = side, Kind = kind,
                Qty = qty, LimitPrice = limitPrice
            };

            if (kind == PaperKind.Market)
            {
                if (currentPrice <= 0) return (false, "현재가를 확인할 수 없습니다.");
                var r = TryFill(order, currentPrice);
                Account.Orders.Insert(0, order);
                Save();
                return r;
            }
            else
            {
                if (limitPrice <= 0) return (false, "지정가를 입력하세요.");
                // 매수 지정가는 현금이 충분한지 사전 점검(참고용)
                if (side == PaperSide.Buy && Account.Cash < qty * limitPrice * (1 + FeeRate))
                    return (false, "현금이 부족합니다(지정가 기준).");
                if (side == PaperSide.Sell && HeldQty(asset, symbol) < qty)
                    return (false, "보유 수량이 부족합니다.");
                Account.Orders.Insert(0, order);
                Save();
                return (true, $"지정가 주문 접수: {name} {qty} @ {limitPrice:N0}");
            }
        }

        private (bool ok, string msg) TryFill(PaperOrder o, decimal price)
        {
            if (o.Side == PaperSide.Buy)
            {
                var cost = o.Qty * price * (1 + FeeRate);
                if (Account.Cash < cost) { o.Status = PaperStatus.Rejected; return (false, "현금이 부족합니다."); }
                Account.Cash -= cost;
                var p = Pos(o.Asset, o.Symbol);
                if (p == null)
                    Account.Positions.Add(new PaperPosition { Asset = o.Asset, StockMarket = o.StockMarket, Symbol = o.Symbol, Name = o.Name, Qty = o.Qty, Avg = price });
                else
                {
                    var totalCost = p.Avg * p.Qty + o.Qty * price;
                    p.Qty += o.Qty;
                    p.Avg = p.Qty > 0 ? totalCost / p.Qty : price;
                    if (string.IsNullOrEmpty(p.Name)) p.Name = o.Name;
                }
            }
            else // Sell
            {
                var p = Pos(o.Asset, o.Symbol);
                if (p == null || p.Qty < o.Qty) { o.Status = PaperStatus.Rejected; return (false, "보유 수량이 부족합니다."); }
                var proceeds = o.Qty * price * (1 - FeeRate);
                Account.Cash += proceeds;
                p.Qty -= o.Qty;
                if (p.Qty <= 0.00000001m) Account.Positions.Remove(p);
            }
            o.Status = PaperStatus.Filled;
            o.FillPrice = price;
            o.FillTime = DateTime.Now;
            return (true, $"체결: {o.Name} {(o.Side == PaperSide.Buy ? "매수" : "매도")} {o.Qty} @ {price:N0}");
        }

        public void Cancel(string orderId)
        {
            var o = Account.Orders.FirstOrDefault(x => x.Id == orderId && x.Status == PaperStatus.Pending);
            if (o != null) { o.Status = PaperStatus.Canceled; Save(); }
        }

        /// <summary>대기 중 지정가 주문을 현재가와 비교해 체결 처리.</summary>
        public bool ProcessLimits(Func<PaperAsset, string, decimal> priceOf)
        {
            bool changed = false;
            foreach (var o in Account.Orders.Where(x => x.Status == PaperStatus.Pending && x.Kind == PaperKind.Limit).ToList())
            {
                var price = priceOf(o.Asset, o.Symbol);
                if (price <= 0) continue;
                bool hit = (o.Side == PaperSide.Buy && price <= o.LimitPrice)
                        || (o.Side == PaperSide.Sell && price >= o.LimitPrice);
                if (hit) { TryFill(o, price); changed = true; }
            }
            if (changed) Save();
            return changed;
        }

        public decimal PositionsValue(Func<PaperAsset, string, decimal> priceOf) =>
            Account.Positions.Sum(p => p.Qty * priceOf(p.Asset, p.Symbol));

        public decimal Equity(Func<PaperAsset, string, decimal> priceOf) => Account.Cash + PositionsValue(priceOf);
    }
}

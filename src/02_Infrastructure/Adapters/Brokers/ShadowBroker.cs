// ============================================================================
// ShadowBroker — S12 #1 섀도우 샌드박스 브로커
//  · IBroker 데코레이터: PaperBroker 위에 결정론적 지연·슬리피지 레이어 삽입
//  · Task.Delay 금지 — 시뮬레이션 틱 타임라인 기반 DelayQueue<ShadowOrder>
//  · RegimeDetector.VolRatio → 지연·슬리피지 계수 증폭 (결정론적 재현성 보장)
//  · 환각 방화벽 엄수: 핵심 체결 수치는 keyword 아닌 수치 입력만 허용
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    // ── 설정 계약 ─────────────────────────────────────────────────────────────

    public sealed record ShadowNoiseConfig
    {
        public bool   Enabled               { get; init; } = true;
        public int    BaseDelayMs           { get; init; } = 50;
        public int    MaxDelayMs            { get; init; } = 500;
        public double SlippageBasePct       { get; init; } = 0.001;   // 0.1%
        public double SlippageVolMultiplier { get; init; } = 2.0;
        public int    Seed                  { get; init; } = 42;
    }

    // ── 체결 이벤트 (Tick 반환) ───────────────────────────────────────────────

    public sealed record ShadowFillEvent(
        string    ShadowOrderId,
        string    Symbol,
        PaperSide Side,
        decimal   SubmittedPrice,   // Place 시점 현재가(시장가) 또는 지정가
        decimal   ActualFillPrice,  // 슬리피지 적용 후 실체결가
        double    DelayMs,
        double    SlippagePct,
        bool      WasAccepted,
        string    InnerMsg);

    // ── 대기 주문 (내부) ──────────────────────────────────────────────────────

    public sealed class ShadowOrder
    {
        public string     Id               { get; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime   ExecuteAt        { get; init; }
        public PaperAsset Asset            { get; init; }
        public string     Symbol           { get; init; } = "";
        public string     Name             { get; init; } = "";
        public PaperSide  Side             { get; init; }
        public PaperKind  Kind             { get; init; }
        public decimal    Qty              { get; init; }
        public decimal    LimitPrice       { get; init; }
        public decimal    SubmittedPrice   { get; init; }  // 제출 시 현재가
        public decimal    SlippedFillPrice { get; init; }  // 시장가 슬리피지 적용가
        public double     DelayMs          { get; init; }
        public double     SlippagePct      { get; init; }
    }

    // ── 핵심 데코레이터 ───────────────────────────────────────────────────────

    public sealed class ShadowBroker : IBroker
    {
        private readonly IBroker                   _inner;
        private readonly ShadowNoiseConfig         _config;
        private readonly Func<RegimeSignal?>       _regime;
        private readonly Random                    _rng;
        private readonly List<ShadowOrder>         _queue = new();
        private readonly OrderAttestationService?  _gating;
        private DateTime                           _simClock;

        public string Name   => $"Shadow({_inner.Name})";
        public bool   IsLive => false;

        public int                        QueueDepth    => _queue.Count;
        public IReadOnlyList<ShadowOrder> PendingOrders => _queue;

        public ShadowBroker(
            IBroker                    inner,
            ShadowNoiseConfig          config,
            Func<RegimeSignal?>?       regimeProvider = null,
            OrderAttestationService?   gating         = null)
        {
            _inner    = inner  ?? throw new ArgumentNullException(nameof(inner));
            _config   = config ?? throw new ArgumentNullException(nameof(config));
            _regime   = regimeProvider ?? (() => null);
            _rng      = new Random(config.Seed);
            _gating   = gating;
            _simClock = DateTime.UtcNow;   // 페이지 기준 초기값; 테스트는 Tick()으로 재설정
        }

        // ── 주문 접수 ─────────────────────────────────────────────────────────

        public (bool ok, string msg) Place(
            PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty,
            decimal limitPrice, decimal currentPrice)
        {
            // 비상 동결 게이팅 인터셉터 — EmergencyFreeze 시 InvalidOperationException
            _gating?.AssertNormal("ShadowBroker.Place");

            if (!_config.Enabled)
                return _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);

            if (qty <= 0)
                return (false, "수량이 올바르지 않습니다.");

            // 레짐 신호 → 노이즈 계수
            var    sig  = _regime();
            double volR = Math.Clamp(sig?.VolRatio ?? 1.0, 0.5, 10.0);

            // 지연 = BaseDelayMs + rand × (MaxDelayMs - BaseDelayMs) × min(volR, 3)
            double volMult    = Math.Min(volR, 3.0);
            int    delayRange = Math.Max(0, _config.MaxDelayMs - _config.BaseDelayMs);
            int    delayMs    = _config.BaseDelayMs + (int)(_rng.NextDouble() * delayRange * volMult);

            // 슬리피지는 시장가만 적용
            double slippagePct = kind == PaperKind.Market
                ? _config.SlippageBasePct * (1.0 + volR * _config.SlippageVolMultiplier)
                : 0.0;
            int     sideSign     = side == PaperSide.Buy ? 1 : -1;
            decimal refPrice     = kind == PaperKind.Market
                ? (currentPrice > 0 ? currentPrice : limitPrice)
                : limitPrice;
            decimal slippedPrice = refPrice * (decimal)(1.0 + slippagePct * sideSign);

            var order = new ShadowOrder
            {
                ExecuteAt        = _simClock.AddMilliseconds(delayMs),
                Asset            = asset,
                Symbol           = symbol,
                Name             = name,
                Side             = side,
                Kind             = kind,
                Qty              = qty,
                LimitPrice       = limitPrice,
                SubmittedPrice   = currentPrice > 0 ? currentPrice : limitPrice,
                SlippedFillPrice = slippedPrice,
                DelayMs          = delayMs,
                SlippagePct      = slippagePct,
            };

            InsertByExecuteAt(order);
            return (true,
                $"[Shadow] 접수 · 지연 {delayMs}ms · 슬리피지 {slippagePct * 100.0:0.000}%");
        }

        // ── 취소: 섀도 큐 우선 → 내부 브로커 위임 ────────────────────────────

        public void Cancel(string orderId)
        {
            int removed = _queue.RemoveAll(o => o.Id == orderId);
            if (removed == 0) _inner.Cancel(orderId);
        }

        // ── 보유 수량: 내부 브로커 위임 ──────────────────────────────────────

        public decimal HeldQty(PaperAsset asset, string symbol)
            => _inner.HeldQty(asset, symbol);

        // ── 시뮬레이션 틱: 클록 전진 → 만기 주문 체결 ────────────────────────

        public IReadOnlyList<ShadowFillEvent> Tick(
            DateTime simTime, Func<PaperAsset, string, decimal> priceOf)
        {
            _simClock = simTime;
            var events = new List<ShadowFillEvent>();

            var due = _queue.Where(o => o.ExecuteAt <= _simClock).ToList();
            foreach (var o in due)
            {
                _queue.Remove(o);

                bool ok; string msg;
                decimal actualFill;

                if (o.Kind == PaperKind.Market)
                {
                    // 시장가: 슬리피지 적용 가격으로 즉시 체결
                    actualFill = o.SlippedFillPrice;
                    (ok, msg) = _inner.Place(o.Asset, o.Symbol, o.Name,
                        o.Side, PaperKind.Market, o.Qty, actualFill, actualFill);
                }
                else
                {
                    // 지정가: 슬리피지 없이 지정가 그대로 전달 → inner ProcessLimits가 체결
                    actualFill = o.LimitPrice;
                    decimal curP = priceOf(o.Asset, o.Symbol);
                    (ok, msg) = _inner.Place(o.Asset, o.Symbol, o.Name,
                        o.Side, PaperKind.Limit, o.Qty, o.LimitPrice, curP);
                }

                events.Add(new ShadowFillEvent(
                    o.Id, o.Symbol, o.Side,
                    o.SubmittedPrice, actualFill,
                    o.DelayMs, o.SlippagePct, ok, msg));
            }

            // inner PaperBroker의 지정가 처리
            if (_inner is PaperBroker pb) pb.ProcessLimits(priceOf);

            return events;
        }

        // ── 내부 헬퍼 ─────────────────────────────────────────────────────────

        private void InsertByExecuteAt(ShadowOrder order)
        {
            int i = 0;
            while (i < _queue.Count && _queue[i].ExecuteAt <= order.ExecuteAt) i++;
            _queue.Insert(i, order);
        }
    }
}

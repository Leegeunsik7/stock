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
        // [Phase 1 감사] 8자리 hex(32비트)는 생일 충돌(~1만 건에 약 1%) 시 Cancel 의 RemoveAll 이 타 주문까지 제거 → 128비트 전체.
        public string     Id               { get; } = Guid.NewGuid().ToString("N");
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
        // [Phase 1 감사] 섀도 ID → 이관된 내부(PaperBroker) 주문 ID. 이관 후 섀도 ID 로 취소하면 내부 브로커가 모르는
        // ID 라 조용히 무시되어 "취소한 지정가가 나중에 체결"되던 결함 교정용. 내부 주문이 대기를 벗어나면 Tick 에서 정리.
        private readonly Dictionary<string, string> _forwardedLimitIds = new(StringComparer.Ordinal);
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

            // v12.10.0 [Step 4/7] 감사 — 노이즈 활성 경로(기본값)가 _inner.Place(PaperBroker, 104/122행)와 달리
            // 가격 검증을 누락했음(실측 확인): currentPrice·limitPrice 가 둘 다 0/음수여도 그대로 접수되어
            // refPrice=0 체결이 큐에 삽입될 수 있었다. Fail-Closed 초크포인트 — 발주 전 사출 차단.
            if (kind == PaperKind.Market && currentPrice <= 0)
                return (false, "현재가를 확인할 수 없습니다.");
            if (kind == PaperKind.Limit && limitPrice <= 0)
                return (false, "지정가를 입력하세요.");

            // 레짐 신호 → 노이즈 계수. [Phase 1 감사] Math.Clamp(NaN)=NaN 이라 NaN 신호가 (decimal)/(int) 변환에서
            // OverflowException 으로 Place 밖까지 누출됐다 — 비유한 신호는 센서 이상으로 보고 발주 거부(fail-closed).
            var    sig  = _regime();
            double rawVol = sig?.VolRatio ?? 1.0;
            if (!double.IsFinite(rawVol))
                return (false, "레짐 신호가 비정상입니다 — 주문을 접수하지 않습니다.");
            double volR = Math.Clamp(rawVol, 0.5, 10.0);

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
            // [Phase 1 감사] 샌드박스 슬리피지 입력은 상한이 없어 매도 체결가 계수(1 - slip)가 0 이하가 될 수 있었다 —
            // 이전엔 "접수"로 반환한 뒤 Tick 에서 PaperBroker 가 조용히 거절. 접수 시점에 거부해야 사용자 피드백이 정직하다.
            double priceFactor = 1.0 + slippagePct * sideSign;
            if (!double.IsFinite(priceFactor) || priceFactor <= 0.0)
                return (false, "슬리피지 설정이 비정상입니다 — 체결가가 0 이하가 됩니다.");
            decimal slippedPrice;
            try { slippedPrice = refPrice * (decimal)priceFactor; }
            catch (OverflowException) { return (false, "슬리피지 설정이 비정상입니다 — 체결가 계산 범위 초과."); }

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
            if (removed > 0) return;
            if (_forwardedLimitIds.Remove(orderId, out var innerId))
            {
                _inner.Cancel(innerId);   // 이관된 지정가 → 내부 브로커 자신의 ID 로 취소
                return;
            }
            _inner.Cancel(orderId);       // 호출측이 내부 브로커 ID 를 직접 넘기는 기존 계약 보존
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
                    // PaperBroker.Place 는 성공 시 새 주문을 Orders[0] 에 삽입한다(동기·단일 스레드 계약) — 그 ID 를 기억.
                    if (ok && _inner is PaperBroker paper && paper.Account.Orders.Count > 0)
                        _forwardedLimitIds[o.Id] = paper.Account.Orders[0].Id;
                }

                events.Add(new ShadowFillEvent(
                    o.Id, o.Symbol, o.Side,
                    o.SubmittedPrice, actualFill,
                    o.DelayMs, o.SlippagePct, ok, msg));
            }

            // inner PaperBroker의 지정가 처리
            if (_inner is PaperBroker pb)
            {
                pb.ProcessLimits(priceOf);
                PruneSettledForwardedIds(pb);
            }

            return events;
        }

        // ── 내부 헬퍼 ─────────────────────────────────────────────────────────

        /// <summary>체결/취소/거절로 대기를 벗어난 내부 주문의 ID 매핑 제거(장시간 세션 무한 증가 방지 — 콜드 경로).</summary>
        private void PruneSettledForwardedIds(PaperBroker pb)
        {
            if (_forwardedLimitIds.Count == 0) return;
            var pending = new HashSet<string>(
                pb.Account.Orders.Where(x => x.Status == PaperStatus.Pending).Select(x => x.Id), StringComparer.Ordinal);
            foreach (var shadowId in _forwardedLimitIds.Where(kv => !pending.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                _forwardedLimitIds.Remove(shadowId);
        }

        private void InsertByExecuteAt(ShadowOrder order)
        {
            int i = 0;
            while (i < _queue.Count && _queue[i].ExecuteAt <= order.ExecuteAt) i++;
            _queue.Insert(i, order);
        }
    }
}

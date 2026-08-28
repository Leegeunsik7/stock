// ============================================================================
// DualBrokerStatusAdapter — v11.3.0: 키움+토스 이중 실통신 관제 Port 호스트 어댑터 (조회 전용)
//   순수 UI 의 IDualBrokerStatusPort 를 콘크리트 브로커 서비스(KiwoomBridgeClient·TossTokenService·
//   TossLiveApiGovernor·IBrokerAccountPort)에 결속한다. 서비스는 IServiceProvider 로 '있으면 소비, 없으면 N/A'
//   (fail-closed·날조 0). 금액 raw(QFixed 10^8) → 표시 decimal 환산.
//   ★liveArmed:false: 이 어댑터에는 발주/변조 경로가 전혀 없다(IBroker·주문 송출 참조 0) — 순수 상태 판독.
// ============================================================================
using System;
using MS2E.Core;
using MS2E.UI.Portable.Ports.Brokers;

namespace MS2E.Infrastructure
{
    public sealed class DualBrokerStatusAdapter : IDualBrokerStatusPort
    {
        private const long TicksPerMs = TimeSpan.TicksPerMillisecond;   // 100ns ticks → ms

        private readonly KiwoomBridgeClient? _kiwoom;
        private readonly TossTokenService? _tossToken;
        private readonly TossLiveApiGovernor? _tossGov;
        private readonly IBrokerAccountPort? _tossAccount;

        public DualBrokerStatusAdapter(IServiceProvider sp)
        {
            // 등록된 것만 해석 — 미배선 서비스는 null → 해당 지표 fail-closed(N/A). GetService 는 미등록 시 null.
            _kiwoom      = sp.GetService(typeof(KiwoomBridgeClient)) as KiwoomBridgeClient;
            _tossToken   = sp.GetService(typeof(TossTokenService)) as TossTokenService;
            _tossGov     = sp.GetService(typeof(TossLiveApiGovernor)) as TossLiveApiGovernor;
            _tossAccount = sp.GetService(typeof(IBrokerAccountPort)) as IBrokerAccountPort;
        }

        /// <summary>★조회 전용 관제 — 발주 무장은 물리적으로 없다. 항상 false.</summary>
        public bool LiveArmed => false;

        // ── 라인 상태 ─────────────────────────────────────────────────────────
        public int GetLineStatus(Span<BrokerLineStatus> dest)
        {
            int n = 0;
            if (dest.Length > n) dest[n++] = KiwoomStatus();
            if (dest.Length > n) dest[n++] = TossStatus();
            return n;
        }

        private BrokerLineStatus KiwoomStatus()
        {
            if (_kiwoom is null)
                return new BrokerLineStatus(BrokerLine.Kiwoom, LineHealth.Offline, false, -1, -1, -1, -1, "프록시 미배선");

            bool alive = _kiwoom.IsProxyAlive;
            long rttMs = _kiwoom.CurrentIpcRttTicks / TicksPerMs;
            // 프록시 존재하나 생존신호 끊김 → SafeHold(라인 격리). 생존 → Online.
            var health = alive ? LineHealth.Online : LineHealth.SafeHold;
            string detail = alive
                ? $"RTT {rttMs}ms · pong {_kiwoom.TotalPongReceived}/{_kiwoom.TotalPingSent}"
                : "x86 프록시 단선 — 격리(SafeHold)";
            return new BrokerLineStatus(BrokerLine.Kiwoom, health, alive, alive ? rttMs : -1, -1, -1, -1, detail);
        }

        private BrokerLineStatus TossStatus()
        {
            if (_tossToken is null && _tossGov is null && _tossAccount is null)
                return new BrokerLineStatus(BrokerLine.Toss, LineHealth.Offline, false, -1, -1, -1, -1, "REST 어댑터 미배선");

            bool hasTok = _tossToken?.HasValidToken ?? false;
            long expSec = _tossToken?.SecondsUntilExpiry() ?? -1;
            long fails = _tossToken is null ? -1 : _tossToken.ConsecutiveFailures;
            long h429 = _tossGov is null ? -1 : _tossGov.Http429;

            LineHealth health;
            string detail;
            if (_tossToken is null)
            {
                health = LineHealth.Degraded; detail = "토큰 서비스 미배선";
            }
            else if (hasTok)
            {
                // 유효 토큰 보유. 429 누적이 있으면 열화 표기(정상 라이브니스는 유지).
                health = (h429 > 0) ? LineHealth.Degraded : LineHealth.Online;
                detail = (h429 > 0) ? $"429 누적 {h429} · 만료 {expSec}s" : $"토큰 유효 · 만료 {expSec}s";
            }
            else
            {
                // 무효 토큰: 연속 실패 임계(≥5) 도달 시 격리(SafeHold), 아니면 열화.
                health = (fails >= 5) ? LineHealth.SafeHold : LineHealth.Degraded;
                detail = (fails >= 5) ? $"인증 연속실패 {fails} — 격리(SafeHold)" : $"토큰 무효 · 실패 {fails}";
            }
            return new BrokerLineStatus(BrokerLine.Toss, health, hasTok, -1, expSec, h429, fails, detail);
        }

        // ── 계좌 비교 ─────────────────────────────────────────────────────────
        public int GetAccounts(Span<BrokerAccountLine> dest)
        {
            int n = 0;
            // 키움 계좌 포트는 이 스켈레톤에 미배선 → fail-closed(가짜 잔고 미생성).
            if (dest.Length > n) dest[n++] = new BrokerAccountLine(BrokerLine.Kiwoom, false, 0, 0, 0, 0, 0);
            if (dest.Length > n) dest[n++] = TossAccount();
            return n;
        }

        private BrokerAccountLine TossAccount()
        {
            if (_tossAccount is not null && _tossAccount.TryGetAccount(out var s))
                return new BrokerAccountLine(BrokerLine.Toss, true,
                    QMath.ToDecimal(s.TotalEvalRaw), QMath.ToDecimal(s.TotalPnlRaw),
                    QMath.ToDecimal(s.DepositRaw), QMath.ToDecimal(s.BuyingPowerRaw), s.HoldingCount);
            return new BrokerAccountLine(BrokerLine.Toss, false, 0, 0, 0, 0, 0);
        }

        // ── 보유 종목 ─────────────────────────────────────────────────────────
        public int GetHoldings(BrokerLine line, Span<BrokerHoldingLine> dest)
        {
            if (line != BrokerLine.Toss || _tossAccount is null || dest.Length == 0) return 0;

            Span<TossHolding> buf = stackalloc TossHolding[Math.Min(dest.Length, 256)];
            int m = _tossAccount.GetHoldings(buf);
            int n = 0;
            for (int i = 0; i < m && n < dest.Length; i++)
            {
                var h = buf[i];
                dest[n++] = new BrokerHoldingLine(BrokerLine.Toss, h.SymbolHash, h.Quantity,
                    QMath.ToDecimal(h.AvgPriceRaw), QMath.ToDecimal(h.CurPriceRaw), QMath.ToDecimal(h.UnrealizedPnlRaw), h.IsOverseas);
            }
            return n;
        }
    }
}

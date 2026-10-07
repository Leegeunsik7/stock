// ============================================================================
// KisOpenOrderQuery — [KIS 미체결 대사 · 추천 A] TTTC8001R(주식일별주문체결조회 · 당일 미체결) 실배선 → BrokerOpenOrderReconciler KIS venue
//
//  문제(실측 · v12.16.x): 대사기에 KIS 판독 경로(OnKisPage · KisProtocol.ParseOpenOrders)는 있었지만 호스트 어디에도 조회 호출부가 없었다.
//    → KIS 대사 확정 불가 · 세션 재접속 후 SessionRecoveryManager 가 Reconciling 에 영구 정체(격리 유지 — fail-closed 였지만 복구 불가).
//
//  구동(콜드 · 전용 1초 타이머 — HTTP 는 블로킹이라 50ms 평가 타이머에 올리지 않는다):
//   ① 기한 도래(확정 = 30초 · 미확정/유령/불일치 = 3초) 또는 대사 요청 계수 변화(재접속 RequestReconcile) → 즉시 조회.
//   ② TryBeginSnapshot(Kis) — 대사기의 진행 배치는 venue 공용 1개 · 키움 OPT10075 대기 중이면 덮어쓰지 않고 다음 주기로(바쁨 계수).
//   ③ 페이지 루프(상한 MaxPages): fetch(ctx_fk, ctx_nk, 연속 여부, out more) → OnKisPage(원문, last: !more). 다음 페이지 키는 응답 본문
//      ctx_area_fk100/nk100(TryParseContinuation · 고정 버퍼). 실패(HTTP null · 키 이탈 · 페이지 상한 · 중도 배치 폐기) = AbortSnapshot
//      (Incomplete — 확정 없음 · 격리 유지). ★재시도 루프 없음 — 다음 주기가 새 스냅샷으로 다시 조회(상태 구동 · 큐잉 0).
//  게이트(★Zero Divergent Dispatch): KIS 한도 0(한도 피드 대사 격리 마스크) = 미확정 ∨ 유령 ∨ 불일치 ∨ 조회 수단 없음.
//    생성 즉시 격리(최초 확정 전 KIS 자식 0) · RefreshGate 는 δ=0(50ms 평가 타이머에서도 호출).
//  패스 결과 라우팅(OnPass · 대사기 onPass 결선):
//    · KIS Confirmed = SessionRecoveryManager.OnReconciliationCompleted(재접속 후면 락 안 즉시 해제 — 확정 프로브 재확인) + 게이트 갱신.
//    · KIS Divergence/Ghost = 게이트 격리 · OnDiscrepancy(영구 래치 — 이중키 리셋 전 해제 없음) · onDiscrepancy(호스트: 블랙리스트 +
//      자식 매트릭스 IsolateBroker — 비트 23). 비트 22(OpenOrderDivergence)는 대사기가 판정 순간 이미 sticky 로 올린다.
//    · [방향 B] 유령 = 대사기가 패스당 ≤4건 KIS 비상 취소(TTTC0803U · ODNO + ord_gno_brno) — AttachKisCanceller 결선 시.
//  δ=0 범위: 페이지 판독(ParseOpenOrders) · 연속 키 판독 · 대사 평가 · 게이트 · 패스 라우팅. HTTP(HttpClient · 문자열 본문)와 다음 페이지 키의
//    URL 문자열화는 전송 몫(콜드 · 할당) — 정직.
// ============================================================================
using System;
using System.Text;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    /// <summary>TTTC8001R 1페이지 조회(전송 계층 · KisLiveBroker.QueryOpenOrdersJson). null = 실패. more = 다음 페이지 있음(tr_cont F/M).</summary>
    public delegate byte[]? KisOpenOrderPageFetch(string ctxFk, string ctxNk, bool continuation, out bool more);

    public sealed class KisOpenOrderQuery
    {
        public const int DefaultMaxPages = 20;
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30), DefaultFastInterval = TimeSpan.FromSeconds(3);

        private readonly BrokerOpenOrderReconciler _recon;
        private readonly KisOpenOrderPageFetch? _fetch;
        private readonly BrokerLimitFeedProvider? _limits;
        private readonly SessionRecoveryManager? _recovery;
        private readonly Action? _onDiscrepancy;
        private readonly int _broker, _maxPages;
        private readonly long _intervalTicks, _fastTicks;
        private readonly Func<long> _nowTicks;
        private readonly byte[] _fk = new byte[128], _nk = new byte[128];          // ctx_area_fk100/nk100(100자) + 여유
        private long _nextTicks, _seenRequests = -1;
        private int _running, _discrepancy;
        private long _queries, _pages, _fetchFailures, _continuationFaults, _pageCapHits, _busySkips, _confirms, _discrepancies;

        /// <param name="fetch">null = 조회 수단 없음(KIS 미설정) → 게이트 영구 격리.</param>
        /// <param name="onDiscrepancy">KIS 불일치/유령 최초 1회(호스트: 스위치보드 블랙리스트 + 자식 매트릭스 IsolateBroker).</param>
        public KisOpenOrderQuery(BrokerOpenOrderReconciler reconciler, KisOpenOrderPageFetch? fetch, BrokerLimitFeedProvider? limits = null,
                                 SessionRecoveryManager? recovery = null, Action? onDiscrepancy = null, int broker = 1,
                                 int maxPages = DefaultMaxPages, TimeSpan? interval = null, TimeSpan? fastInterval = null, Func<long>? nowTicks = null)
        {
            _recon = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
            if (maxPages < 1) throw new ArgumentOutOfRangeException(nameof(maxPages));
            _fetch = fetch; _limits = limits; _recovery = recovery; _onDiscrepancy = onDiscrepancy; _broker = broker; _maxPages = maxPages;
            _intervalTicks = (interval ?? DefaultInterval).Ticks; _fastTicks = (fastInterval ?? DefaultFastInterval).Ticks;
            if (_intervalTicks <= 0 || _fastTicks <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
            _nowTicks = nowTicks ?? SessionRecoveryManager.MonotonicTicks;
            _limits?.SetReconcileIsolated(_broker, true);                              // ★최초 확정 전 KIS 한도 0(fail-closed 기본)
        }

        public long Queries => Interlocked.Read(ref _queries);
        public long Pages => Interlocked.Read(ref _pages);
        public long FetchFailures => Interlocked.Read(ref _fetchFailures);
        public long ContinuationFaults => Interlocked.Read(ref _continuationFaults);
        public long PageCapHits => Interlocked.Read(ref _pageCapHits);
        public long BusySkips => Interlocked.Read(ref _busySkips);
        public long Confirms => Interlocked.Read(ref _confirms);
        public long Discrepancies => Interlocked.Read(ref _discrepancies);
        public bool DiscrepancyLatched => Volatile.Read(ref _discrepancy) == 1;

        /// <summary>KIS 신규 자식 발주 차단 여부 — 조회 수단 없음 ∨ 미확정 ∨ 유령 ∨ 불일치 ∨ 불일치 래치.</summary>
        public bool Blocked => _fetch is null || !_recon.IsConfirmed(OpenOrderVenueKind.Kis) || _recon.GhostDetected || _recon.DivergenceDetected
                               || Volatile.Read(ref _discrepancy) == 1;

        /// <summary>게이트 갱신(δ=0) — 한도 피드 KIS 대사 격리 비트 = Blocked.</summary>
        public void RefreshGate() => _limits?.SetReconcileIsolated(_broker, Blocked);

        /// <summary>대사기 onPass 결선(δ=0) — KIS 패스만 처리.</summary>
        public void OnPass(OpenOrderPassResult r)
        {
            if (r.Venue != OpenOrderVenueKind.Kis) return;
            if (r.Verdict is OpenOrderVerdict.Divergence or OpenOrderVerdict.Ghost)
            {
                _limits?.SetReconcileIsolated(_broker, true);                          // ① KIS 한도 0 선반영
                bool first = Interlocked.Exchange(ref _discrepancy, 1) == 0;
                Interlocked.Increment(ref _discrepancies);
                _recovery?.OnDiscrepancy();                                            // ② 세션 격리 영구 래치(블랙리스트 재주장 포함)
                if (first) { try { _onDiscrepancy?.Invoke(); } catch (Exception) { } } // ③ 호스트: KIS 슬롯 격리(비트 23)
                return;
            }
            if (r.Verdict == OpenOrderVerdict.Confirmed)
            {
                Interlocked.Increment(ref _confirms);
                RefreshGate();
                _recovery?.OnReconciliationCompleted();                                // 재접속 후 Reconciling 이면 즉시 해제(확정 재확인)
            }
        }

        /// <summary>[방향 B] 불일치 래치 해제 — 물리 이중키(대사기 · 복구 관리자 재무장과 함께 호출). 게이트는 새 확정 패스 후에만 열림.</summary>
        public bool TryReset(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _discrepancy, 0);
            RefreshGate();
            return true;
        }

        /// <summary>주기 구동(전용 타이머 · 단일 진입). 반환 = 이번에 조회 패스를 수행했는가.</summary>
        public bool Pump()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return false;
            try
            {
                RefreshGate();
                if (_fetch is null) return false;
                long now = _nowTicks();
                long requests = _recon.ReconcileRequests;
                if (requests == Volatile.Read(ref _seenRequests) && now < Volatile.Read(ref _nextTicks)) return false;
                if (!_recon.TryBeginSnapshot(OpenOrderVenueKind.Kis))
                {
                    Interlocked.Increment(ref _busySkips);                                // 키움 배치 진행 중 — 덮어쓰지 않음(다음 주기)
                    return false;
                }
                Volatile.Write(ref _seenRequests, requests);
                Interlocked.Increment(ref _queries);
                RunPages();
                bool confirmed = !Blocked;
                Volatile.Write(ref _nextTicks, _nowTicks() + (confirmed ? _intervalTicks : _fastTicks));
                RefreshGate();
                return true;
            }
            finally { Volatile.Write(ref _running, 0); }
        }

        private void RunPages()
        {
            string fk = "", nk = "";
            for (int page = 0; page < _maxPages; page++)
            {
                byte[]? body;
                bool more;
                try { body = _fetch!(fk, nk, page > 0, out more); }
                catch (Exception) { body = null; more = false; }
                if (body is null) { Interlocked.Increment(ref _fetchFailures); _recon.AbortSnapshot(OpenOrderVenueKind.Kis); return; }
                Interlocked.Increment(ref _pages);
                if (more)
                {
                    // 다음 페이지 키를 먼저 판독(형식 이탈 = 배치 폐기) — 판독 실패 페이지를 대사기에 넣지 않는다
                    if (!KisProtocol.TryParseContinuation(body, _fk, out int fl, _nk, out int nl) || (fl == 0 && nl == 0))
                    {
                        Interlocked.Increment(ref _continuationFaults);
                        _recon.AbortSnapshot(OpenOrderVenueKind.Kis);
                        return;
                    }
                    _recon.OnKisPage(body, last: false);
                    if (!_recon.QueryPending) return;                                     // 중도 형식 이탈 = 대사기가 이미 폐기
                    fk = Encoding.ASCII.GetString(_fk, 0, fl);                            // URL 키(콜드 · 전송 몫)
                    nk = Encoding.ASCII.GetString(_nk, 0, nl);
                    continue;
                }
                _recon.OnKisPage(body, last: true);                                       // 평가 → onPass(OnPass 라우팅)
                return;
            }
            Interlocked.Increment(ref _pageCapHits);                                       // 페이지 상한 = 미완(확정 금지)
            _recon.AbortSnapshot(OpenOrderVenueKind.Kis);
        }
    }
}

// ============================================================================
// KiwoomComBridgeCore — [키움 COM · 추천 A] 키움 OpenAPI+ 이벤트 처리 로직의 COM 무의존 코어(호스트·x86 프록시 공용 링크)
//
//  왜(실측): ProductionKiwoomApi 는 `#if KIWOOM_COM`(SDK 게이트)라 키움 OpenAPI+ 미설치 환경(본 CI 포함)에선 컴파일조차 되지 않는다
//   — FID 사상·주문번호 사상·거부 처리 로직이 통째로 미검증이었다. 이 파일은 그 로직 전부를 IKiwoomOcx(최소 COM 표면) 위의
//   순수 코드로 옮겨, 테스트가 가짜 OCX 로 전 경로를 검증하게 한다. #if 블록에는 AxKHOpenAPI → IKiwoomOcx 얇은 위임만 남는다.
//
//  교정/신설:
//   ① 서버 거부(OnReceiveMsg) 파서 — "[코드] 메시지" ReadOnlySpan 무할당 분해 + 보수 분류(성공/거부/중대 거부/정보).
//      ★오분류 비대칭: '성공을 거부로' 오판하면 살아 있는 주문의 추적을 놓친다(위험) → 이미 키움 주문번호가 사상된 주문의 '거부'
//      메시지는 무시(정보 취급). 모호 = 정보(슬롯 유지 → TTL 회수) — 거부 단정은 고신뢰 신호만.
//   ② 주문 TR 응답의 빈 주문번호 = 서버 거부(키움: 거부 시 OnReceiveTrData 주문번호 공란) → 즉시 Rejected 통보.
//   ③ 주문번호 사상 경합: 체결 통보(접수)가 TR 응답(주문번호)보다 먼저 오면 수정 전엔 키움 번호로 송신 → 호스트 '미상 주문'으로 유실.
//      미사상 체결 통보를 보류 버퍼(64)에 두었다가 TR 응답에서 사상 즉시 호스트 ID 로 방출(만료 2초 · 포화 시 구 동작).
//   ④ 주문번호 양방향 표 — 고정 용량 개방 주소(역방향 삭제 · δ=0) · 종결(완전체결·거부·취소확인) 시 제거(영구 성장 0).
//   ⑤ [방향 A] STA 메시지 펌프 동결 감시 — 프록시 Ping/Pong 은 파이프 스레드가 응답하므로 COM STA 펌프가 얼어도 호스트는 몰랐다.
//      STA 타이머가 Beat, 프록시가 Ping 수신 시 IsStalled 판정 → 동결이면 Pong 억제(호스트 하트비트 단절 → 기존 ProxyLost 동결 경로).
//   ⑥ [정정/취소 · 계좌] 수정 전 ProductionKiwoomApi.CancelOrder 는 빈 스텁 — 호스트 취소 요청이 프록시에서 '조용히' 사라졌다.
//      키움 SendOrder 주문유형: 3 매수취소 · 4 매도취소 · 5 매수정정 · 6 매도정정(원주문번호 필수 · 7자리 0 채움).
//      · 취소: 사상 완료 주문만 · 수량 = 최신 미체결(902) · 진행 중 중복 차단 · 사상 전 요청은 보류 후 TR 사상 즉시 집행.
//      · 정정: 사상 완료 · 진행 중 취소/정정 없음 · 1 ≤ 정정수량 ≤ 최신 미체결(초과 정정 100% 차단) · 지정가만.
//      · RQName 태그: RQ 신규 · RC 취소 · RM 정정 — 취소/정정 TR 의 주문번호 공란은 '원주문 거부'가 아니다(원주문 생존 · 플래그만 해제).
//      · 계좌: OPW00018(계좌평가잔고내역 · 다중행 보유) · OPW00001(예수금상세 · 주문가능금액) 조회 → Position/AccountCash 프레임.
//   중대 서버 오류(비밀번호·계좌 정지·증거금 부족) · STA 동결은 ErrorReport(코드 71xx)로 송신 — 호스트 KiwoomFreezeRelay(code≠0 동결) +
//   [방향 B] KiwoomServerErrorInterlock(비트). 일반 주문 거부는 ErrorReport 를 쓰지 않는다(전체 동결 오남용 금지) — FillPayload Rejected.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>키움 OpenAPI+ COM 의 최소 표면(테스트 가짜 OCX 교체 지점). 시그니처는 KHOpenAPI 컨트롤과 동일.</summary>
    public interface IKiwoomOcx
    {
        string GetCommRealData(string realKey, int fid);
        string GetChejanData(int fid);
        string GetCommData(string trCode, string recordName, int index, string itemName);
        int SendOrder(string rqName, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hoga, string orgOrderNo);
        void SetInputValue(string id, string value);
        int CommRqData(string rqName, string trCode, int prevNext, string screenNo);
        int GetRepeatCnt(string trCode, string recordName);
    }

    /// <summary>[정정/취소] 요청 결과. Sent/Deferred 외는 송신 0(fail-closed).</summary>
    public enum KiwoomAmendResult : byte
    {
        Sent = 0, Deferred = 1, NotFound = 2, Duplicate = 3, NothingRemaining = 4, ExceedsRemaining = 5,
        InvalidPrice = 6, InvalidQuantity = 7, NotAccepted = 8, SendFailed = 9, NoAccount = 10,
    }

    public enum KiwoomMsgVerdict : byte { Info = 0, Accepted = 1, Rejected = 2, CriticalRejected = 3 }
    public enum KiwoomCriticalKind : byte { None = 0, Margin = 1, Password = 2, AccountSuspended = 3 }

    /// <summary>호스트로 가는 ErrorReport 코드(71xx = 키움 서버 중대 오류 · 7199 = STA 펌프 동결).</summary>
    public static class KiwoomServerErrorCodes
    {
        public const int Margin = 7101, Password = 7102, AccountSuspended = 7103, StaPumpStall = 7199;
        public static int For(KiwoomCriticalKind k) => k switch
        {
            KiwoomCriticalKind.Margin => Margin,
            KiwoomCriticalKind.Password => Password,
            KiwoomCriticalKind.AccountSuspended => AccountSuspended,
            _ => 0,
        };
        public static bool IsCritical(int code) => code is Margin or Password or AccountSuspended or StaPumpStall;
    }

    public static class KiwoomServerMessage
    {
        /// <summary>"[코드] 메시지" 분해(δ=0). 괄호가 없으면 code 빈 · text = 전체.</summary>
        public static void Split(ReadOnlySpan<char> msg, out ReadOnlySpan<char> code, out ReadOnlySpan<char> text)
        {
            msg = msg.Trim();
            code = default; text = msg;
            if (msg.Length > 1 && msg[0] == '[')
            {
                int close = msg.IndexOf(']');
                if (close > 1) { code = msg[1..close].Trim(); text = msg[(close + 1)..].Trim(); }
            }
        }

        /// <summary>서버 메시지 보수 분류(δ=0). 중대 → 거부 → 성공 → 정보 순(거부 단정은 키워드 고신뢰만).</summary>
        public static KiwoomMsgVerdict Classify(ReadOnlySpan<char> msg, out KiwoomCriticalKind critical)
        {
            critical = KiwoomCriticalKind.None;
            Split(msg, out _, out var t);
            if (t.IsEmpty) return KiwoomMsgVerdict.Info;
            if (Has(t, "비밀번호")) { critical = KiwoomCriticalKind.Password; return KiwoomMsgVerdict.CriticalRejected; }
            if (Has(t, "계좌") && (Has(t, "정지") || Has(t, "사고") || Has(t, "해지"))) { critical = KiwoomCriticalKind.AccountSuspended; return KiwoomMsgVerdict.CriticalRejected; }
            if (Has(t, "증거금") || (Has(t, "주문가능") && Has(t, "부족"))) { critical = KiwoomCriticalKind.Margin; return KiwoomMsgVerdict.CriticalRejected; }
            if (Has(t, "거부") || Has(t, "부족") || Has(t, "초과") || Has(t, "불가") || Has(t, "실패") || Has(t, "오류")
                || Has(t, "잘못") || Has(t, "장종료") || Has(t, "장시작전") || Has(t, "호가범위"))
                return KiwoomMsgVerdict.Rejected;
            if (Has(t, "정상처리") || Has(t, "완료되었")) return KiwoomMsgVerdict.Accepted;
            return KiwoomMsgVerdict.Info;
        }

        private static bool Has(ReadOnlySpan<char> s, string word) => s.IndexOf(word.AsSpan(), StringComparison.Ordinal) >= 0;

        /// <summary>RQName "RQ{호스트ID}"(신규 주문) → ID(δ=0).</summary>
        public static bool TryParseRqName(ReadOnlySpan<char> rq, out long clientId)
            => TryParseTagged(rq, out char tag, out clientId) && tag == 'Q';

        /// <summary>RQName "R{태그}{ID}" — Q 신규 · C 취소 · M 정정(δ=0).</summary>
        public static bool TryParseTagged(ReadOnlySpan<char> rq, out char tag, out long clientId)
        {
            clientId = 0; tag = '\0';
            rq = rq.Trim();
            if (rq.Length <= 2 || rq[0] != 'R' || (rq[1] != 'Q' && rq[1] != 'C' && rq[1] != 'M')) return false;
            tag = rq[1];
            return long.TryParse(rq[2..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out clientId)
                && clientId > 0;
        }
    }

    /// <summary>호스트 주문ID ↔ 키움 주문번호 양방향 고정 표(개방 주소 · 역방향 삭제 · δ=0 · Monitor 직렬화).</summary>
    public sealed class KiwoomOrderNumberMap
    {
        private readonly long[] _ck, _cv, _cm, _cr, _cf, _kk, _kv;   // client→kiwoom(+메타: 종목×2|매수 · 최신 미체결 · 플래그) · kiwoom→client
        public const long FlagCancelInFlight = 1, FlagModifyInFlight = 2, FlagCancelDeferred = 4;
        private readonly int _mask;
        private readonly object _gate = new();
        private int _count;
        private long _full;

        public KiwoomOrderNumberMap(int capacityPow2 = 4096)
        {
            if (capacityPow2 < 4 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("용량은 2의 거듭제곱", nameof(capacityPow2));
            _ck = new long[capacityPow2]; _cv = new long[capacityPow2]; _cm = new long[capacityPow2]; _cr = new long[capacityPow2]; _cf = new long[capacityPow2];
            _kk = new long[capacityPow2]; _kv = new long[capacityPow2];
            _mask = capacityPow2 - 1;
        }

        public int Count { get { lock (_gate) return _count; } }
        public long FullRejects => Interlocked.Read(ref _full);

        /// <summary>송신 직후 등록(키움 번호 미상 = 0 · 종목/방향 메타 — 서버 거부 통보에 실어 호스트 형식 검증 통과). 포화면 false.</summary>
        public bool RegisterPending(long clientId, long symbolCode = 0, int side = 1, long quantity = 0)
        {
            lock (_gate)
            {
                if (Find(_ck, clientId) >= 0) return true;
                if (_count >= _ck.Length - 1) { Interlocked.Increment(ref _full); return false; }
                int i = Insert(_ck, _cv, clientId, 0);
                _cm[i] = symbolCode * 2 + (side > 0 ? 1 : 0);
                _cr[i] = quantity; _cf[i] = 0;
                _count++;
                return true;
            }
        }

        /// <summary>[정정/취소] 주문 상태 판독(δ=0). 미등록 = false.</summary>
        public bool TryGetOrder(long clientId, out long kiwoomNo, out long symbolCode, out int side, out long remaining, out long flags)
        {
            lock (_gate)
            {
                int i = Find(_ck, clientId);
                if (i < 0) { kiwoomNo = 0; symbolCode = 0; side = 0; remaining = 0; flags = 0; return false; }
                kiwoomNo = _cv[i]; symbolCode = _cm[i] / 2; side = (_cm[i] & 1) == 1 ? 1 : -1; remaining = _cr[i]; flags = _cf[i];
                return true;
            }
        }

        public void SetRemaining(long clientId, long remaining)
        {
            lock (_gate) { int i = Find(_ck, clientId); if (i >= 0 && remaining >= 0) _cr[i] = remaining; }
        }

        /// <summary>플래그 원자 설정 — 이미 mustBeClear 중 하나라도 서 있으면 false(중복 정정/취소 차단).</summary>
        public bool TrySetFlag(long clientId, long flag, long mustBeClear)
        {
            lock (_gate)
            {
                int i = Find(_ck, clientId);
                if (i < 0 || (_cf[i] & mustBeClear) != 0) return false;
                _cf[i] |= flag;
                return true;
            }
        }

        public void ClearFlag(long clientId, long flag)
        {
            lock (_gate) { int i = Find(_ck, clientId); if (i >= 0) _cf[i] &= ~flag; }
        }

        public bool TryGetMeta(long clientId, out long symbolCode, out int side)
        {
            lock (_gate)
            {
                int i = Find(_ck, clientId);
                long m = i >= 0 ? _cm[i] : 0;
                symbolCode = m / 2; side = (m & 1) == 1 ? 1 : -1;
                return i >= 0 && symbolCode > 0;
            }
        }

        /// <summary>TR 응답 사상. 미등록 클라이언트도 사상(재기동 직후 등) — 포화면 false.</summary>
        public bool Map(long clientId, long kiwoomNo)
        {
            if (clientId <= 0 || kiwoomNo <= 0) return false;
            lock (_gate)
            {
                int i = Find(_ck, clientId);
                if (i < 0)
                {
                    if (_count >= _ck.Length - 1) { Interlocked.Increment(ref _full); return false; }
                    Insert(_ck, _cv, clientId, kiwoomNo);   // 메타 미상(0)
                    _count++;
                }
                else _cv[i] = kiwoomNo;
                if (Find(_kk, kiwoomNo) < 0) Insert(_kk, _kv, kiwoomNo, clientId);
                return true;
            }
        }

        public bool TryGetClient(long kiwoomNo, out long clientId)
        {
            lock (_gate) { int i = Find(_kk, kiwoomNo); clientId = i >= 0 ? _kv[i] : 0; return i >= 0; }
        }

        /// <summary>등록 상태: 0 = 미등록 · 1 = 송신됨(번호 미상) · 2 = 번호 사상.</summary>
        public int StateOf(long clientId)
        {
            lock (_gate) { int i = Find(_ck, clientId); return i < 0 ? 0 : _cv[i] == 0 ? 1 : 2; }
        }

        /// <summary>종결 제거(양방향).</summary>
        public bool RemoveClient(long clientId)
        {
            lock (_gate)
            {
                int i = Find(_ck, clientId);
                if (i < 0) return false;
                long k = _cv[i];
                DeleteClient(i);
                _count--;
                if (k != 0) { int j = Find(_kk, k); if (j >= 0) DeleteReverse(j); }
                return true;
            }
        }

        private int Find(long[] keys, long key)
        {
            int i = (int)((ulong)key * 0x9E3779B97F4A7C15UL >> 40) & _mask;
            for (int n = 0; n <= _mask; n++, i = (i + 1) & _mask)
            {
                long k = keys[i];
                if (k == key) return i;
                if (k == 0) return -1;
            }
            return -1;
        }

        private int Insert(long[] keys, long[] vals, long key, long val)
        {
            int i = (int)((ulong)key * 0x9E3779B97F4A7C15UL >> 40) & _mask;
            while (keys[i] != 0) i = (i + 1) & _mask;
            keys[i] = key; vals[i] = val;
            return i;
        }

        private void DeleteClient(int i) => Delete(_ck, i, _cv, _cm, _cr, _cf);
        private void DeleteReverse(int i) => Delete(_kk, i, _kv, null, null, null);

        // 역방향 이동 삭제(묘비 없음 — 탐사 사슬 유지) · 동반 배열 함께 이동
        private void Delete(long[] keys, int i, long[] a, long[]? b, long[]? c, long[]? d)
        {
            int j = i;
            while (true)
            {
                keys[i] = 0; a[i] = 0; if (b != null) b[i] = 0; if (c != null) c[i] = 0; if (d != null) d[i] = 0;
                while (true)
                {
                    j = (j + 1) & _mask;
                    if (keys[j] == 0) return;
                    int home = (int)((ulong)keys[j] * 0x9E3779B97F4A7C15UL >> 40) & _mask;
                    bool between = i <= j ? (i < home && home <= j) : (i < home || home <= j);
                    if (!between) break;
                }
                keys[i] = keys[j]; a[i] = a[j];
                if (b != null) b[i] = b[j]; if (c != null) c[i] = c[j]; if (d != null) d[i] = d[j];
                i = j;
            }
        }
    }

    /// <summary>[방향 A] STA 메시지 펌프 동결 감시(δ=0). STA 타이머가 Beat, 다른 스레드가 IsStalled 판정.</summary>
    public sealed class StaPumpWatchdog
    {
        private readonly long _stallTicks;
        private readonly Func<long> _now;
        private long _lastBeat, _stalls, _maxGapTicks;
        private int _stalled;

        public StaPumpWatchdog(long stallMillis = 100, Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            long f = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            _stallTicks = Math.Max(1, stallMillis) * f / 1000;
            _now = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
        }

        public long Stalls => Interlocked.Read(ref _stalls);
        public long MaxGapTicks => Interlocked.Read(ref _maxGapTicks);
        public bool HasBeaten => Interlocked.Read(ref _lastBeat) != 0;

        public void Beat()
        {
            long now = _now();
            long prev = Interlocked.Exchange(ref _lastBeat, now);
            if (prev != 0)
            {
                long gap = now - prev, cur = Interlocked.Read(ref _maxGapTicks);
                while (gap > cur) { long p = Interlocked.CompareExchange(ref _maxGapTicks, gap, cur); if (p == cur) break; cur = p; }
            }
        }

        /// <summary>마지막 Beat 이후 임계 초과 = 동결(최초 Beat 전 = 미확립 → false). 동결 진입 1회만 계수 · sticky(재기동 전 회복 불인정).</summary>
        public bool IsStalled()
        {
            if (Volatile.Read(ref _stalled) == 1) return true;
            long last = Interlocked.Read(ref _lastBeat);
            if (last == 0 || _now() - last <= _stallTicks) return false;
            if (Interlocked.CompareExchange(ref _stalled, 1, 0) == 0) Interlocked.Increment(ref _stalls);
            return true;
        }
    }

    /// <summary>
    /// 키움 OCX 이벤트 → 브리지 와이어 페이로드 사상 코어. 이벤트 순서/경합·거부·사상을 전부 여기서 처리한다.
    /// 출력은 콜백(프록시는 EventArgs 로 변환해 기존 IKiwoomApi 계약 유지). 스레드: COM 이벤트(STA) + SendOrder(파이프) — 내부 직렬화.
    /// </summary>
    public sealed class KiwoomComEventCore
    {
        public const int PendingCapacity = 64;
        private readonly IKiwoomOcx _ocx;
        private readonly Action<TickPayload>? _onTick;
        private readonly Action<FillPayload>? _onFill;
        private readonly Action<OrderBookPayload>? _onBook;
        private readonly Action<ReferencePricePayload>? _onBase;
        private readonly Action<int>? _onServerError;
        private readonly Action<AccountCashPayload>? _onCash;
        private readonly Action<PositionPayload>? _onPosition;
        private readonly Action<OpenOrderPayload>? _onOpenOrder;     // [미체결 대사] OPT10075 행
        private readonly Action<OrderMappedPayload>? _onMapped;      // [미체결 대사] 클라이언트 ID ↔ 키움 번호 통지
        private long _openOrderBatch, _ghostCancels, _adoptedMappings;
        private readonly Func<long> _nowTicks;
        private string _screenNo = "5001", _accountNo = "";
        private long _batchId, _amendSent, _amendBlocked, _amendFailed, _cancelDeferredExecuted;
        private readonly long _pendingTtlTicks;
        private readonly KiwoomOrderNumberMap _map;
        private readonly object _pendingGate = new();
        private readonly FillPayload[] _pending = new FillPayload[PendingCapacity];
        private readonly long[] _pendingAt = new long[PendingCapacity];
        private int _pendingCount;
        private readonly System.Collections.Generic.Dictionary<long, long> _lastBase = new();
        private long _rejectsSend, _rejectsTr, _rejectsMsg, _ignoredMsgRejects, _criticals, _buffered, _flushed, _expiredPending;

        public KiwoomComEventCore(IKiwoomOcx ocx, Action<TickPayload>? onTick, Action<FillPayload>? onFill, Action<OrderBookPayload>? onBook,
                                  Action<ReferencePricePayload>? onBase, Action<int>? onServerError,
                                  Func<long>? nowTicks = null, int mapCapacityPow2 = 4096, long pendingTtlMillis = 2_000,
                                  Action<AccountCashPayload>? onCash = null, Action<PositionPayload>? onPosition = null,
                                  Action<OpenOrderPayload>? onOpenOrder = null, Action<OrderMappedPayload>? onMapped = null)
        {
            _onCash = onCash; _onPosition = onPosition;
            _onOpenOrder = onOpenOrder; _onMapped = onMapped;
            _ocx = ocx ?? throw new ArgumentNullException(nameof(ocx));
            _onTick = onTick; _onFill = onFill; _onBook = onBook; _onBase = onBase; _onServerError = onServerError;
            _nowTicks = nowTicks ?? (static () => DateTime.UtcNow.Ticks);
            _pendingTtlTicks = pendingTtlMillis * TimeSpan.TicksPerMillisecond;
            _map = new KiwoomOrderNumberMap(mapCapacityPow2);
        }

        public KiwoomOrderNumberMap Map => _map;
        public long RejectsAtSend => Interlocked.Read(ref _rejectsSend);
        public long RejectsAtTr => Interlocked.Read(ref _rejectsTr);
        public long RejectsAtMsg => Interlocked.Read(ref _rejectsMsg);
        public long IgnoredMsgRejects => Interlocked.Read(ref _ignoredMsgRejects);
        public long CriticalErrors => Interlocked.Read(ref _criticals);
        public long BufferedChejan => Interlocked.Read(ref _buffered);
        public long FlushedChejan => Interlocked.Read(ref _flushed);
        public long ExpiredPending => Interlocked.Read(ref _expiredPending);
        public int PendingCount { get { lock (_pendingGate) return _pendingCount; } }
        public long AmendSent => Interlocked.Read(ref _amendSent);
        public long AmendBlocked => Interlocked.Read(ref _amendBlocked);
        public long AmendFailed => Interlocked.Read(ref _amendFailed);
        public long CancelDeferredExecuted => Interlocked.Read(ref _cancelDeferredExecuted);
        public long AccountBatches => Interlocked.Read(ref _batchId);

        // ── 주문 송신 ─────────────────────────────────────────────────────────

        /// <summary>신규 주문 송신. rc≠0 = 송신 단계 거부 → 즉시 Rejected 통보(슬롯 0ms 반납). 반환 = rc.</summary>
        public int SendOrder(long clientId, string screenNo, string accountNo, long symbolCode, int side, double price, long qty)
        {
            if (string.IsNullOrWhiteSpace(accountNo)) { EmitReject(clientId, symbolCode, side); Interlocked.Increment(ref _rejectsSend); return -1; }   // fail-closed
            int orderType = side > 0 ? 1 : 2;                                   // 1 신규매수 · 2 신규매도
            string hoga = price <= 0.0 ? "03" : "00";                           // 03 시장가 · 00 지정가
            _screenNo = screenNo; _accountNo = accountNo;   // 정정/취소·조회 재사용
            _map.RegisterPending(clientId, symbolCode, side, qty);
            int rc = _ocx.SendOrder("RQ" + clientId.ToString(System.Globalization.CultureInfo.InvariantCulture), screenNo, accountNo, orderType,
                                    symbolCode.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), (int)qty, (int)price, hoga, "");
            if (rc != 0)
            {
                _map.RemoveClient(clientId);
                EmitReject(clientId, symbolCode, side);
                Interlocked.Increment(ref _rejectsSend);
            }
            return rc;
        }

        // ── TR 응답(주문번호) ─────────────────────────────────────────────────

        public void OnTrData(string? rqName, string trCode)
        {
            if (rqName is null) return;
            if (rqName == RqAccountBalance) { OnAccountBalanceTr(trCode, rqName); return; }
            if (rqName == RqAccountCash) { OnAccountCashTr(trCode, rqName); return; }
            if (rqName == RqOpenOrders) { OnOpenOrdersTr(trCode, rqName); return; }
            if (!KiwoomServerMessage.TryParseTagged(rqName, out char tag, out long clientId)) return;
            if (tag != 'Q') { OnAmendTr(tag, clientId, trCode, rqName); return; }
            string noText = _ocx.GetCommData(trCode, rqName, 0, "주문번호");
            if (!long.TryParse(noText.AsSpan().Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long kiwoomNo) || kiwoomNo <= 0)
            {
                // ② 주문번호 공란 = 서버 거부(키움 규약) — 이미 종결/미등록이면 중복 통보 생략
                _map.TryGetMeta(clientId, out long sym, out int sd);
                if (_map.RemoveClient(clientId)) { EmitReject(clientId, sym, sd); Interlocked.Increment(ref _rejectsTr); }
                return;
            }
            _map.Map(clientId, kiwoomNo);
            EmitMapped(clientId, kiwoomNo);                                     // [미체결 대사] 호스트가 키움 번호를 알아야 OPT10075 와 1:1 대사 가능
            FlushPendingFor(kiwoomNo, clientId);                                // ③ 보류 체결 통보 사상 방출
            // ⑥ 사상 전에 들어온 취소 요청 — 지금 집행(원주문번호 확보)
            if (_map.TryGetOrder(clientId, out _, out _, out _, out _, out long flags) && (flags & KiwoomOrderNumberMap.FlagCancelDeferred) != 0)
            {
                _map.ClearFlag(clientId, KiwoomOrderNumberMap.FlagCancelDeferred);
                Interlocked.Increment(ref _cancelDeferredExecuted);
                CancelOrder(clientId);
            }
        }

        // ⑥ 취소(RC)/정정(RM) TR 응답 — 주문번호 공란 = 정정/취소 실패(원주문 생존 · 플래그만 해제)
        private void OnAmendTr(char tag, long clientId, string trCode, string rqName)
        {
            string noText = _ocx.GetCommData(trCode, rqName, 0, "주문번호");
            bool ok = long.TryParse(noText.AsSpan().Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long newNo) && newNo > 0;
            if (tag == 'C')
            {
                if (!ok) { _map.ClearFlag(clientId, KiwoomOrderNumberMap.FlagCancelInFlight); Interlocked.Increment(ref _amendFailed); return; }
                // [실결함 교정] 취소 주문도 새 주문번호 — 미사상이면 취소확인 통보가 보류→만료(호스트 UnknownOrder)되어 슬롯이 TTL 까지 잔존 → sticky Disarm
                _map.Map(clientId, newNo);
                FlushPendingFor(newNo, clientId);
                return;                                                          // 종결은 체결 통보(취소 확인)가 담당
            }
            if (!ok) { _map.ClearFlag(clientId, KiwoomOrderNumberMap.FlagModifyInFlight); Interlocked.Increment(ref _amendFailed); return; }
            _map.Map(clientId, newNo);                                           // 정정 주문은 새 주문번호 — 이후 체결 통보 사상
            EmitMapped(clientId, newNo);
            _map.ClearFlag(clientId, KiwoomOrderNumberMap.FlagModifyInFlight);
            FlushPendingFor(newNo, clientId);
        }

        // ── ⑥ 취소/정정 송신 ─────────────────────────────────────────────────

        /// <summary>취소(키움 3 매수취소 · 4 매도취소). 수량 = 최신 미체결 · 중복/잔량 0/미등록 = 송신 0.</summary>
        private long _supersededIgnored;
        /// <summary>대체된 원주문번호의 비체결 통보 무시 누계(정정/취소 후 구 번호 확인 통보).</summary>
        public long SupersededIgnored => Interlocked.Read(ref _supersededIgnored);

        public KiwoomAmendResult CancelOrder(long clientId)
        {
            if (string.IsNullOrWhiteSpace(_accountNo)) return Blocked(KiwoomAmendResult.NoAccount);
            if (!_map.TryGetOrder(clientId, out long kiwoomNo, out long symbol, out int side, out long remaining, out _)) return Blocked(KiwoomAmendResult.NotFound);
            if (kiwoomNo == 0)
            {
                // 송신했으나 주문번호 미사상 — 원주문번호 없이 취소 불가 → 보류(TR 사상 즉시 집행)
                return _map.TrySetFlag(clientId, KiwoomOrderNumberMap.FlagCancelDeferred, KiwoomOrderNumberMap.FlagCancelDeferred)
                    ? KiwoomAmendResult.Deferred : Blocked(KiwoomAmendResult.Duplicate);
            }
            if (remaining <= 0) return Blocked(KiwoomAmendResult.NothingRemaining);
            if (!_map.TrySetFlag(clientId, KiwoomOrderNumberMap.FlagCancelInFlight, KiwoomOrderNumberMap.FlagCancelInFlight)) return Blocked(KiwoomAmendResult.Duplicate);
            int rc = _ocx.SendOrder("RC" + clientId.ToString(System.Globalization.CultureInfo.InvariantCulture), _screenNo, _accountNo, side > 0 ? 3 : 4,
                                    symbol.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), (int)remaining, 0, "00",
                                    kiwoomNo.ToString("D7", System.Globalization.CultureInfo.InvariantCulture));
            if (rc != 0) { _map.ClearFlag(clientId, KiwoomOrderNumberMap.FlagCancelInFlight); Interlocked.Increment(ref _amendFailed); return KiwoomAmendResult.SendFailed; }
            Interlocked.Increment(ref _amendSent);
            return KiwoomAmendResult.Sent;
        }

        /// <summary>정정(키움 5 매수정정 · 6 매도정정 · 지정가). 1 ≤ 정정수량 ≤ 최신 미체결 · 진행 중 취소/정정 없음.</summary>
        public KiwoomAmendResult ModifyOrder(long clientId, long newPrice, long newQty)
        {
            if (string.IsNullOrWhiteSpace(_accountNo)) return Blocked(KiwoomAmendResult.NoAccount);
            if (newPrice <= 0) return Blocked(KiwoomAmendResult.InvalidPrice);
            if (newQty <= 0) return Blocked(KiwoomAmendResult.InvalidQuantity);
            if (!_map.TryGetOrder(clientId, out long kiwoomNo, out long symbol, out int side, out long remaining, out _)) return Blocked(KiwoomAmendResult.NotFound);
            if (kiwoomNo == 0) return Blocked(KiwoomAmendResult.NotAccepted);
            if (newQty > remaining) return Blocked(KiwoomAmendResult.ExceedsRemaining);   // ★초과 정정 차단(체결 경합으로 줄어든 잔량 기준)
            if (!_map.TrySetFlag(clientId, KiwoomOrderNumberMap.FlagModifyInFlight,
                                 KiwoomOrderNumberMap.FlagCancelInFlight | KiwoomOrderNumberMap.FlagModifyInFlight)) return Blocked(KiwoomAmendResult.Duplicate);
            int rc = _ocx.SendOrder("RM" + clientId.ToString(System.Globalization.CultureInfo.InvariantCulture), _screenNo, _accountNo, side > 0 ? 5 : 6,
                                    symbol.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), (int)newQty, (int)newPrice, "00",
                                    kiwoomNo.ToString("D7", System.Globalization.CultureInfo.InvariantCulture));
            if (rc != 0) { _map.ClearFlag(clientId, KiwoomOrderNumberMap.FlagModifyInFlight); Interlocked.Increment(ref _amendFailed); return KiwoomAmendResult.SendFailed; }
            Interlocked.Increment(ref _amendSent);
            return KiwoomAmendResult.Sent;
        }

        private KiwoomAmendResult Blocked(KiwoomAmendResult r) { Interlocked.Increment(ref _amendBlocked); return r; }

        // ── ⑥ 계좌 조회(OPW00018 보유 · OPW00001 예수금) ─────────────────────

        public const string RqAccountBalance = "ACCBAL", RqAccountCash = "ACCCASH", RqOpenOrders = "OPENORD";

        public long OpenOrderBatches => Interlocked.Read(ref _openOrderBatch);
        public long GhostCancels => Interlocked.Read(ref _ghostCancels);
        public long AdoptedMappings => Interlocked.Read(ref _adoptedMappings);

        /// <summary>[미체결 대사] OPT10075 미체결 조회(조회 TR 1건 — 레이트: 호출측 주기 관리).</summary>
        public int QueryOpenOrders(string accountNo, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(accountNo)) return -1;
            _accountNo = accountNo; _screenNo = screenNo;
            _ocx.SetInputValue("계좌번호", accountNo);
            _ocx.SetInputValue("전체종목구분", "0");
            _ocx.SetInputValue("매매구분", "0");
            _ocx.SetInputValue("종목코드", "");
            _ocx.SetInputValue("체결구분", "1");                               // 1 = 미체결
            return _ocx.CommRqData(RqOpenOrders, "opt10075", 0, screenNo);
        }

        /// <summary>[미체결 대사] 승계 확정 주문을 이 프록시의 사상표에 재등록 — 이후 체결/취소 통보가 호스트 클라이언트 ID 로 사상된다.</summary>
        public bool AdoptMapping(long clientId, long kiwoomNo, long symbolCode, int side, long remaining)
        {
            if (clientId <= 0 || kiwoomNo <= 0 || remaining <= 0) return false;
            _map.RegisterPending(clientId, symbolCode, side, remaining);
            if (!_map.Map(clientId, kiwoomNo)) return false;
            _map.SetRemaining(clientId, remaining);
            Interlocked.Increment(ref _adoptedMappings);
            FlushPendingFor(kiwoomNo, clientId);
            return true;
        }

        /// <summary>[미체결 대사 · 방향 B] 키움 번호로 직접 전량 취소(호스트 미추적 유령 주문 · 사상표 무관). rqName 'RX' = 응답 무시.</summary>
        public KiwoomAmendResult CancelByVenueNo(long kiwoomNo, long symbolCode, int side, long qty)
        {
            if (string.IsNullOrWhiteSpace(_accountNo)) return Blocked(KiwoomAmendResult.NoAccount);
            if (kiwoomNo <= 0 || qty <= 0 || symbolCode <= 0) return Blocked(KiwoomAmendResult.InvalidQuantity);
            int rc = _ocx.SendOrder("RX" + kiwoomNo.ToString(System.Globalization.CultureInfo.InvariantCulture), _screenNo, _accountNo, side > 0 ? 3 : 4,
                                    symbolCode.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), (int)Math.Min(qty, int.MaxValue), 0, "00",
                                    kiwoomNo.ToString("D7", System.Globalization.CultureInfo.InvariantCulture));
            if (rc != 0) { Interlocked.Increment(ref _amendFailed); return KiwoomAmendResult.SendFailed; }
            Interlocked.Increment(ref _ghostCancels);
            return KiwoomAmendResult.Sent;
        }

        private void OnOpenOrdersTr(string trCode, string rqName)
        {
            long batch = Interlocked.Increment(ref _openOrderBatch);
            int rows = Math.Max(0, _ocx.GetRepeatCnt(trCode, rqName));
            long now = _nowTicks();
            if (rows == 0)
            {
                try { _onOpenOrder?.Invoke(new OpenOrderPayload { BatchId = batch, RowIndex = 0, RowCount = 0, TimestampTicks = now }); } catch { }
                return;
            }
            for (int i = 0; i < rows; i++)
            {
                string kind = _ocx.GetCommData(trCode, rqName, i, "주문구분");
                int side = kind.Contains("매수", StringComparison.Ordinal) ? 1 : kind.Contains("매도", StringComparison.Ordinal) ? -1 : 0;
                var p = new OpenOrderPayload
                {
                    BatchId = batch, RowIndex = i, RowCount = rows,
                    VenueOrderNo = KiwoomRealFids.ParseQty(_ocx.GetCommData(trCode, rqName, i, "주문번호")),
                    OrigVenueOrderNo = KiwoomRealFids.ParseQty(_ocx.GetCommData(trCode, rqName, i, "원주문번호")),
                    SymbolCode = ParseCode(_ocx.GetCommData(trCode, rqName, i, "종목코드")),
                    OrderQty = KiwoomRealFids.ParseQty(_ocx.GetCommData(trCode, rqName, i, "주문수량")),
                    RemainingQty = KiwoomRealFids.ParseQty(_ocx.GetCommData(trCode, rqName, i, "미체결수량")),
                    OrderPriceWon = KiwoomRealFids.ParsePrice(_ocx.GetCommData(trCode, rqName, i, "주문가격")),
                    Side = side, TimestampTicks = now,
                };
                try { _onOpenOrder?.Invoke(p); } catch { }
            }
        }

        private void EmitMapped(long clientId, long kiwoomNo)
        {
            try { _onMapped?.Invoke(new OrderMappedPayload { ClientId = clientId, VenueOrderNo = kiwoomNo, TimestampTicks = _nowTicks() }); } catch { }
        }

        /// <summary>계좌 조회 2 TR 요청(조회 TR 레이트: 호출측이 주기 ≥ 10초 유지 — 초당 5 · 분당 100 한도 내).</summary>
        public int QueryAccount(string accountNo, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(accountNo)) return -1;
            _accountNo = accountNo; _screenNo = screenNo;
            _ocx.SetInputValue("계좌번호", accountNo);
            _ocx.SetInputValue("비밀번호", "");
            _ocx.SetInputValue("비밀번호입력매체구분", "00");
            _ocx.SetInputValue("조회구분", "1");
            int rc1 = _ocx.CommRqData(RqAccountBalance, "opw00018", 0, screenNo);
            _ocx.SetInputValue("계좌번호", accountNo);
            _ocx.SetInputValue("비밀번호", "");
            _ocx.SetInputValue("비밀번호입력매체구분", "00");
            _ocx.SetInputValue("조회구분", "2");
            int rc2 = _ocx.CommRqData(RqAccountCash, "opw00001", 0, screenNo);
            return rc1 != 0 ? rc1 : rc2;
        }

        private void OnAccountBalanceTr(string trCode, string rqName)
        {
            long batch = Interlocked.Increment(ref _batchId);
            int rows = Math.Max(0, _ocx.GetRepeatCnt(trCode, rqName));
            long now = _nowTicks();
            if (rows == 0)
            {
                try { _onPosition?.Invoke(new PositionPayload { BatchId = batch, RowIndex = 0, RowCount = 0, TimestampTicks = now }); } catch { }
                return;
            }
            for (int i = 0; i < rows; i++)
            {
                var p = new PositionPayload
                {
                    BatchId = batch, RowIndex = i, RowCount = rows,
                    SymbolCode = ParseCode(_ocx.GetCommData(trCode, rqName, i, "종목번호")),
                    Quantity = KiwoomRealFids.ParseQty(_ocx.GetCommData(trCode, rqName, i, "보유수량")),
                    AvgPrice = KiwoomRealFids.ParsePrice(_ocx.GetCommData(trCode, rqName, i, "매입가")),
                    TimestampTicks = now,
                };
                try { _onPosition?.Invoke(p); } catch { }
            }
        }

        private void OnAccountCashTr(string trCode, string rqName)
        {
            var c = new AccountCashPayload
            {
                Deposit = ParseSigned(_ocx.GetCommData(trCode, rqName, 0, "예수금")),
                D2Deposit = ParseSigned(_ocx.GetCommData(trCode, rqName, 0, "d+2추정예수금")),
                OrderableCash = ParseSigned(_ocx.GetCommData(trCode, rqName, 0, "주문가능금액")),
                TimestampTicks = _nowTicks(),
            };
            try { _onCash?.Invoke(c); } catch { }
        }

        private static long ParseSigned(string s)
        {
            var t = s.AsSpan().Trim();
            return long.TryParse(t, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0;
        }

        // ── 서버 메시지(OnReceiveMsg) ─────────────────────────────────────────

        public void OnReceiveMsg(string? rqName, string? msg)
        {
            var verdict = KiwoomServerMessage.Classify(msg.AsSpan(), out var critical);
            if (verdict == KiwoomMsgVerdict.CriticalRejected)
            {
                Interlocked.Increment(ref _criticals);
                try { _onServerError?.Invoke(KiwoomServerErrorCodes.For(critical)); } catch { }
            }
            if (verdict is not (KiwoomMsgVerdict.Rejected or KiwoomMsgVerdict.CriticalRejected)) return;
            if (rqName is null || !KiwoomServerMessage.TryParseRqName(rqName, out long clientId)) return;
            // ①★오분류 비대칭 방어: 이미 키움 주문번호가 사상된(서버가 접수한) 주문의 '거부' 메시지는 신뢰하지 않는다.
            if (_map.StateOf(clientId) != 1) { Interlocked.Increment(ref _ignoredMsgRejects); return; }
            _map.TryGetMeta(clientId, out long sym, out int sd);
            if (_map.RemoveClient(clientId)) { EmitReject(clientId, sym, sd); Interlocked.Increment(ref _rejectsMsg); }
        }

        // ── 체결 통보(OnReceiveChejanData gubun "0") ─────────────────────────

        public void OnChejan(string gubun)
        {
            if (gubun != "0") return;
            long kiwoomNo = KiwoomRealFids.ParseQty(_ocx.GetChejanData(9203));
            long code = ParseCode(_ocx.GetChejanData(9001));
            double fillPx = KiwoomRealFids.ParsePrice(_ocx.GetChejanData(910));
            long fillQty = KiwoomRealFids.ParseQty(_ocx.GetChejanData(911));
            int side = _ocx.GetChejanData(907).AsSpan().Trim().SequenceEqual("2") ? 1 : -1;
            var status = KiwoomChejanClassifier.Classify(_ocx.GetChejanData(913), _ocx.GetChejanData(905), fillQty);
            string remainText = _ocx.GetChejanData(902);
            long remaining = remainText.AsSpan().Trim().IsEmpty ? -1 : KiwoomRealFids.ParseQty(remainText);
            var f = new FillPayload
            {
                OrderId = kiwoomNo, SymbolCode = code, FillPrice = fillPx, FillQty = fillQty, Side = side, TimestampTicks = _nowTicks(),
                Status = BridgeFillStatusCodec.Encode(status, remaining >= 0), RemainingQty = remaining >= 0 ? remaining : 0,
            };
            bool known = _map.TryGetClient(kiwoomNo, out long clientId);
            if (!known)
            {
                // ⑥ 정정/취소 주문의 통보가 RM/RC TR 응답보다 먼저 도착 — 원주문번호(FID 904)로 결속(새 번호를 현재 번호로 사상)
                long orig = KiwoomRealFids.ParseQty(_ocx.GetChejanData(904));
                if (orig > 0 && orig != kiwoomNo && _map.TryGetClient(orig, out clientId) && _map.Map(clientId, kiwoomNo)) known = true;
            }
            if (known)
            {
                f.OrderId = clientId;
                // ⑥ 대체된(정정/취소 전) 원주문번호의 비체결 통보(정정확인 잔량 0 등)는 현재 주문 상태가 아님 — 무시(체결은 실 노출이라 항상 방출)
                if (status != BridgeFillStatus.Filled && _map.TryGetOrder(clientId, out long current, out _, out _, out _, out _) && current != kiwoomNo)
                { Interlocked.Increment(ref _supersededIgnored); return; }
                if (remaining >= 0 && (!_map.TryGetOrder(clientId, out long cur2, out _, out _, out _, out _) || cur2 == kiwoomNo))
                    _map.SetRemaining(clientId, remaining);                          // ⑥ 취소/정정 수량 기준(체결 경합 선반영 · 현재 번호만)
                Emit(in f);
                if (IsTerminal(status, remaining)) _map.RemoveClient(clientId);
                return;
            }
            Buffer(in f);                                                        // ③ TR 응답보다 먼저 온 통보 — 보류
        }

        // ── 실시간(주식체결 · 주식호가잔량) ───────────────────────────────────

        public void OnRealData(string realType, string realKey)
        {
            long code = ParseCode(realKey);
            if (realType == "주식호가잔량")
            {
                var b = new OrderBookPayload { SymbolCode = code, TimestampTicks = _nowTicks() };
                for (int i = 0; i < 10; i++)
                {
                    b.AskPrice[i] = KiwoomRealFids.ParsePrice(_ocx.GetCommRealData(realKey, KiwoomRealFids.AskPrice1 + i));
                    b.BidPrice[i] = KiwoomRealFids.ParsePrice(_ocx.GetCommRealData(realKey, KiwoomRealFids.BidPrice1 + i));
                    b.AskQty[i] = KiwoomRealFids.ParseQty(_ocx.GetCommRealData(realKey, KiwoomRealFids.AskQty1 + i));
                    b.BidQty[i] = KiwoomRealFids.ParseQty(_ocx.GetCommRealData(realKey, KiwoomRealFids.BidQty1 + i));
                }
                try { _onBook?.Invoke(b); } catch { }
                return;
            }
            if (realType != "주식체결") return;
            string curText = _ocx.GetCommRealData(realKey, KiwoomRealFids.CurrentPrice);
            long price = KiwoomRealFids.ParsePrice(curText);
            if (price <= 0) return;
            double rate = double.TryParse(_ocx.GetCommRealData(realKey, KiwoomRealFids.ChangeRate).AsSpan().Trim(), System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 0;
            var tick = new TickPayload
            {
                SymbolCode = code, Price = price, Volume = KiwoomRealFids.ParseQty(_ocx.GetCommRealData(realKey, KiwoomRealFids.AccVolume)),
                ChangeRate = rate, TimestampTicks = _nowTicks(),
            };
            try { _onTick?.Invoke(tick); } catch { }
            long basePx = KiwoomRealFids.BasePrice(curText, _ocx.GetCommRealData(realKey, KiwoomRealFids.ChangeFromPrevClose));
            if (basePx > 0)
            {
                bool changed;
                lock (_lastBase) { changed = !_lastBase.TryGetValue(code, out long prev) || prev != basePx; if (changed) _lastBase[code] = basePx; }
                if (changed) { try { _onBase?.Invoke(new ReferencePricePayload { SymbolCode = code, BasePrice = basePx, TimestampTicks = _nowTicks() }); } catch { } }
            }
        }

        /// <summary>보류 체결 통보 만료 방출(주기 호출) — TR 응답이 끝내 오지 않으면 키움 번호 그대로(구 동작 · 호스트 미상 → TTL).</summary>
        public int FlushExpired()
        {
            long now = _nowTicks();
            int n = 0;
            while (true)
            {
                FillPayload f;
                lock (_pendingGate)
                {
                    int i = -1;
                    for (int k = 0; k < _pendingCount; k++) if (now - _pendingAt[k] > _pendingTtlTicks) { i = k; break; }
                    if (i < 0) return n;
                    f = _pending[i];
                    RemovePendingAt(i);
                }
                Interlocked.Increment(ref _expiredPending);
                Emit(in f);
                n++;
            }
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        private static bool IsTerminal(BridgeFillStatus s, long remaining)
            => s is BridgeFillStatus.Rejected or BridgeFillStatus.CancelConfirmed || (s == BridgeFillStatus.Filled && remaining == 0);

        private void Buffer(in FillPayload f)
        {
            FillPayload evicted = default; bool evict = false;
            lock (_pendingGate)
            {
                if (_pendingCount == PendingCapacity) { evicted = _pending[0]; RemovePendingAt(0); evict = true; }
                _pending[_pendingCount] = f; _pendingAt[_pendingCount] = _nowTicks(); _pendingCount++;
            }
            Interlocked.Increment(ref _buffered);
            if (evict) { Interlocked.Increment(ref _expiredPending); Emit(in evicted); }
        }

        private void FlushPendingFor(long kiwoomNo, long clientId)
        {
            while (true)
            {
                FillPayload f;
                lock (_pendingGate)
                {
                    int i = -1;
                    for (int k = 0; k < _pendingCount; k++) if (_pending[k].OrderId == kiwoomNo) { i = k; break; }   // 도착 순서 유지
                    if (i < 0) return;
                    f = _pending[i];
                    RemovePendingAt(i);
                }
                f.OrderId = clientId;
                Interlocked.Increment(ref _flushed);
                Emit(in f);
                var st = BridgeFillStatusCodec.Decode(in f, out long rem);
                if (IsTerminal(st, rem)) _map.RemoveClient(clientId);
            }
        }

        private void RemovePendingAt(int i)
        {
            for (int k = i; k < _pendingCount - 1; k++) { _pending[k] = _pending[k + 1]; _pendingAt[k] = _pendingAt[k + 1]; }
            _pendingCount--;
        }

        private void EmitReject(long clientId, long symbolCode, int side)
        {
            var f = new FillPayload
            {
                OrderId = clientId, SymbolCode = symbolCode, FillPrice = 0, FillQty = 0, Side = side >= 0 ? 1 : -1, TimestampTicks = _nowTicks(),
                Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Rejected, true), RemainingQty = 0,
            };
            Emit(in f);
        }

        private void Emit(in FillPayload f)
        {
            try { _onFill?.Invoke(f); } catch { }
        }

        private static long ParseCode(string s)
        {
            var t = s.AsSpan().Trim();
            if (t.Length > 0 && (t[0] == 'A' || t[0] == 'a')) t = t[1..];
            return long.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) && v > 0 && v <= 999_999 ? v : 0;
        }
    }
}

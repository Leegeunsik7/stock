// ============================================================================
// TossPortfolioSyncAdapter — v10.2.0: 토스증권 REST 포트폴리오 동기화 어댑터 (조회 전용·fail-closed)
//
//   IPortfolioSyncPort + IBrokerAccountPort 구현. 토스 REST API 로 계좌 잔고/보유 주식/평균단가/미실현손익/예수금을
//   주기 조회하고, 응답을 TossJsonParser(δ=0 Utf8JsonReader)로 파싱해 seqlock 보호 스냅샷으로 보관한다. 조회는
//   δ=0(파싱)·읽기는 δ=0(seqlock). HTTP 취득 자체는 콜드(주입 델리게이트).
//
//   ★Read-Only Fail-Closed Safety: (1) API Key 권한이 '조회 전용'으로 선언되지 않으면 SyncNow 즉시 거부(write 가능
//   키를 포트폴리오 조회에 쓰지 않는다). (2) 이 어댑터를 경유한 어떤 발주/변조 시도(RejectOrderAttempt)도 원자적
//   하드 거부(veto)한다 — 구조적으로 발주 메서드가 없고, 라우팅 시도는 fail-closed. ★liveArmed:false: IBroker·소켓
//   송신·onSend·실 발주 유발 0(GET 조회 + 파싱 + 스냅샷 보관뿐).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    public sealed class TossPortfolioSyncAdapter : IPortfolioSyncPort, IBrokerAccountPort
    {
        private const int MaxHoldings = 256;

        private readonly Func<string?> _tokenProvider;        // 현재 access token(null/"" → 미인증). 실 배선: TossTokenService.CurrentAccessToken
        private readonly Func<string, byte[]?> _accountFetch; // token → 계좌 JSON 바이트(콜드·HttpClient GET). 실패 → null
        private readonly Func<long> _nowMicros;
        private readonly bool _readOnlyKey;                   // API Key 가 '조회 전용' 스코프로 선언됐는가

        private readonly TossHolding[] _holdings = new TossHolding[MaxHoldings];
        private TossAccountSnapshot _snapshot;
        private long _seq;   // seqlock(짝=안정, 홀=기입중)
        private long _lastSyncMicros, _connected, _syncCount, _orderVetoes;

        public TossPortfolioSyncAdapter(Func<string?> tokenProvider, Func<string, byte[]?> accountFetch,
                                        bool readOnlyKey, Func<long>? nowMicros = null)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _accountFetch  = accountFetch  ?? throw new ArgumentNullException(nameof(accountFetch));
            _readOnlyKey   = readOnlyKey;
            _nowMicros     = nowMicros ?? (static () => 0L);
        }

        public long LastSyncMicros => Interlocked.Read(ref _lastSyncMicros);
        public bool Connected      => Volatile.Read(ref _connected) == 1L;
        public long SyncCount      => Interlocked.Read(ref _syncCount);
        public long OrderVetoes    => Interlocked.Read(ref _orderVetoes);
        public bool ReadOnlyKey    => _readOnlyKey;

        /// <summary>
        /// ★1회 동기화(콜드). fail-closed: 조회 전용 키 아님/미인증/취득 실패/파싱 실패 → false·미변경. 파싱은 δ=0.
        /// </summary>
        public bool SyncNow()
        {
            if (!_readOnlyKey)   // ★조회 전용 키가 아니면 거부(write 가능 키 오용 차단)
            {
                Interlocked.Increment(ref _orderVetoes);
                return false;
            }
            string? token = _tokenProvider();
            if (string.IsNullOrEmpty(token)) { Volatile.Write(ref _connected, 0L); return false; }   // 미인증

            byte[]? json = _accountFetch(token);
            if (json is null || json.Length == 0) { Volatile.Write(ref _connected, 0L); return false; }   // 취득 실패

            long now = _nowMicros();
            Span<TossHolding> tmp = stackalloc TossHolding[MaxHoldings];
            if (!TossJsonParser.TryParseAccount(json, now, out var snap, tmp, out int count))
            {
                Volatile.Write(ref _connected, 0L);
                return false;   // ★파싱 실패 → fail-closed(스냅샷 미변경)
            }

            // seqlock 기입: 짝→홀→기입→짝.
            Interlocked.Increment(ref _seq);   // → 홀(기입 중)
            tmp[..count].CopyTo(_holdings);
            _snapshot = snap;
            Interlocked.Increment(ref _seq);   // → 짝(안정)

            Interlocked.Exchange(ref _lastSyncMicros, now);
            Volatile.Write(ref _connected, 1L);
            Interlocked.Increment(ref _syncCount);
            return true;
        }

        /// <summary>계좌 스냅샷 조회(δ=0·seqlock). 미동기화 → false(fail-closed).</summary>
        public bool TryGetAccount(out TossAccountSnapshot snapshot)
        {
            if (Interlocked.Read(ref _syncCount) == 0L) { snapshot = default; return false; }
            SpinWait sw = default;
            while (true)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1L) != 0L) { sw.SpinOnce(); continue; }   // 기입 중 → 재시도
                TossAccountSnapshot snap = _snapshot;
                long s2 = Volatile.Read(ref _seq);
                if (s1 == s2) { snapshot = snap; return true; }
                sw.SpinOnce();
            }
        }

        /// <summary>보유 종목 조회(δ=0·seqlock). 반환=기입 개수.</summary>
        public int GetHoldings(Span<TossHolding> dest)
        {
            if (dest.Length == 0 || Interlocked.Read(ref _syncCount) == 0L) return 0;
            SpinWait sw = default;
            while (true)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1L) != 0L) { sw.SpinOnce(); continue; }
                int n = Math.Min(_snapshot.HoldingCount, dest.Length);
                _holdings.AsSpan(0, n).CopyTo(dest);
                long s2 = Volatile.Read(ref _seq);
                if (s1 == s2) return n;
                sw.SpinOnce();
            }
        }

        /// <summary>
        /// ★비인가 발주/변조 시도 하드 거부(원자적 veto). 이 조회 전용 어댑터를 경유해 발주를 라우팅하려는 어떤 시도도
        /// 여기서 fail-closed 로 차단된다(항상 false). liveArmed 무관하게 read-only 경계를 봉인.
        /// </summary>
        public bool RejectOrderAttempt(long reasonCode)
        {
            Interlocked.Increment(ref _orderVetoes);
            return false;   // ★언제나 거부 — 이 어댑터는 조회 전용
        }
    }
}

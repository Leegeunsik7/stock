// ============================================================================
// OpenApiPortfolioAdapter — v10.2.0: 스키마 구동 범용 증권사 포트폴리오 어댑터 (조회 전용)
//
//   BrokerSchema + UniversalPortfolioMapper 로 어떤 증권사(KIS·LS 등)든 단일 코드로 조회 동기화한다(Toss 는 기존
//   TossPortfolioSyncAdapter 재사용 가능). IPortfolioSyncPort 구현. seqlock δ=0 읽기. HTTP 취득은 콜드(주입 델리게이트).
//
//   ★Read-Only Fail-Closed: 조회 전용 키 아님/미인증/취득 실패/파싱 실패 → 거부. RejectOrderAttempt 발주 시도 하드 veto.
//   ★liveArmed:false: IBroker·소켓 송신·onSend·실 발주 유발 0(GET 조회 + 매핑뿐).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class OpenApiPortfolioAdapter : IPortfolioSyncPort
    {
        private const int MaxHoldings = 256;

        private readonly BrokerSchema _schema;
        private readonly Func<string?> _tokenProvider;
        private readonly Func<string, byte[]?> _accountFetch;
        private readonly Func<long> _nowMicros;
        private readonly bool _readOnlyKey;

        private readonly TossHolding[] _holdings = new TossHolding[MaxHoldings];
        private PortfolioSummary _summary;
        private long _seq;   // seqlock
        private long _lastSyncMicros, _connected, _syncCount, _orderVetoes;

        public OpenApiPortfolioAdapter(BrokerSchema schema, Func<string?> tokenProvider, Func<string, byte[]?> accountFetch,
                                       bool readOnlyKey, Func<long>? nowMicros = null)
        {
            _schema        = schema ?? throw new ArgumentNullException(nameof(schema));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _accountFetch  = accountFetch ?? throw new ArgumentNullException(nameof(accountFetch));
            _readOnlyKey   = readOnlyKey;
            _nowMicros     = nowMicros ?? (static () => 0L);
        }

        public BrokerId Broker      => _schema.Broker;
        public long LastSyncMicros  => Interlocked.Read(ref _lastSyncMicros);
        public bool Connected       => Volatile.Read(ref _connected) == 1L;
        public long SyncCount       => Interlocked.Read(ref _syncCount);
        public long OrderVetoes     => Interlocked.Read(ref _orderVetoes);

        public bool SyncNow()
        {
            if (!_readOnlyKey) { Interlocked.Increment(ref _orderVetoes); return false; }   // ★조회 전용 키만
            string? token = _tokenProvider();
            if (string.IsNullOrEmpty(token)) { Volatile.Write(ref _connected, 0L); return false; }

            byte[]? json = _accountFetch(token);
            if (json is null || json.Length == 0) { Volatile.Write(ref _connected, 0L); return false; }

            long now = _nowMicros();
            Span<TossHolding> tmp = stackalloc TossHolding[MaxHoldings];
            if (!UniversalPortfolioMapper.TryMap(json, _schema, now, out var summary, tmp, out int count))
            {
                Volatile.Write(ref _connected, 0L);
                return false;   // ★파싱 실패 → fail-closed
            }

            Interlocked.Increment(ref _seq);   // → 홀
            tmp[..count].CopyTo(_holdings);
            _summary = summary;
            Interlocked.Increment(ref _seq);   // → 짝

            Interlocked.Exchange(ref _lastSyncMicros, now);
            Volatile.Write(ref _connected, 1L);
            Interlocked.Increment(ref _syncCount);
            return true;
        }

        /// <summary>포트폴리오 요약 조회(δ=0·seqlock). 미동기화 → false(fail-closed).</summary>
        public bool TryGetSummary(out PortfolioSummary summary)
        {
            if (Interlocked.Read(ref _syncCount) == 0L) { summary = default; return false; }
            SpinWait sw = default;
            while (true)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1L) != 0L) { sw.SpinOnce(); continue; }
                PortfolioSummary snap = _summary;
                long s2 = Volatile.Read(ref _seq);
                if (s1 == s2) { summary = snap; return true; }
                sw.SpinOnce();
            }
        }

        /// <summary>★비인가 발주/변조 시도 하드 거부(원자적 veto·항상 false). 이 어댑터는 조회 전용.</summary>
        public bool RejectOrderAttempt(long reasonCode)
        {
            Interlocked.Increment(ref _orderVetoes);
            return false;
        }
    }
}

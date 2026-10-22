// ============================================================================
// PlutoNewsFeeder — v12.18.64-DEV: 플루토 브레인 2단계 — 공시/뉴스 수집 피더 계약(IPlutoNewsFeeder) · HTTP 폴링 공통 기반(콜드 · 장애 격리)
//
//   흐름: 백그라운드 폴링 태스크(Start) → HTTP GET(요청별 타임아웃 · 응답 상한 바이트) → 원문 파싱(파생 피더) → 접수번호 중복 제거 →
//     싱크(PlutoCatalystAnalyzer.TryEnqueue · 유계 DropOldest 채널 · 비차단)로 PlutoNewsItem 전달. 분석 · 버퍼 게시는 분석기 워커가 한다.
//   ★Fail-Safe 격리: HTTP 오류 · 타임아웃 · 연결 실패 · 비정상/과대 페이로드 · 파서 예외 = 장애 계수 + 지수 백오프(주기 × 2^n · 상한 MaxBackoff) 후 다음 회차 —
//     예외는 피더 밖으로 나가지 않는다(PollOnceAsync 무예외 · 호출 측 취소만 OperationCanceled 없이 0 반환). 트레이딩 엔진 · 아레나 정산 스레드와 공유 자원 0
//     (싱크 = 비차단 TryEnqueue · 큐 포화 = 분석기가 가장 오래된 항목 폐기 · 재시도 큐잉 0).
//   ★중복 제거: 최근 키 MaxSeenKeys(4096)개 FIFO 집합 — 같은 공시가 다음 회차에 다시 와도 전달 1회.
//   ★liveArmed:false — 피더 출력은 가상 아레나 시그널 입력뿐(주문 · 브로커 경로 0). 자격 증명(DART 인증키)은 요청 URL 에만 실리고 로그 · 장애 기록에 남기지 않는다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Engines.Cognitive.News
{
    /// <summary>피더 장애 분류(마지막 회차).</summary>
    public enum PlutoFeederFault : byte
    {
        None = 0,
        HttpStatus = 1,
        Timeout = 2,
        Network = 3,
        Malformed = 4,
        Oversize = 5,
        ApiError = 6,
        Unexpected = 7,
    }

    /// <summary>피더 관측 지표(판독 시점 사본).</summary>
    public readonly record struct PlutoFeederHealth(
        string Name, PlutoSource Source, bool Running, long Polls, long Received, long Delivered, long Duplicates, long SinkRejected,
        long Failures, long Timeouts, long Malformed, int ConsecutiveFailures, PlutoFeederFault LastFault, long LastSuccessUtcTicks);

    /// <summary>공시/뉴스 수집 피더. PollOnceAsync 는 예외를 던지지 않는다(장애 = 계수 · 반환 0).</summary>
    public interface IPlutoNewsFeeder : IDisposable
    {
        string Name { get; }
        PlutoSource Source { get; }
        PlutoFeederHealth Health { get; }
        /// <summary>1회 수집 · 전달. 반환 = 싱크가 수락한 항목 수.</summary>
        Task<int> PollOnceAsync(CancellationToken cancellationToken);
        /// <summary>백그라운드 폴링 기동(1회 · 재호출 무시).</summary>
        void Start();
        /// <summary>백그라운드 폴링 정지(대기 상한 2초).</summary>
        void Stop();
    }

    /// <summary>HTTP 폴링 피더 공통 기반(요청 타임아웃 · 응답 상한 · 백오프 · 중복 제거 · 장애 격리).</summary>
    public abstract class PollingNewsFeeder : IPlutoNewsFeeder
    {
        public const int MaxSeenKeys = 4096;
        public const int DefaultMaxPayloadBytes = 2 * 1024 * 1024;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

        /// <summary>파싱 결과 1건(Key = 중복 제거 키 · 접수번호/액세션 번호).</summary>
        protected readonly record struct ParsedItem(string Key, PlutoNewsItem Item);

        private readonly Func<PlutoNewsItem, bool> _sink;
        private readonly IVirtualClock _clock;
        private readonly HttpClient _http;
        private readonly TimeSpan _interval, _timeout;
        private readonly int _maxPayloadBytes;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new();
        private readonly SemaphoreSlim _pollGate = new(1, 1);
        private CancellationTokenSource? _loopCts;
        private Task? _loop;
        private int _running, _consecutive, _lastFault;
        private long _polls, _received, _delivered, _duplicates, _sinkRejected, _failures, _timeouts, _malformed, _lastSuccess;

        protected PollingNewsFeeder(string name, PlutoSource source, Func<PlutoNewsItem, bool> sink, IVirtualClock clock,
            HttpMessageHandler? handler, TimeSpan pollInterval, TimeSpan? timeout, int maxPayloadBytes)
        {
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("피더 이름 필요", nameof(name)) : name;
            Source = source;
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (pollInterval < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
            _interval = pollInterval;
            _timeout = timeout ?? DefaultTimeout;
            if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (maxPayloadBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            _maxPayloadBytes = maxPayloadBytes;
            _http = handler is null
                ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate }, disposeHandler: true)
                : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;   // 요청별 CancelAfter 로만 제한(이중 타이머 0)
        }

        public string Name { get; }
        public PlutoSource Source { get; }
        protected IVirtualClock Clock => _clock;

        public PlutoFeederHealth Health => new(Name, Source, Volatile.Read(ref _running) != 0, Interlocked.Read(ref _polls), Interlocked.Read(ref _received),
            Interlocked.Read(ref _delivered), Interlocked.Read(ref _duplicates), Interlocked.Read(ref _sinkRejected), Interlocked.Read(ref _failures),
            Interlocked.Read(ref _timeouts), Interlocked.Read(ref _malformed), Volatile.Read(ref _consecutive), (PlutoFeederFault)Volatile.Read(ref _lastFault),
            Interlocked.Read(ref _lastSuccess));

        /// <summary>요청 생성(nowUtcTicks = 피더 시계). 자격 증명은 여기서만 결합.</summary>
        protected abstract HttpRequestMessage BuildRequest(long nowUtcTicks);

        /// <summary>
        /// 응답 원문 파싱. None = 정상(항목 0건 포함) · Malformed = 구조 위반 · ApiError = 서버 상태 코드 오류.
        /// 파생 구현은 예외를 던져도 된다(기반이 Malformed 로 계수).
        /// </summary>
        protected abstract PlutoFeederFault Parse(ReadOnlySpan<byte> payload, long nowUtcTicks, List<ParsedItem> output);

        /// <summary>전달 직전 필터(기본 = 전부 전달). DART 최초 기준선처럼 '기록만 · 전달 0' 회차를 구현할 때 재정의.</summary>
        protected virtual bool ShouldDeliver(in ParsedItem item, bool firstSuccessfulPoll) => true;

        public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return 0;
            try { await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return 0; }
            try
            {
                Interlocked.Increment(ref _polls);
                long now = _clock.UtcNowTicks;
                byte[]? payload;
                int length;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_timeout);
                    try
                    {
                        using var req = BuildRequest(now);
                        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) return Fail(PlutoFeederFault.HttpStatus);
                        long? declared = resp.Content.Headers.ContentLength;
                        if (declared > _maxPayloadBytes) return Fail(PlutoFeederFault.Oversize);
                        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                        (payload, length) = await ReadCappedAsync(stream, _maxPayloadBytes, cts.Token).ConfigureAwait(false);
                        if (payload is null) return Fail(PlutoFeederFault.Oversize);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
                    catch (OperationCanceledException) { Interlocked.Increment(ref _timeouts); return Fail(PlutoFeederFault.Timeout); }
                    catch (HttpRequestException) { return Fail(PlutoFeederFault.Network); }
                    catch (IOException) { return Fail(PlutoFeederFault.Network); }
                }

                var items = new List<ParsedItem>();
                PlutoFeederFault fault;
                try { fault = Parse(payload.AsSpan(0, length), now, items); }
                catch (Exception) { fault = PlutoFeederFault.Malformed; }
                if (fault != PlutoFeederFault.None)
                {
                    if (fault == PlutoFeederFault.Malformed) Interlocked.Increment(ref _malformed);
                    return Fail(fault);
                }

                bool first = Interlocked.Read(ref _lastSuccess) == 0;
                int accepted = 0;
                foreach (var it in items)
                {
                    Interlocked.Increment(ref _received);
                    if (string.IsNullOrEmpty(it.Key) || !Remember(it.Key)) { Interlocked.Increment(ref _duplicates); continue; }
                    if (!ShouldDeliver(in it, first)) continue;
                    bool ok;
                    try { ok = _sink(it.Item); }
                    catch (Exception) { ok = false; }
                    if (ok) { accepted++; Interlocked.Increment(ref _delivered); }
                    else Interlocked.Increment(ref _sinkRejected);
                }
                Volatile.Write(ref _consecutive, 0);
                Volatile.Write(ref _lastFault, (int)PlutoFeederFault.None);
                Interlocked.Exchange(ref _lastSuccess, Math.Max(1, _clock.UtcNowTicks));
                return accepted;
            }
            catch (Exception)
            {
                return Fail(PlutoFeederFault.Unexpected);
            }
            finally
            {
                _pollGate.Release();
            }
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _loop = Task.Run(() => LoopAsync(cts.Token));
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _running, 0, 1) != 1) return;
            var cts = _loopCts;
            if (cts is null) return;
            cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            cts.Dispose();
            _loopCts = null;
            _loop = null;
        }

        /// <summary>다음 회차 대기 시간(연속 실패 n = 주기 × 2^n · 상한 MaxBackoff).</summary>
        public TimeSpan NextDelay()
        {
            int n = Math.Min(Volatile.Read(ref _consecutive), 16);
            if (n == 0) return _interval;
            double ms = Math.Min(_interval.TotalMilliseconds * Math.Pow(2, n), MaxBackoff.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(ms);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                try { await Task.Delay(NextDelay(), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private int Fail(PlutoFeederFault fault)
        {
            Interlocked.Increment(ref _failures);
            Interlocked.Increment(ref _consecutive);
            Volatile.Write(ref _lastFault, (int)fault);
            return 0;
        }

        private bool Remember(string key)
        {
            if (!_seen.Add(key)) return false;
            _seenOrder.Enqueue(key);
            if (_seenOrder.Count > MaxSeenKeys) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }

        private static async Task<(byte[]? Buffer, int Length)> ReadCappedAsync(Stream stream, int cap, CancellationToken ct)
        {
            var buffer = new byte[Math.Min(cap, 64 * 1024)];
            int length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length >= cap) return (null, 0);
                    Array.Resize(ref buffer, Math.Min(cap, buffer.Length * 2));
                }
                int n = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct).ConfigureAwait(false);
                if (n == 0) return (buffer, length);
                length += n;
            }
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
            _pollGate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

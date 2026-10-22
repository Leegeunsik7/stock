// ============================================================================
// UpbitWebSocketLiveTests — [v12.18.56-DEV] 업비트 공개 WebSocket 실측(옵트인 · 인증 0 · 주문 0)
//
//  실행 조건(옵트인): MS2E_UPBIT_WS=1. 미충족 = Skip(실패 아님). 24/7 시장이라 시각 무관.
//  흐름: UpbitWebSocketFeedAdapter.RunAsync(실 ClientWebSocket · wss://api.upbit.com/websocket/v1 정확 일치) → 구독 1건 →
//   orderbook/trade 10 프레임 수신(관찰 지점 = 사전 할당 배열 기록) → 접속(TCP+TLS+업그레이드) 시간 · 첫 프레임 · 프레임 간격 · 서버 시각 대비 로컬 수신 지연(시계 오차 포함) ·
//   피드가 살아 있는 동안 CryptoPaperExchange 가상 매수 → 매도 1회(메모리 잔고만) → 취소 → 루프 정상 종료.
//  송출 = 구독 요청 1건(+ 60초 PING · 실측 구간 내 0). 프로세스 전체 할당은 참고값(TLS · 런타임 소켓 내부 포함 — δ=0 판정은 오프라인 대역 테스트).
// ============================================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Feeds.Crypto;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    public sealed class UpbitWebSocketFactAttribute : FactAttribute
    {
        public const string OptInVar = "MS2E_UPBIT_WS";
        public static string? SkipReason(Func<string, string?> env) => env(OptInVar) == "1" ? null : OptInVar + " ≠ 1(업비트 WebSocket 실측 옵트인 없음) — 미접속";
        public UpbitWebSocketFactAttribute() => Skip = SkipReason(Environment.GetEnvironmentVariable);
    }

    public sealed class UpbitWebSocketLiveTests
    {
        private const int Frames = 10, Cap = 256;
        private readonly ITestOutputHelper _out;
        public UpbitWebSocketLiveTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void OptIn_SkipReason()
        {
            Assert.NotNull(UpbitWebSocketFactAttribute.SkipReason(_ => null));
            Assert.NotNull(UpbitWebSocketFactAttribute.SkipReason(_ => "true"));
            Assert.Null(UpbitWebSocketFactAttribute.SkipReason(k => k == UpbitWebSocketFactAttribute.OptInVar ? "1" : null));
        }

        /// <summary>접속 시간 계측 래퍼(콜드 · 실 전송 위임).</summary>
        private sealed class TimedTransport : IUpbitWsTransport
        {
            private readonly UpbitClientWebSocketTransport _inner = new();
            public double ConnectMs;
            public async Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                long t0 = Stopwatch.GetTimestamp();
                await _inner.ConnectAsync(uri, ct).ConfigureAwait(false);
                ConnectMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => _inner.SendTextAsync(payload, ct);
            public ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReceiveAsync(buffer, ct);
            public void Dispose() => _inner.Dispose();
        }

        [UpbitWebSocketFact]
        public async Task LiveWire_Upbit_WebSocket_Ticket_Handshake_10_Frames_Latency_And_Paper_Roundtrip()
        {
            var kinds = new UpbitStreamKind[Cap];
            var rxTicks = new long[Cap];
            var serverMs = new long[Cap];
            var localMs = new long[Cap];
            int n = 0;
            void Tap(in UpbitStreamFrame f)
            {
                int i = Interlocked.Increment(ref n) - 1;
                if (i >= Cap) return;
                kinds[i] = f.Kind; rxTicks[i] = Stopwatch.GetTimestamp(); serverMs[i] = f.TimestampMs;
                localMs[i] = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
            }

            var ex = new CryptoPaperExchange();
            TimedTransport? transport = null;
            var feed = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, () => transport = new TimedTransport(), tap: Tap);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            long t0 = Stopwatch.GetTimestamp();
            long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
            var run = feed.RunAsync(cts.Token);

            while (Volatile.Read(ref n) < Frames && !run.IsCompleted && !cts.IsCancellationRequested) await Task.Delay(5);
            int got = Math.Min(Volatile.Read(ref n), Cap);
            long allocFeed = GC.GetTotalAllocatedBytes(precise: true) - alloc0;

            // 피드가 살아 있는 동안 가상 왕복(메모리 잔고만 · 10,000원 상당)
            long nowUs = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
            var snap = ex.Snapshot();
            long qty = snap.AskRaw > 0 ? (long)((Int128)10_000 * UpbitTickAligner.Scale * UpbitTickAligner.Scale / snap.AskRaw) + 1 : 0;
            var buy = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, qty, 0, nowUs);
            var sell = ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, qty, 0, nowUs);

            cts.Cancel();
            await run;                                                    // RunAsync = 예외 미전파 계약

            _out.WriteLine($"접속(TCP+TLS+업그레이드) {transport?.ConnectMs:0.0}ms · 접속 {feed.Connects} · 단절 {feed.Disconnects} · 결함 {feed.Faults} · 중단 {feed.Aborts}");
            _out.WriteLine($"수신 프레임 {feed.FramesIn} · 호가 반영 {feed.QuotesFed}/폐기 {feed.QuotesDropped}/거래소 거부 {feed.QuotesRejected} · 체결 {feed.Trades}/폐기 {feed.TradesDropped} · " +
                           $"status {feed.StatusFrames} · error {feed.ErrorFrames} · 미구독 {feed.Unsupported} · 손상 {feed.Malformed} · 초과 {feed.Oversized}");
            if (got > 0)
            {
                double first = Stopwatch.GetElapsedTime(t0, rxTicks[0]).TotalMilliseconds;
                var gaps = Enumerable.Range(1, Math.Min(got, Frames) - 1).Select(i => Stopwatch.GetElapsedTime(rxTicks[i - 1], rxTicks[i]).TotalMilliseconds).OrderBy(x => x).ToArray();
                var lag = Enumerable.Range(0, Math.Min(got, Frames)).Select(i => (double)(localMs[i] - serverMs[i])).OrderBy(x => x).ToArray();
                _out.WriteLine($"시작 → 첫 프레임 {first:0.0}ms · 프레임 간격 중앙 {Median(gaps):0.0}ms(최소 {gaps.FirstOrDefault():0.0} · 최대 {gaps.LastOrDefault():0.0}) · " +
                               $"서버 시각 → 로컬 수신 중앙 {Median(lag):0}ms(최소 {lag.First():0} · 최대 {lag.Last():0} · 로컬 시계 오차 포함)");
                for (int i = 0; i < Math.Min(got, Frames); i++)
                    _out.WriteLine($"  #{i + 1} {kinds[i]} t={serverMs[i]} +{Stopwatch.GetElapsedTime(t0, rxTicks[i]).TotalMilliseconds:0.0}ms");
            }
            var s = feed.State.Snapshot();
            _out.WriteLine($"L1 매수 {Won(s.BidRaw)} · 매도 {Won(s.AskRaw)} · 마지막 체결 {Won(s.LastTradePriceRaw)} {s.LastTradeSide} · 호가 {s.QuoteCount} · 체결 {s.TradeCount}");
            _out.WriteLine($"가상 매수 {buy.Status} {Won(buy.PriceRaw)} · 가상 매도 {sell.Status} {Won(sell.PriceRaw)} · 왕복 비용 {Won(CryptoPaperExchange.InitialKrwRaw - ex.Snapshot().KrwRaw)}");
            _out.WriteLine($"프로세스 전체 할당(참고 · TLS/소켓/계측 포함) {allocFeed:N0}B");

            Assert.True(got >= Frames, $"프레임 {got}/{Frames}");
            Assert.Equal(1, feed.Connects);
            Assert.Equal(0, feed.Malformed);
            Assert.Equal(0, feed.ErrorFrames);
            Assert.True(feed.QuotesFed > 0, "호가 반영 0");
            Assert.True(buy.Filled, "가상 매수 " + buy.Status);
            Assert.True(sell.Filled, "가상 매도 " + sell.Status);
            Assert.Equal(0, ex.Snapshot().BtcRaw);
            Assert.False(feed.IsConnected);
        }

        private static double Median(double[] sorted) => sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];

        private static string Won(long raw) => ((decimal)raw / UpbitTickAligner.Scale).ToString("#,0.########", CultureInfo.InvariantCulture) + "원";
    }
}

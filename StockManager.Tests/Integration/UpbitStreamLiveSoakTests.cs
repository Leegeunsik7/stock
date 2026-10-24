// ============================================================================
// UpbitStreamLiveSoakTests — [v12.18.74-DEV] 업비트 공개 WebSocket 4종(KRW-BTC · ETH · XRP · SOL) 실 스트림 장시간 소크(옵트인 · 인증 0 · 주문 0)
//
//  실행 조건(옵트인): MS2E_UPBIT_SOAK=1 · 시간 = MS2E_UPBIT_SOAK_MINUTES(기본 5 · 1~1440 = 최대 24시간). 미충족 = Skip(실패 아님).
//  구성 = 호스트 결선과 동일: UpbitWebSocketFeedAdapter(Markets = CryptoArenaUniverse.StreamMarkets · 실 ClientWebSocket · 접속 대상 정확 일치) →
//   marketTap → CryptoArenaQuoteFeeder → UpbitCryptoArenaEngine. 정산 = 전용 스레드 50ms 펌프(대시보드 허브 주기 동일).
//  송출 = 연결당 구독 1건 + 60초 PING 뿐. 관측: 마켓별 수용 호가/체결 · 최대 도착 간격 · 재접속/장애/침묵 단절 · 주입기 폐기 ·
//   ★정산 스레드 할당 = 창(시간/6 · 최대 60초)별 GC.GetAllocatedBytesForCurrentThread — δ=0 판정(PassesRecorded: 잡음 창 ≤ 1).
//   수신 스레드(스레드 풀 · TLS/소켓 런타임 내부 포함)는 프로세스 전체 할당/프레임 = 참고값(δ=0 판정은 오프라인 UpbitStreamSoakTests).
// ============================================================================
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Feeds.Crypto;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    public sealed class UpbitSoakFactAttribute : FactAttribute
    {
        public const string OptInVar = "MS2E_UPBIT_SOAK", MinutesVar = "MS2E_UPBIT_SOAK_MINUTES";
        public static string? SkipReason(Func<string, string?> env) => env(OptInVar) == "1" ? null : OptInVar + " ≠ 1(업비트 실 스트림 소크 옵트인 없음) — 미접속";
        public static int Minutes(Func<string, string?> env)
            => int.TryParse(env(MinutesVar), NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m is >= 1 and <= 1440 ? m : 5;
        public UpbitSoakFactAttribute() => Skip = SkipReason(Environment.GetEnvironmentVariable);
    }

    [Collection("ConcurrencyStress")]
    public sealed class UpbitStreamLiveSoakTests
    {
        private readonly ITestOutputHelper _out;
        public UpbitStreamLiveSoakTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void OptIn_SkipReason_And_Minutes()
        {
            Assert.NotNull(UpbitSoakFactAttribute.SkipReason(_ => null));
            Assert.NotNull(UpbitSoakFactAttribute.SkipReason(_ => "0"));
            Assert.Null(UpbitSoakFactAttribute.SkipReason(_ => "1"));
            Assert.Equal(5, UpbitSoakFactAttribute.Minutes(_ => null));
            Assert.Equal(5, UpbitSoakFactAttribute.Minutes(_ => "0"));
            Assert.Equal(5, UpbitSoakFactAttribute.Minutes(_ => "1441"));
            Assert.Equal(5, UpbitSoakFactAttribute.Minutes(_ => "-3"));
            Assert.Equal(1440, UpbitSoakFactAttribute.Minutes(_ => "1440"));
        }

        /// <summary>수용 프레임 도착 간격 추적(δ=0) 후 주입기 전달.</summary>
        private sealed class GapTap
        {
            private readonly CryptoArenaQuoteFeeder _f;
            public readonly long[] LastQuoteTicks = new long[CryptoArenaUniverse.Count], MaxQuoteGapMs = new long[CryptoArenaUniverse.Count];
            public GapTap(CryptoArenaQuoteFeeder f) => _f = f;
            public void Tap(int m, in UpbitStreamFrame frame)
            {
                if (frame.Kind == UpbitStreamKind.Orderbook)
                {
                    long now = Environment.TickCount64, last = LastQuoteTicks[m];
                    if (last != 0 && now - last > MaxQuoteGapMs[m]) MaxQuoteGapMs[m] = now - last;
                    LastQuoteTicks[m] = now;
                }
                _f.OnUpbitMarketFrame(m, in frame);
            }
        }

        [UpbitSoakFact]
        public async Task Live_Four_Market_Stream_Soak_Arena_Pump_Zero_Alloc()
        {
            int minutes = UpbitSoakFactAttribute.Minutes(Environment.GetEnvironmentVariable);
            long totalMs = minutes * 60_000L, windowMs = Math.Min(60_000L, totalMs / 6);
            int windows = (int)(totalMs / windowMs);
            var e = new UpbitCryptoArenaEngine();
            e.EnableTechnical(true);
            var feeder = new CryptoArenaQuoteFeeder(e);
            var tap = new GapTap(feeder);
            var feed = new UpbitWebSocketFeedAdapter(new UpbitMarketState(),
                options: new UpbitFeedOptions { Markets = CryptoArenaUniverse.StreamMarkets }, marketTap: tap.Tap);
            feeder.AttachStream(feed);

            var winBytes = new long[windows];
            int stop = 0;
            var pump = new Thread(() =>
            {
                for (int w = 0; w < windows && Volatile.Read(ref stop) == 0; w++)
                {
                    long end = Environment.TickCount64 + windowMs;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    while (Environment.TickCount64 < end && Volatile.Read(ref stop) == 0)
                    {
                        feeder.Pump(DateTime.UtcNow.Ticks);
                        Thread.Sleep(50);
                    }
                    winBytes[w] = GC.GetAllocatedBytesForCurrentThread() - before;
                }
            }) { IsBackground = true, Name = "soak-pump" };

            using var cts = new CancellationTokenSource();
            long procBefore = GC.GetTotalAllocatedBytes(precise: true);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            Task run = Task.Run(() => feed.RunAsync(cts.Token));
            pump.Start();
            for (long elapsed = 0; elapsed < totalMs; elapsed += 10_000)
            {
                await Task.Delay(10_000);
                if ((elapsed + 10_000) % 60_000 == 0)
                    _out.WriteLine($"[{(elapsed + 10_000) / 60_000}m] frames={feed.FramesIn:N0} q=({feed.QuotesOf(0)},{feed.QuotesOf(1)},{feed.QuotesOf(2)},{feed.QuotesOf(3)}) " +
                                   $"t=({feed.TradesOf(0)},{feed.TradesOf(1)},{feed.TradesOf(2)},{feed.TradesOf(3)}) conn={feed.IsConnected} connects={feed.Connects} faults={feed.Faults}");
            }
            bool connectedAtEnd = feed.IsConnected;
            Volatile.Write(ref stop, 1);
            pump.Join();
            cts.Cancel();
            await run;
            long procBytes = GC.GetTotalAllocatedBytes(precise: true) - procBefore;

            Assert.True(e.TryReadSnapshot(out var s));
            int nonZero = 0;
            foreach (long b in winBytes) if (b != 0) nonZero++;
            _out.WriteLine($"minutes={minutes} frames={feed.FramesIn:N0} quotesFed={feed.QuotesFed:N0} trades={feed.Trades:N0} quoteDrop={feed.QuotesDropped} tradeDrop={feed.TradesDropped} " +
                           $"malformed={feed.Malformed} unsupported={feed.Unsupported} oversized={feed.Oversized} status={feed.StatusFrames} errors={feed.ErrorFrames} " +
                           $"connects={feed.Connects} disconnects={feed.Disconnects} faults={feed.Faults} aborts={feed.Aborts} silence={feed.SilenceAborts} pings={feed.Pings} backoff={feed.BackoffWaits}");
            _out.WriteLine($"maxQuoteGapMs=({tap.MaxQuoteGapMs[0]},{tap.MaxQuoteGapMs[1]},{tap.MaxQuoteGapMs[2]},{tap.MaxQuoteGapMs[3]}) feederDropped={feeder.DroppedQuotes} " +
                           $"streamUpdates={feeder.StreamUpdates:N0} pumps={feeder.Pumps:N0} engineTicks={s.Ticks:N0} engineTrades={s.Trades:N0} aiFills={s.Ai.Fills} rejects={s.Ai.Rejects}");
            _out.WriteLine($"pumpWindows={windows}×{windowMs / 1000}s bytes=[{string.Join(",", winBytes)}] nonZero={nonZero} " +
                           $"process={procBytes:N0}B ≈ {(feed.FramesIn > 0 ? procBytes / feed.FramesIn : 0)}B/frame(참고 · TLS/소켓 포함) gc=({GC.CollectionCount(0) - g0},{GC.CollectionCount(1) - g1},{GC.CollectionCount(2) - g2})");

            Assert.True(feed.Connects >= 1, "업비트 WebSocket 접속 실패");
            Assert.True(connectedAtEnd, "소크 종료 시점 연결 미복구");
            Assert.Equal(0, feed.Malformed);                                                    // 실 프레임 전량 판독
            Assert.Equal(0, feed.Oversized);
            Assert.Equal(0, feed.ErrorFrames);
            Assert.Equal(0, feeder.DroppedQuotes);                                              // 실 호가 = 업비트 격자 정렬 · 범위 안
            for (int m = 0; m < CryptoArenaUniverse.Count; m++)
            {
                Assert.True(feed.QuotesOf(m) > 0, CryptoArenaUniverse.Market(m) + " 호가 0");
                Assert.True(feed.TradesOf(m) > 0, CryptoArenaUniverse.Market(m) + " 체결 0");
            }
            Assert.Equal(feed.QuotesFed + feed.Trades, feeder.StreamUpdates);
            Assert.True(s.Ticks > 0);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
            Assert.True(ZeroAllocProbe.PassesRecorded(winBytes), "정산 스레드 할당 창: " + string.Join(",", winBytes));
        }
    }
}

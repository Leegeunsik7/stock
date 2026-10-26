// ============================================================================
// UpbitCryptoPaperEngineTests — [v12.18.54-DEV] 24/7 가상자산 모의 체결 파이프라인 검증(세션 · 업비트 KRW 호가 단위 · 인메모리 체결기 · 공개 시세 파서/가드)
//
//   §1 CryptoContinuousSession: 유효 시각 = 언제나 ContinuousTrading(주말 · 자정 · 연말) · 시각 ≤0 = Invalid(fail-closed) · δ=0
//   §2 UpbitTickAligner: 2025-07-31 개정 표 경계 전수 · 개정 전 표 회귀 차단 · 내림/올림/반올림 격자 성질(시드 고정 난수) · 오버플로 = 0 · δ=0
//   §3 CryptoPaperExchange: 초기 1억 · 시장가/지정가 체결 산술(슬리피지 2bp · 수수료 5bp 올림) · fail-closed 거부 전종(잔고 무변경) ·
//      Reset · 동시 라이터 CAS 거부 + 회계 보존 · seqlock 찢김 0 · 가상 체결 10만 회 δ=0
//   §4 UpbitPublicProtocol/Guard(오프라인): 파서 정상/이탈 fail-closed · 파서 δ=0 · 송출 허용 목록(거래 API · POST · 인증 헤더 · http = 거부) · 옵트인 Skip
//   §5 리얼 와이어(옵트인 MS2E_UPBIT_PUBLIC=1): 업비트 공개 시세 → 인메모리 가상 왕복 체결(송출 = 시세 GET 2종뿐)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    [Collection("ConcurrencyStress")]
    public sealed class UpbitCryptoPaperEngineTests
    {
        private readonly ITestOutputHelper _out;
        public UpbitCryptoPaperEngineTests(ITestOutputHelper o) => _out = o;

        private const long W = UpbitTickAligner.Won;
        private const long T0 = 1_792_627_200_000_000L;             // 2026-10-22 00:00:00Z(µs)
        private const long Ask = 150_000_000 * W, Bid = 149_990_000 * W;
        private const long Qty = 100_000;                            // 0.001 BTC

        private static CryptoPaperExchange Quoted(long bid = Bid, long ask = Ask, long at = T0)
        {
            var ex = new CryptoPaperExchange();
            Assert.True(ex.UpdateQuote(bid, ask, at));
            return ex;
        }

        // ── §1 세션 ──

        [Fact]
        public void Session_AnyValidInstant_IsContinuous_NonPositive_IsInvalid()
        {
            long sat = new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;   // 토요일
            long newYear = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;
            foreach (long t in new[] { 1L, T0, sat, newYear, long.MaxValue })
            {
                Assert.Equal(CryptoSessionPhase.ContinuousTrading, CryptoContinuousSession.PhaseAtUtcMicros(t));
                Assert.True(CryptoContinuousSession.IsContinuousTradingUtcMicros(t));
            }
            foreach (long t in new[] { 0L, -1L, long.MinValue })
            {
                Assert.Equal(CryptoSessionPhase.Invalid, CryptoContinuousSession.PhaseAtUtcMicros(t));
                Assert.False(CryptoContinuousSession.IsContinuousTradingUtcMicros(t));
            }
            Assert.Equal(3, (int)CryptoSessionPhase.ContinuousTrading);
            Assert.Equal((int)UsSessionPhase.ContinuousTrading, (int)CryptoSessionPhase.ContinuousTrading);
        }

        [Fact]
        public void Session_Hot_Path_ZeroAlloc()
        {
            int hits = 0;
            CryptoContinuousSession.IsContinuousTradingUtcMicros(T0);
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (long i = 0; i < 100_000; i++) if (CryptoContinuousSession.IsContinuousTradingUtcMicros(T0 + i)) hits++;
            });
            Assert.Equal(0, bytes);
            Assert.True(hits >= 100_000);
        }

        // ── §2 호가 단위 ──

        public static IEnumerable<object[]> TickTable() => new[]
        {
            new object[] { 150_000_000 * W, 1_000 * W },
            new object[] { 2_000_000 * W, 1_000 * W },
            new object[] { 1_000_000 * W, 1_000 * W },
            new object[] { 1_000_000 * W - 1, 500 * W },
            new object[] { 500_000 * W, 500 * W },               // 개정 전 표 = 100
            new object[] { 500_000 * W - 1, 100 * W },
            new object[] { 100_000 * W, 100 * W },               // 개정 전 표 = 50
            new object[] { 100_000 * W - 1, 50 * W },
            new object[] { 50_000 * W, 50 * W },
            new object[] { 50_000 * W - 1, 10 * W },
            new object[] { 10_000 * W, 10 * W },
            new object[] { 10_000 * W - 1, 5 * W },
            new object[] { 5_000 * W, 5 * W },
            new object[] { 5_000 * W - 1, W },
            new object[] { 1_000 * W, W },
            new object[] { 100 * W, W },
            new object[] { 100 * W - 1, W / 10 },
            new object[] { 10 * W, W / 10 },
            new object[] { 10 * W - 1, W / 100 },
            new object[] { W, W / 100 },
            new object[] { W - 1, W / 1_000 },
            new object[] { W / 10, W / 1_000 },
            new object[] { W / 10 - 1, W / 10_000 },
            new object[] { W / 100, W / 10_000 },
            new object[] { W / 100 - 1, W / 100_000 },
            new object[] { W / 1_000, W / 100_000 },
            new object[] { W / 1_000 - 1, W / 1_000_000 },
            new object[] { W / 10_000, W / 1_000_000 },
            new object[] { W / 10_000 - 1, W / 10_000_000 },
            new object[] { W / 100_000, W / 10_000_000 },
            new object[] { W / 100_000 - 1, 1L },
            new object[] { 1L, 1L },
            new object[] { 0L, 0L },
            new object[] { -W, 0L },
        };

        [Theory]
        [MemberData(nameof(TickTable))]
        public void Tick_Table_2025_07_31_Boundaries(long priceRaw, long expectedTick)
            => Assert.Equal(expectedTick, UpbitTickAligner.TickFor(priceRaw));

        [Fact]
        public void Tick_Align_Directions_And_FailClosed()
        {
            long p = 150_000_500 * W + 123;
            Assert.Equal(150_000_000 * W, UpbitTickAligner.AlignDown(p));
            Assert.Equal(150_001_000 * W, UpbitTickAligner.AlignUp(p));
            Assert.Equal(150_001_000 * W, UpbitTickAligner.AlignNearest(p));
            Assert.Equal(150_000_000 * W, UpbitTickAligner.AlignNearest(150_000_499 * W));
            Assert.Equal(150_001_000 * W, UpbitTickAligner.AlignNearest(150_000_500 * W));        // 중간값 = 올림
            Assert.Equal(1_000_000 * W, UpbitTickAligner.AlignUp(999_999 * W));                   // 구간 경계 넘김 = 상위 격자점
            Assert.True(UpbitTickAligner.IsAligned(UpbitTickAligner.AlignUp(999_999 * W)));
            Assert.False(UpbitTickAligner.IsAligned(1_000_500 * W));                               // 개정 전 표(500)로는 정렬 — 현행 거부
            Assert.True(UpbitTickAligner.IsAligned(999_500 * W));
            Assert.False(UpbitTickAligner.IsAligned(499_950 * W));                                 // 개정 전 표(50)로는 정렬 — 현행 거부
            Assert.Equal(0, UpbitTickAligner.AlignUp(long.MaxValue));                              // 오버플로 = 무효
            Assert.True(UpbitTickAligner.AlignDown(long.MaxValue) > 0);
            foreach (long bad in new[] { 0L, -1L, long.MinValue })
            {
                Assert.Equal(0, UpbitTickAligner.AlignDown(bad));
                Assert.Equal(0, UpbitTickAligner.AlignUp(bad));
                Assert.Equal(0, UpbitTickAligner.AlignNearest(bad));
                Assert.False(UpbitTickAligner.IsAligned(bad));
            }
        }

        [Fact]
        public void Tick_Align_Grid_Properties_Seeded()
        {
            var rnd = new Random(20261022);
            for (int i = 0; i < 200_000; i++)
            {
                long p = 1 + (long)(rnd.NextDouble() * rnd.NextDouble() * 10_000_000) * (rnd.Next(2) == 0 ? W : 1) + rnd.Next(0, 1_000);
                long t = UpbitTickAligner.TickFor(p);
                long d = UpbitTickAligner.AlignDown(p), u = UpbitTickAligner.AlignUp(p), n = UpbitTickAligner.AlignNearest(p);
                Assert.True(d > 0 && d <= p && p - d < t, "down " + p);
                Assert.True(u >= p && u - p < t, "up " + p);
                Assert.True(UpbitTickAligner.IsAligned(d) && UpbitTickAligner.IsAligned(u) && UpbitTickAligner.IsAligned(n), "grid " + p);
                Assert.True(n == d || n == u, "nearest " + p);
                Assert.Equal(UpbitTickAligner.IsAligned(p), d == p);
            }
        }

        [Fact]
        public void Tick_Hot_Path_ZeroAlloc()
        {
            long acc = 0;
            UpbitTickAligner.AlignUp(Ask + 1);
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (long i = 1; i <= 100_000; i++)
                    acc += UpbitTickAligner.AlignDown(i * 7_777 * W) + UpbitTickAligner.AlignUp(i * 13) + UpbitTickAligner.AlignNearest(i * W + 5) + (UpbitTickAligner.IsAligned(i) ? 1 : 0);
            });
            Assert.Equal(0, bytes);
            Assert.NotEqual(0, acc);
        }

        // ── §3 가상 체결기 ──

        [Fact]
        public void Paper_Initial_State_100M_Krw_Zero_Btc()
        {
            var ex = new CryptoPaperExchange();
            var s = ex.Snapshot();
            Assert.Equal(100_000_000 * W, s.KrwRaw);
            Assert.Equal(CryptoPaperExchange.InitialKrwRaw, s.KrwRaw);
            Assert.Equal(0, s.BtcRaw);
            Assert.Equal(0, s.FillCount);
            Assert.Equal(5, ex.FeeBp);
            Assert.Equal(2, ex.SlippageBp);
            Assert.Equal(s.KrwRaw, s.EquityAtBidRaw());
            Assert.Throws<ArgumentOutOfRangeException>(() => new CryptoPaperExchange(feeBp: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CryptoPaperExchange(slippageBp: 10_000));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CryptoPaperExchange(maxQuoteAgeMicros: 0));
        }

        [Fact]
        public void Paper_Market_Buy_Then_Sell_Exact_Arithmetic()
        {
            var ex = Quoted();
            var b = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0);
            Assert.True(b.Filled);
            Assert.Equal(150_030_000 * W, b.PriceRaw);                       // 150,000,000 × 1.0002 → 1,000 원 격자 올림
            Assert.Equal(150_030 * W, b.NotionalRaw);
            Assert.Equal(75 * W + W * 15 / 1_000, b.FeeRaw);                  // 75.015 원(0.05%)
            long krwAfterBuy = 100_000_000 * W - 150_030 * W - b.FeeRaw;
            Assert.Equal(krwAfterBuy, b.KrwAfterRaw);
            Assert.Equal(Qty, b.BtcAfterRaw);

            var s = ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, Qty, 0, T0 + 1);
            Assert.True(s.Filled);
            Assert.Equal(149_960_000 * W, s.PriceRaw);                       // 149,990,000 × 0.9998 = 149,960,002 → 내림
            Assert.Equal(149_960 * W, s.NotionalRaw);
            Assert.Equal(74 * W + W * 98 / 100, s.FeeRaw);                    // 74.98 원
            Assert.Equal(krwAfterBuy + 149_960 * W - s.FeeRaw, s.KrwAfterRaw);
            Assert.Equal(0, s.BtcAfterRaw);

            var snap = ex.Snapshot();
            Assert.Equal(2, snap.FillCount);
            Assert.Equal(b.FeeRaw + s.FeeRaw, snap.FeesPaidRaw);
            Assert.Equal(s.KrwAfterRaw, snap.KrwRaw);
            Assert.Equal(149_960_000 * W, snap.LastFillPriceRaw);
            Assert.Equal(0, ex.RejectCount);
        }

        [Fact]
        public void Paper_Limit_Ioc_Price_Improvement_And_NotMarketable()
        {
            var ex = Quoted();
            var nm = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Limit, Qty, Ask - 1_000 * W, T0);
            Assert.Equal(CryptoPaperStatus.NotMarketable, nm.Status);
            var b = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Limit, Qty, Ask, T0);                     // 한도 < 슬립가 → 한도 체결
            Assert.True(b.Filled);
            Assert.Equal(Ask, b.PriceRaw);
            var b2 = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Limit, Qty, 151_000_000 * W, T0);        // 한도 > 슬립가 → 슬립가
            Assert.Equal(150_030_000 * W, b2.PriceRaw);
            Assert.Equal(CryptoPaperStatus.InvalidPrice, ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Limit, Qty, Ask + 500 * W, T0).Status);   // 비정렬
            Assert.Equal(CryptoPaperStatus.InvalidPrice, ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Limit, Qty, 0, T0).Status);
            Assert.Equal(CryptoPaperStatus.NotMarketable, ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Limit, Qty, Bid + 1_000 * W, T0).Status);
            var s = ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Limit, Qty, Bid, T0);                   // 한도 > 슬립가 → 한도 체결
            Assert.True(s.Filled);
            Assert.Equal(Bid, s.PriceRaw);
            Assert.Equal(Qty, ex.Snapshot().BtcRaw);
        }

        [Fact]
        public void Paper_FailClosed_Rejections_Leave_Balance_Untouched()
        {
            var empty = new CryptoPaperExchange();
            Assert.Equal(CryptoPaperStatus.NoQuote, empty.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0).Status);

            var ex = Quoted();
            var before = ex.Snapshot();
            var cases = new (CryptoOrderSide Side, CryptoOrderType Type, long Qty, long Now, CryptoPaperStatus Want)[]
            {
                ((CryptoOrderSide)0, CryptoOrderType.Market, Qty, T0, CryptoPaperStatus.InvalidSide),
                ((CryptoOrderSide)3, CryptoOrderType.Market, Qty, T0, CryptoPaperStatus.InvalidSide),
                (CryptoOrderSide.Buy, (CryptoOrderType)9, Qty, T0, CryptoPaperStatus.InvalidPrice),
                (CryptoOrderSide.Buy, CryptoOrderType.Market, 0, T0, CryptoPaperStatus.InvalidQuantity),
                (CryptoOrderSide.Buy, CryptoOrderType.Market, -Qty, T0, CryptoPaperStatus.InvalidQuantity),
                (CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, CryptoPaperStatus.SessionInvalid),
                (CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, -5, CryptoPaperStatus.SessionInvalid),
                (CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, T0 - 1, CryptoPaperStatus.StaleQuote),                     // 시계 역행
                (CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, T0 + CryptoPaperExchange.DefaultMaxQuoteAgeMicros + 1, CryptoPaperStatus.StaleQuote),
                (CryptoOrderSide.Buy, CryptoOrderType.Market, 3_000, T0, CryptoPaperStatus.BelowMinimumOrder),                // 약 4,500원 < 5,000원
                (CryptoOrderSide.Buy, CryptoOrderType.Market, 100_000_000, T0, CryptoPaperStatus.InsufficientKrw),           // 1 BTC ≈ 1.5억 > 1억
                (CryptoOrderSide.Sell, CryptoOrderType.Market, Qty, T0, CryptoPaperStatus.InsufficientBtc),                  // 미보유 매도
            };
            foreach (var c in cases)
            {
                var f = ex.Submit(c.Side, c.Type, c.Qty, 0, c.Now);
                Assert.Equal(c.Want, f.Status);
                Assert.False(f.Filled);
                Assert.Equal(0, f.PriceRaw);
                Assert.Equal(0, f.NotionalRaw);
                Assert.Equal(before.KrwRaw, f.KrwAfterRaw);
                Assert.Equal(before.BtcRaw, f.BtcAfterRaw);
            }
            Assert.Equal(before, ex.Snapshot());
            Assert.Equal(cases.Length, ex.RejectCount);
            Assert.True(ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0 + CryptoPaperExchange.DefaultMaxQuoteAgeMicros).Filled);   // 지연 한계 = 허용
            Assert.Equal(CryptoPaperStatus.InsufficientBtc, ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, Qty + 1, 0, T0).Status);      // 보유 초과
            Assert.Equal(CryptoPaperStatus.None, default(CryptoPaperFill).Status);
        }

        [Fact]
        public void Paper_Insufficient_Krw_Boundary_Is_Exact()
        {
            // 1억 원 전액에 수수료까지 맞는 최대 수량 = 체결, 1 satoshi 더 = 예수금 부족(잔고 무변경)
            var probe = Quoted();
            long px = 150_030_000 * W;
            long lo = 1, hi = 100_000_000;
            while (lo < hi)
            {
                long mid = lo + (hi - lo + 1) / 2;
                Int128 n = ((Int128)px * mid + (W - 1)) / W;
                Int128 f = (n * 5 + 9_999) / 10_000;
                if (n + f <= CryptoPaperExchange.InitialKrwRaw) lo = mid; else hi = mid - 1;
            }
            var over = Quoted().Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, lo + 1, 0, T0);
            Assert.Equal(CryptoPaperStatus.InsufficientKrw, over.Status);
            Assert.Equal(CryptoPaperExchange.InitialKrwRaw, over.KrwAfterRaw);
            var max = probe.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, lo, 0, T0);
            Assert.True(max.Filled);
            Assert.True(max.KrwAfterRaw >= 0);
            Assert.Equal(CryptoPaperStatus.InsufficientKrw, probe.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, 10_000, 0, T0).Status);
            Assert.Equal(lo, probe.Snapshot().BtcRaw);
        }

        [Fact]
        public void Paper_UpdateQuote_Rejects_Invalid_And_Keeps_Previous()
        {
            var ex = Quoted();
            Assert.False(ex.UpdateQuote(Ask, Bid, T0 + 1));                    // 교차
            Assert.False(ex.UpdateQuote(Bid, Bid, T0 + 1));                    // 동일
            Assert.False(ex.UpdateQuote(Bid + 1, Ask, T0 + 1));                // 비정렬
            Assert.False(ex.UpdateQuote(0, Ask, T0 + 1));
            Assert.False(ex.UpdateQuote(Bid, Ask, 0));                         // 세션 무효
            var s = ex.Snapshot();
            Assert.Equal(Bid, s.BidRaw);
            Assert.Equal(Ask, s.AskRaw);
            Assert.Equal(T0, s.QuoteUtcMicros);
        }

        [Fact]
        public void Paper_Reset_Restores_Initial_State()
        {
            var ex = Quoted();
            Assert.True(ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0).Filled);
            ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, Qty * 9, 0, T0);
            Assert.True(ex.Reset());
            Assert.Equal(new CryptoPaperBalance(CryptoPaperExchange.InitialKrwRaw, 0, 0, 0, 0, 0, 0, 0), ex.Snapshot());
            Assert.Equal(0, ex.RejectCount);
            Assert.Equal(CryptoPaperStatus.NoQuote, ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0).Status);
        }

        [Fact]
        public void Paper_Equity_At_Bid()
        {
            var ex = Quoted();
            var b = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0);
            Assert.Equal(b.KrwAfterRaw + 149_990 * W, ex.Snapshot().EquityAtBidRaw());
        }

        [Fact]
        public void Paper_Concurrent_Writers_CasReject_And_Accounting_Conserved_No_Torn_Reads()
        {
            const int writers = 4, perWriter = 1_000;
            var ex = Quoted();
            var oneBuy = Quoted().Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0);
            long unit = oneBuy.NotionalRaw + oneBuy.FeeRaw;
            long filled = 0, casRejects = 0, krwRejects = 0, otherRejects = 0, torn = 0, reads = 0;    // 4,000 매수 × 약 15만원 > 1억 → 예수금 고갈 후 InsufficientKrw(fail-closed) 도달
            int stop = 0;
            using var start = new ManualResetEventSlim(false);
            var threads = new List<Thread>();
            for (int w = 0; w < writers; w++)
            {
                threads.Add(new Thread(() =>
                {
                    start.Wait();
                    for (int i = 0; i < perWriter; i++)
                    {
                        var f = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, Qty, 0, T0);
                        if (f.Filled) Interlocked.Increment(ref filled);
                        else if (f.Status == CryptoPaperStatus.ConcurrentWriter) Interlocked.Increment(ref casRejects);
                        else if (f.Status == CryptoPaperStatus.InsufficientKrw) Interlocked.Increment(ref krwRejects);
                        else Interlocked.Increment(ref otherRejects);
                        if ((i & 15) == 0) Thread.Yield();                                         // 2-vCPU CI 러너 기아 방어 — 리더에 타임슬라이스 양보
                    }
                }));
            }
            var reader = new Thread(() =>
            {
                start.Wait();
                do                                                                                  // do-while: 스케줄 지연돼도 최소 1회 스냅샷 검증
                {
                    var s = ex.Snapshot();
                    reads++;
                    // 동일 매수만 → 모든 필드가 같은 세대면 KRW · BTC · 수수료가 FillCount 의 선형 함수
                    if (s.KrwRaw != CryptoPaperExchange.InitialKrwRaw - s.FillCount * unit || s.BtcRaw != s.FillCount * Qty
                        || s.FeesPaidRaw != s.FillCount * oneBuy.FeeRaw) torn++;
                } while (Volatile.Read(ref stop) == 0);
            });
            foreach (var t in threads) t.Start();
            reader.Start();
            start.Set();
            foreach (var t in threads) t.Join();
            Volatile.Write(ref stop, 1);
            reader.Join();

            var end = ex.Snapshot();
            _out.WriteLine($"filled={filled} cas={casRejects} krw={krwRejects} other={otherRejects} reads={reads} torn={torn}");
            Assert.Equal(0, otherRejects);
            Assert.Equal(writers * perWriter, filled + casRejects + krwRejects);
            Assert.True(end.KrwRaw >= 0);
            if (krwRejects > 0) Assert.True(end.KrwRaw < unit, "예수금 남았는데 부족 거부");      // 고갈 거부는 진짜 고갈일 때만
            Assert.Equal(filled, end.FillCount);
            Assert.Equal(CryptoPaperExchange.InitialKrwRaw - filled * unit, end.KrwRaw);
            Assert.Equal(filled * Qty, end.BtcRaw);
            Assert.Equal(casRejects + krwRejects, ex.RejectCount);
            Assert.Equal(0, torn);
            Assert.True(reads > 0);
        }

        [Fact]
        public void Paper_100k_Virtual_Fills_ZeroAlloc()
        {
            const long q = 10_000;                                              // 0.0001 BTC ≈ 15,000원(최소 주문 위)
            var ex = new CryptoPaperExchange();
            long rejected = 0;
            Action body = () =>
            {
                for (int i = 0; i < 50_000; i++)
                {
                    long now = T0 + i;
                    long bump = (i & 7) * 1_000 * W;
                    ex.UpdateQuote(Bid + bump, Ask + bump, now);
                    if (!ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, q, 0, now).Filled) rejected++;
                    if (!ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, q, 0, now).Filled) rejected++;
                    ex.Snapshot();
                }
            };
            body();                                                              // JIT 예열
            long bytes = ZeroAllocProbe.MinAllocated(body, reset: () => { ex.Reset(); rejected = 0; });
            var s = ex.Snapshot();
            _out.WriteLine($"alloc={bytes}B fills={s.FillCount} krw={UpbitPublicFeedProbe.Won(s.KrwRaw)} fees={UpbitPublicFeedProbe.Won(s.FeesPaidRaw)}");
            Assert.Equal(0, bytes);
            Assert.Equal(0, rejected);
            Assert.Equal(100_000, s.FillCount);
            Assert.Equal(0, s.BtcRaw);
            Assert.True(s.KrwRaw < CryptoPaperExchange.InitialKrwRaw);         // 왕복 = 수수료 + 슬리피지 손실(공짜 차익 0)
        }

        // ── §4 공개 시세 파서 · 가드(오프라인) ──

        private const string TickerJson = "[{\"market\":\"KRW-BTC\",\"trade_date\":\"20261022\",\"trade_price\":150000000.0,\"trade_volume\":0.00123456,"
                                        + "\"trade_timestamp\":1792627200123,\"timestamp\":1792627200456,\"change\":\"RISE\"}]";
        private const string BookJson = "[{\"market\":\"KRW-BTC\",\"timestamp\":1792627200789,\"total_ask_size\":3.5,\"total_bid_size\":4.1,"
                                      + "\"orderbook_units\":[{\"ask_price\":150000000,\"bid_price\":149990000,\"ask_size\":0.12345678,\"bid_size\":0.5},"
                                      + "{\"ask_price\":150001000,\"bid_price\":149989000,\"ask_size\":1,\"bid_size\":2}],\"level\":0}]";

        private static byte[] U(string s) => System.Text.Encoding.UTF8.GetBytes(s);

        [Fact]
        public void Parser_Ticker_And_Orderbook_Valid()
        {
            Assert.True(UpbitPublicProtocol.TryParseTicker(U(TickerJson), UpbitPublicProtocol.MarketKrwBtc, out var t));
            Assert.Equal(new UpbitTicker(Ask, 1792627200123, 1792627200456), t);
            Assert.True(UpbitPublicProtocol.TryParseOrderbookL1(U(BookJson), UpbitPublicProtocol.MarketKrwBtc, out var q));
            Assert.Equal(new UpbitL1Quote(Bid, Ask, 50_000_000, 12_345_678, 1792627200789), q);
            Assert.True(new CryptoPaperExchange().UpdateQuote(q.BidRaw, q.AskRaw, T0));
        }

        [Theory]
        [InlineData("{\"error\":{\"name\":\"404\",\"message\":\"Code not found\"}}")]
        [InlineData("[]")]
        [InlineData("[{\"market\":\"KRW-ETH\",\"trade_price\":1,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":1,\"timestamp\":1},{\"market\":\"KRW-BTC\",\"trade_price\":1,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":1,\"trade_price\":2,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"market\":\"KRW-BTC\",\"trade_price\":1,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":-1,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":0,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":1e30,\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":\"1\",\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":1,\"timestamp\":1.5}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"trade_price\":1,\"timestamp\":1}")]
        [InlineData("not json")]
        [InlineData("")]
        public void Parser_Ticker_FailClosed(string json)
        {
            Assert.False(UpbitPublicProtocol.TryParseTicker(U(json), UpbitPublicProtocol.MarketKrwBtc, out var t));
            Assert.Equal(default, t);
        }

        [Theory]
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[{\"ask_price\":100,\"bid_price\":100,\"ask_size\":1,\"bid_size\":1}]}]")]   // 교차/동일
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[{\"ask_price\":100,\"bid_price\":0,\"ask_size\":1,\"bid_size\":1}]}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[]}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"orderbook_units\":[{\"ask_price\":101,\"bid_price\":100,\"ask_size\":1,\"bid_size\":1}]}]")]
        [InlineData("[{\"market\":\"KRW-XRP\",\"timestamp\":1,\"orderbook_units\":[{\"ask_price\":101,\"bid_price\":100,\"ask_size\":1,\"bid_size\":1}]}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[{\"ask_price\":101,\"ask_price\":102,\"bid_price\":100,\"ask_size\":1,\"bid_size\":1}]}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[{\"ask_price\":101,\"bid_price\":100,\"ask_size\":-1,\"bid_size\":1}]}]")]
        [InlineData("[{\"market\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[],\"orderbook_units\":[]}]")]
        public void Parser_Orderbook_FailClosed(string json)
        {
            Assert.False(UpbitPublicProtocol.TryParseOrderbookL1(U(json), UpbitPublicProtocol.MarketKrwBtc, out var q));
            Assert.Equal(default, q);
        }

        [Fact]
        public void Parser_Orderbook_Uses_First_Level_Only()
        {
            Assert.True(UpbitPublicProtocol.TryParseOrderbookL1(U(BookJson), UpbitPublicProtocol.MarketKrwBtc, out var q));
            Assert.NotEqual(150_001_000 * W, q.AskRaw);
            Assert.NotEqual(149_989_000 * W, q.BidRaw);
        }

        [Fact]
        public void Parser_ZeroAlloc()
        {
            byte[] tj = U(TickerJson), bj = U(BookJson);
            UpbitPublicProtocol.TryParseTicker(tj, UpbitPublicProtocol.MarketKrwBtc, out _);
            UpbitPublicProtocol.TryParseOrderbookL1(bj, UpbitPublicProtocol.MarketKrwBtc, out _);
            int ok = 0;
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < 10_000; i++)
                {
                    if (UpbitPublicProtocol.TryParseTicker(tj, UpbitPublicProtocol.MarketKrwBtc, out _)) ok++;
                    if (UpbitPublicProtocol.TryParseOrderbookL1(bj, UpbitPublicProtocol.MarketKrwBtc, out _)) ok++;
                }
            });
            Assert.Equal(0, bytes);
            Assert.True(ok >= 20_000);
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Hits;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Hits++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
            }
        }

        [Fact]
        public async Task Guard_Allows_Only_Public_Quotation_Gets()
        {
            UpbitPublicFeedGuard.EnsureAllowed(UpbitPublicFeedGuard.Get(UpbitPublicFeedGuard.TickerUri));
            UpbitPublicFeedGuard.EnsureAllowed(UpbitPublicFeedGuard.Get(UpbitPublicFeedGuard.OrderbookUri));

            var bad = new List<HttpRequestMessage>
            {
                new(HttpMethod.Get, "https://api.upbit.com/v1/orders?markets=KRW-BTC"),                // 거래 API
                new(HttpMethod.Get, "https://api.upbit.com/v1/accounts"),
                new(HttpMethod.Post, UpbitPublicFeedGuard.TickerUri),
                new(HttpMethod.Get, "http://api.upbit.com/v1/ticker?markets=KRW-BTC"),
                new(HttpMethod.Get, "https://api.upbit.com.evil.example/v1/ticker?markets=KRW-BTC"),
                new(HttpMethod.Get, "https://api.upbit.com:8443/v1/ticker?markets=KRW-BTC"),
                new(HttpMethod.Get, "https://user@api.upbit.com/v1/ticker?markets=KRW-BTC"),
                new(HttpMethod.Get, "https://api.upbit.com/v1/ticker?markets=KRW-ETH"),
                new(HttpMethod.Get, "https://api.upbit.com/v1/ticker?markets=KRW-BTC#x"),
                new(HttpMethod.Get, UpbitPublicFeedGuard.TickerUri) { Content = new StringContent("{}") },
            };
            var auth = UpbitPublicFeedGuard.Get(UpbitPublicFeedGuard.TickerUri);
            auth.Headers.TryAddWithoutValidation("Authorization", "Bearer x");
            bad.Add(auth);

            var h = new CountingHandler();
            using var inv = new HttpMessageInvoker(h);
            foreach (var r in bad)
                await Assert.ThrowsAsync<InvalidOperationException>(() => UpbitPublicFeedGuard.SendAsync(inv, r, CancellationToken.None));
            Assert.Equal(0, h.Hits);                                            // 거부 = 핸들러 도달 0(패킷 0)
            using var ok = await UpbitPublicFeedGuard.SendAsync(inv, UpbitPublicFeedGuard.Get(UpbitPublicFeedGuard.TickerUri), CancellationToken.None);
            Assert.Equal(1, h.Hits);
        }

        [Fact]
        public void Guard_OptIn_SkipReason()
        {
            Assert.NotNull(UpbitPublicFeedGuard.SkipReason(_ => null));
            Assert.NotNull(UpbitPublicFeedGuard.SkipReason(_ => "true"));
            Assert.Null(UpbitPublicFeedGuard.SkipReason(k => k == UpbitPublicFeedGuard.OptInVar ? "1" : null));
        }

        // ── §5 리얼 와이어(옵트인) ──

        [UpbitPublicFeedFact]
        public async Task LiveWire_Upbit_Public_Quote_To_InMemory_Paper_Roundtrip()
        {
            using var http = UpbitPublicFeedGuard.CreateInvoker();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var rep = await UpbitPublicFeedProbe.RunAsync(http, UpbitPublicFeedProbe.DefaultRounds, cts.Token);
            foreach (var l in rep.Lines) _out.WriteLine(l);
            Assert.True(rep.OrderbookOk > 0, "호가 판독 0회");
            Assert.True(rep.PaperBuy.Filled, "가상 매수 " + rep.PaperBuy.Status);
            Assert.True(rep.PaperSell.Filled, "가상 매도 " + rep.PaperSell.Status);
            Assert.Equal(0, rep.PaperAfter.BtcRaw);
            Assert.True(rep.PaperAfter.KrwRaw < CryptoPaperExchange.InitialKrwRaw);
        }
    }
}

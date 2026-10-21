// ============================================================================
// FeedStallWatchdogTests — [v12.18.38-DEV] 전역 피드 동시 두절 워치독 회귀
//
//   §1 동시 두절: 두 피드 틱 0건 · 가상 시간 3,000ms = 무격발 / 3,000.001ms 폴링 = 비트 28(실 OrderPathLatencyInterlock 4차 입력 · 실 집계 마스크) Raise
//   §2 정상: 한 피드라도 수신 중 = 무격발(60초) · 미수신(비활성) 피드만 = 감시 대상 없음 · 한 피드만 활성 후 두절 = 격발
//   §3 장 시간: 장외 두절 = 무격발 · 장 진입 시각부터 예산 재기산(개장 첫 폴링 오격발 0) · 장중 판정 예외 = 장중 취급(엄격 측)
//   §4 재무장: 물리 이중키(한쪽 = 거부) · 두절 지속 = 다음 폴링 즉시 재래치 · 수신 재개 후 유지 · 4차 슬롯 교체 금지 · 판독 예외 = 차단 · 2/3차 슬롯 공존
//   §5 δ=0: 10만 회 폴링(수신 · 두절 · 래치 · 재무장 순환) 힙 0B(3창 최솟값) · 실 타이머(정적 콜백) 능동 격발 · 동시 수신 스레드 중 오탐 0 · 호스트 결선
//   §7 [v12.18.52] KRX 달력 일원화: 휴장 70일 × 24h 무패킷 오탐 0 · 08:45/15:25/시간외 동결 · 개장 예산 재기산 · 09:30/수능 10:30·16:00/연초 3,000.001ms 격발 ·
//      KIS 세션 침묵 임계 일원화 · 구형 평일/시각 판정 0 · 세션 횡단 10만 폴링 0B
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using StockManager.Engines.Arbitrage;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arbitrage
{
    [Collection("ConcurrencyStress")]
    public sealed class FeedStallWatchdogTests
    {
        private const int K = 0, S = 1;
        private const long Bit28Mask = 0x10000000L;
        private readonly ITestOutputHelper _out;

        public FeedStallWatchdogTests(ITestOutputHelper output) => _out = output;

        private sealed class Clock { public long Micros = 1_000_000; public long Now() => Micros; }

        private static (ArmingInterlockBitmask mask, OrderPathLatencyInterlock il) Bit28()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.OrderPathLatency);
            var il = new OrderPathLatencyInterlock(new OrderPathStageProfiler());
            il.BindInterlock(mask, ArmingInterlockSource.OrderPathLatency);
            Assert.True(mask.Seal(out _));
            return (mask, il);
        }

        // ════════════ §1 동시 두절 ════════════

        [Fact]
        public void Both_Feeds_Silent_Trip_Bit28_Exactly_Past_3000ms_Without_Any_Tick()
        {
            var c = new Clock();
            var (mask, il) = Bit28();
            var w = new FeedStallWatchdog(c.Now);
            w.BindLatencyInterlock(il);
            w.Stamp(K);
            c.Micros += 7_000; w.Stamp(S);                                                      // 마지막 수신 = KIS · 이후 두 피드 틱 0건
            long last = c.Micros;
            for (int i = 0; i < 30; i++) { c.Micros += 100_000; Assert.False(w.Poll()); }       // 100ms 폴링 × 30 = 정확히 3,000ms(미초과)
            Assert.Equal(3_000_000, c.Micros - last);
            Assert.False(w.Tripped);
            Assert.Equal(0, mask.Mask & Bit28Mask);
            c.Micros += 1;
            Assert.True(w.Poll());                                                              // ★3,000.001ms = 능동 격발(틱 이벤트 0)
            Assert.True(w.Tripped);
            Assert.Equal(3_000_001, w.LastStallMicros);
            Assert.Equal(1, w.Trips);
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);
            Assert.True(il.FeedBlocked && il.ArmingBlocked && !il.Tripped);                     // 4차 입력 OR · 자체 p99 래치와 분리
            c.Micros += 100_000;
            Assert.False(w.Poll());                                                             // sticky — 재격발 0 · 위반 계수
            Assert.Equal(2, w.Violations);
            Assert.Equal(1, w.Trips);
        }

        // ════════════ §2 정상 · 활성 판정 ════════════

        [Fact]
        public void Any_Live_Feed_Keeps_Watchdog_Quiet_And_Never_Seen_Feeds_Are_Inactive()
        {
            var c = new Clock();
            var w = new FeedStallWatchdog(c.Now);
            for (int i = 0; i < 600; i++) { c.Micros += 100_000; w.Poll(); }                   // 미수신(비활성) 60초 = 감시 대상 없음
            Assert.False(w.Tripped);
            w.Stamp(K); w.Stamp(S);
            for (int i = 0; i < 1_200; i++)                                                     // 60초 · 키움 50ms 마다 수신 · KIS 는 정지
            {
                c.Micros += 50_000;
                w.Stamp(K);
                if ((i & 1) == 0) Assert.False(w.Poll());
            }
            Assert.False(w.Tripped);                                                            // 한 피드라도 산 경우 = 무격발(한 피드 정지 = 레이더 침묵 감시 담당)
            Assert.Equal(0, w.Violations);
            Assert.Equal(0, w.MaxStallMicros);

            var c2 = new Clock();
            var w2 = new FeedStallWatchdog(c2.Now);
            w2.Stamp(S);                                                                        // KIS 만 활성(키움 미구성)
            c2.Micros += 3_000_001;
            Assert.True(w2.Poll());
            Assert.Throws<ArgumentOutOfRangeException>(() => new FeedStallWatchdog(stallMicros: 0));
            w2.Stamp(-1); w2.Stamp(FeedStallWatchdog.MaxVenues);                                // 범위 밖 venue = 무시
            Assert.Equal(0, w2.LastRxMicros(FeedStallWatchdog.MaxVenues));
        }

        // ════════════ §3 장 시간 ════════════

        [Fact]
        public void Off_Session_Is_Quiet_And_Session_Entry_Restarts_The_Budget()
        {
            var c = new Clock();
            bool market = false;
            var w = new FeedStallWatchdog(c.Now, () => market);
            w.Stamp(K); w.Stamp(S);
            c.Micros += 60_000_000;                                                             // 장후 · 밤새 정적
            Assert.False(w.Poll());
            market = true;                                                                      // 개장 — 마지막 수신은 60초 전
            Assert.False(w.Poll());                                                             // ★장 진입 첫 폴링 = 무격발(진입 시각부터 예산)
            long open = c.Micros;
            c.Micros = open + 3_000_000; Assert.False(w.Poll());
            c.Micros = open + 3_000_001; Assert.True(w.Poll());                                 // 개장 후 3초 넘게 수신 0 = 격발
            Assert.Equal(3_000_001, w.LastStallMicros);

            var c2 = new Clock();
            var w2 = new FeedStallWatchdog(c2.Now, () => throw new InvalidOperationException());
            w2.Stamp(K);
            c2.Micros += 3_000_001;
            Assert.True(w2.Poll());                                                             // 장중 판정 예외 = 장중 취급(엄격 측)
        }

        // ════════════ §4 재무장 · 결선 ════════════

        [Fact]
        public void Rearm_Needs_Both_Keys_Relatches_While_Stalled_And_Holds_After_Resume()
        {
            var c = new Clock();
            var (mask, il) = Bit28();
            var w = new FeedStallWatchdog(c.Now);
            w.BindLatencyInterlock(il);
            w.Stamp(K);
            c.Micros += 3_100_000; Assert.True(w.Poll());
            Assert.False(w.TryRearm(true, false));
            Assert.False(w.TryRearm(false, true));
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);
            Assert.True(w.TryRearm(true, true));
            Assert.Equal(0, mask.Mask & Bit28Mask);
            c.Micros += 100_000;
            Assert.True(w.Poll());                                                              // 두절 지속 = 다음 폴링 즉시 재래치
            Assert.Equal(2, w.Trips);
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);

            w.Stamp(K);                                                                         // 수신 재개
            Assert.True(w.TryRearm(true, true));
            for (int i = 0; i < 20; i++) { c.Micros += 100_000; w.Stamp(K); Assert.False(w.Poll()); }
            Assert.False(il.FeedBlocked);
            Assert.Equal(0, mask.Mask & Bit28Mask);
        }

        private sealed class ThrowingSource : IArmingBlockSource { public bool ArmingBlocked => throw new InvalidOperationException(); }

        [Fact]
        public void Stall_Slot_Coexists_With_Kis_And_Radar_Slots_And_Forbids_Replacement()
        {
            var (mask, il) = Bit28();
            new StockManager.Adapters.Kis.KisTimeSkewGuard().BindLatencyInterlock(il);           // 2차
            new SyntheticSpreadRadar().BindLatencyInterlock(il);                                 // 3차
            new FeedStallWatchdog().BindLatencyInterlock(il);                                   // 4차 — 별도 슬롯
            Assert.Throws<InvalidOperationException>(() => new FeedStallWatchdog().BindLatencyInterlock(il));   // 교체 금지
            Assert.False(il.FeedBlocked);
            Assert.Equal(0, mask.Mask & Bit28Mask);

            var il2 = new OrderPathLatencyInterlock(new OrderPathStageProfiler());
            il2.AttachStallGuard(new ThrowingSource());
            Assert.True(il2.FeedBlocked);                                                       // 판독 예외 = 차단(fail-closed)
            Assert.True(il2.ArmingBlocked);
        }

        // ════════════ §5 δ=0 · 실 타이머 · 동시성 ════════════

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        [Fact]
        public void HundredThousand_Polls_With_Stamps_Trips_And_Rearms_Allocate_Zero_Bytes()
        {
            var c = new Clock();
            var (mask, il) = Bit28();
            bool market = true;
            var w = new FeedStallWatchdog(c.Now, () => market);
            w.BindLatencyInterlock(il);
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    c.Micros += 100_000;
                    if ((i & 63) < 16) { w.Stamp(i & 1); w.Stamp((i >> 1) & 3); }               // 1.6초 수신 · 4.8초 동시 두절 반복
                    market = (i & 1023) != 1023;                                                // 장외 폴링 섞음
                    w.Poll();
                    if ((i & 63) == 0) w.TryRearm(true, true);
                }
            }
            Run();                                                                              // 예열(JIT)
            long alloc = MinAllocated(Run);
            _out.WriteLine($"[δ] 10만 폴링 × 3창 최소 = {alloc}B · 래치 {w.Trips} · 위반 {w.Violations} · 최대 두절 {w.MaxStallMicros}µs");
            Assert.Equal(0, alloc);
            Assert.True(w.Trips > 1_000);
            Assert.True(w.Polls >= 400_000);
            GC.KeepAlive(mask);
        }

        [Fact]
        public void Real_Timer_Trips_Actively_When_Feeds_Freeze()
        {
            var (mask, il) = Bit28();
            using var w = new FeedStallWatchdog(stallMicros: 150_000);
            w.BindLatencyInterlock(il);
            w.Stamp(K); w.Stamp(S);
            w.Start(10);
            w.Start(10);                                                                        // 중복 기동 = 무시
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!w.Tripped && sw.ElapsedMilliseconds < 10_000) Thread.Sleep(5);              // 틱 0건 — 타이머만으로 격발
            _out.WriteLine($"[타이머] 격발 {sw.ElapsedMilliseconds}ms · 폴링 {w.Polls} · 두절 {w.LastStallMicros}µs");
            Assert.True(w.Tripped);
            Assert.InRange(w.LastStallMicros, 150_001, 5_000_000);
            Assert.True(sw.ElapsedMilliseconds >= 140);                                        // 스톱워치는 Stamp 직후 시작(수 ms 오차)
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);
            w.Dispose();
            long polls = w.Polls;
            Thread.Sleep(60);
            Assert.InRange(w.Polls, polls, polls + 1);                                          // 처분 후 폴링 정지(진행 중 1회 허용)
        }

        [Fact]
        public void Concurrent_Feed_Stamps_Never_Look_Stalled()
        {
            using var w = new FeedStallWatchdog(stallMicros: 200_000);
            int stop = 0;
            using var done = new CountdownEvent(2);
            for (int v = 0; v < 2; v++)
            {
                var t = new Thread(s =>
                {
                    int venue = (int)s!;
                    while (Volatile.Read(ref stop) == 0) { w.Stamp(venue); Thread.SpinWait(200); }
                    done.Signal();
                }) { IsBackground = true };
                t.Start(v);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long polls = 0;
            while (sw.ElapsedMilliseconds < 500) { w.Poll(); polls++; Thread.Sleep(1); }
            Volatile.Write(ref stop, 1);
            Assert.True(done.Wait(5_000));                                                      // 조인 후 처분
            Assert.False(w.Tripped);
            Assert.Equal(0, w.Violations);
            Assert.True(polls > 10);
        }

        [Fact]
        public void Host_Wires_Watchdog_To_Bit28_Fourth_Slot_With_Session_Gate()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("new StockManager.Engines.Arbitrage.FeedStallWatchdog(marketHours: StockManager.Engines.OrderbookIlliquidityInterlock.KrxContinuousSessionNow)", src);
            Assert.Contains("feedStall.BindLatencyInterlock(orderPathLatency);", src);
            Assert.Contains("bridge.OrderBookReceived += _ => feedStall.Stamp(", src);
            Assert.Contains("bridge.TickReceived += _ => feedStall.Stamp(", src);
            Assert.Contains("feedStall.Start();", src);
            Assert.Contains("feedStall.Dispose", src);

            foreach (var t in typeof(FeedStallWatchdog).Assembly.GetType("StockManager.Engines.Arbitrage.FeedStallWatchdog")!.GetFields(
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                Assert.DoesNotContain("Broker", t.FieldType.FullName ?? "", StringComparison.OrdinalIgnoreCase);   // 브로커 · 발주 형식 참조 0
        }

        // ════════════ §6 [v12.18.39-DEV] KIS WebSocket 결선(듀얼 브로커) ════════════

        private static byte[] U8(string s) => System.Text.Encoding.ASCII.GetBytes(s);

        /// <summary>H0STASP0 본문(0 코드 · 1 BSOP_HOUR · 2 HOUR_CLS_CODE · 3~12 매도 · 13~22 매수 · 23~32 매도잔량 · 33~42 매수잔량 · 43/44 총잔량 · 45~62 기타).</summary>
        private static byte[] KisBookFrame(long bid = 70_000, long ask = 70_100)
        {
            var f = new System.Collections.Generic.List<string> { "005930", "093015", "0" };
            for (int l = 0; l < 10; l++) f.Add((ask + l * 100).ToString());
            for (int l = 0; l < 10; l++) f.Add((bid - l * 100).ToString());
            for (int l = 0; l < 20; l++) f.Add((1_000 + l).ToString());
            f.Add("10045"); f.Add("20045");
            while (f.Count < 63) f.Add("0");
            return U8("0|H0STASP0|001|" + string.Join("^", f));
        }

        private static byte[] KisTradeFrame()
        {
            var f = new string[MS2E.Infrastructure.KisProtocol.H0STCNT0FieldCount];
            Array.Fill(f, "0");
            f[0] = "005930"; f[1] = "093015"; f[2] = "70100"; f[12] = "37"; f[13] = "1000037"; f[21] = "1";
            return U8("0|H0STCNT0|001|" + string.Join("^", f));
        }

        private static StockManager.Adapters.KisOpenApiAdapter KisAdapter(FeedStallWatchdog w)
            => new(new StockManager.Adapters.KisSessionInterlock(null, 1, () => 1), stallWatch: w);

        [Fact]
        public void Venue_Ids_Match_Radar_Venues()
        {
            Assert.Equal(MS2E.UI.Portable.Ports.Trading.ArbRadar.VenueKiwoom, FeedStallWatchdog.VenueKiwoom);
            Assert.Equal(MS2E.UI.Portable.Ports.Trading.ArbRadar.VenueKis, FeedStallWatchdog.VenueKis);
        }

        [Fact]
        public void Kis_Market_Data_Frames_Alone_Keep_Watchdog_Alive_Then_Trip_When_Kis_Stops()
        {
            var c = new Clock();
            var (mask, il) = Bit28();
            var w = new FeedStallWatchdog(c.Now);
            w.BindLatencyInterlock(il);
            using var kis = KisAdapter(w);
            w.Stamp(FeedStallWatchdog.VenueKiwoom);                                             // 키움 1회 수신 후 영구 정지
            byte[] book = KisBookFrame(), trade = KisTradeFrame();
            for (int i = 0; i < 100; i++)                                                       // 10초 · KIS 실 어댑터 판독 경로만 생존
            {
                c.Micros += 100_000;
                Assert.True(kis.OnFrame((i & 1) == 0 ? book : trade));
                Assert.False(w.Poll());
            }
            Assert.False(w.Tripped);                                                            // ★KIS 단독 생존 = 정상 시장
            Assert.Equal(c.Micros, w.LastRxMicros(FeedStallWatchdog.VenueKis));                 // KIS 마지막 수신 = 실 판독 시각
            Assert.Equal(1_000_000, w.LastRxMicros(FeedStallWatchdog.VenueKiwoom));
            long kisLast = c.Micros;
            c.Micros = kisLast + 3_000_000; Assert.False(w.Poll());
            c.Micros = kisLast + 3_000_001; Assert.True(w.Poll());                              // KIS 도 멈춤 = 동시 두절 격발
            Assert.Equal(3_000_001, w.LastStallMicros);
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);
        }

        [Fact]
        public void Non_Market_Frames_Never_Count_As_Kis_Liveness()
        {
            var c = new Clock();
            var w = new FeedStallWatchdog(c.Now);
            using var kis = KisAdapter(w);
            w.Stamp(FeedStallWatchdog.VenueKiwoom);
            c.Micros += 500_000;
            kis.OnFrame(U8("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261021093015\"}}"));   // 서버 keepalive — 시세 정지 피드도 응답 가능
            kis.OnFrame(U8("0|H0STASP0|001|005930^093015"));                                    // 형식 이탈 호가
            kis.OnFrame(U8("1|H0STASP0|001|xx"));                                               // 암호화 호가(규격 외)
            kis.OnFrame(U8("0|H0STCNT0|001|bad"));                                              // 판독 0 체결
            kis.OnFrame(U8("garbage"));
            Assert.Equal(0, w.LastRxMicros(FeedStallWatchdog.VenueKis));                        // 생존 증거 0 — 맹목 은폐 방지
            c.Micros = 1_000_000 + 3_000_001;
            Assert.True(w.Poll());
        }

        [Fact]
        public void Stall_Is_Measured_From_The_Newest_Tick_Across_Both_Brokers()
        {
            var c = new Clock();
            var w = new FeedStallWatchdog(c.Now);
            using var kis = KisAdapter(w);
            byte[] book = KisBookFrame();
            w.Stamp(FeedStallWatchdog.VenueKiwoom);                                             // t0 키움
            c.Micros += 1_200_000; Assert.True(kis.OnFrame(book));                              // t0 + 1.2s KIS(더 최신)
            long newest = c.Micros;
            c.Micros = 1_000_000 + 3_000_001; Assert.False(w.Poll());                           // 키움 기준이면 3초 초과지만 KIS 기준 1.8초 = 무격발
            c.Micros = newest + 3_000_000; Assert.False(w.Poll());
            c.Micros = newest + 3_000_001; Assert.True(w.Poll());                               // 전체 최신 틱 기준 3,000.001ms = 격발
            Assert.Equal(3_000_001, w.LastStallMicros);

            var c2 = new Clock();
            var w2 = new FeedStallWatchdog(c2.Now);
            using var kis2 = KisAdapter(w2);
            for (int i = 0; i < 400; i++)                                                       // 40초 · 두 피드가 2.5초씩 번갈아 단독 생존
            {
                c2.Micros += 100_000;
                if ((i / 25 & 1) == 0) w2.Stamp(FeedStallWatchdog.VenueKiwoom); else Assert.True(kis2.OnFrame(book));
                Assert.False(w2.Poll());
            }
            Assert.Equal(0, w2.Violations);                                                     // ★어느 한 피드라도 틱 = 정상(교차 수신 오탐 0)
        }

        [Fact]
        public void Kis_Frame_Path_With_Stamp_Allocates_Zero_Bytes()
        {
            var c = new Clock();
            var w = new FeedStallWatchdog(c.Now);
            using var kis = KisAdapter(w);
            byte[] book = KisBookFrame(), trade = KisTradeFrame();
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    c.Micros += 10;
                    kis.OnFrame((i & 1) == 0 ? book : trade);
                    if ((i & 255) == 0) w.Poll();
                }
            }
            Run();                                                                              // 예열(JIT)
            long alloc = MinAllocated(Run);
            _out.WriteLine($"[δ] KIS 프레임 10만(호가/체결) + Stamp × 3창 최소 = {alloc}B · KIS 마지막 {w.LastRxMicros(FeedStallWatchdog.VenueKis)}µs");
            Assert.Equal(0, alloc);
            Assert.Equal(c.Micros, w.LastRxMicros(FeedStallWatchdog.VenueKis));
        }

        [Fact]
        public void Host_Wires_Kis_Adapter_To_Watchdog()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("stallWatch: feedStall,", src);
            Assert.Contains("feedStall.Stamp(StockManager.Engines.Arbitrage.FeedStallWatchdog.VenueKiwoom)", src);
            int created = src.IndexOf("var feedStall = new", StringComparison.Ordinal), used = src.IndexOf("stallWatch: feedStall", StringComparison.Ordinal);
            Assert.True(created > 0 && used > created);                                         // 워치독 생성 후 KIS 어댑터 결선
        }

        // ════════════ §7 [v12.18.52-DEV] KRX 달력 · 세션 페이즈 일원화(휴장 · 동시호가 · 시간외 = 동결 · 수능 순연) ════════════

        // 단조 시계(워치독) + UTC 벽시계(세션 판정)를 함께 전진시키는 리그 — 술어 = 호스트와 같은 단일 진실 공급원
        private sealed class SessionClock
        {
            public long Mono = 1_000_000, WallUtc;
            public long Now() => Mono;
            public bool Market() => StockManager.Engines.Feeds.KrxSessionStateMachine.InContinuousWatchWindow(WallUtc);
            public void Set(DateTime kst) { long w = (kst.Ticks - TimeSpan.TicksPerHour * 9 - DateTime.UnixEpoch.Ticks) / 10; Mono += Math.Max(0, w - WallUtc); WallUtc = w; }
            public void Advance(long micros) { Mono += micros; WallUtc += micros; }
        }

        private static DateTime Kst(int key, int h, int m, int s = 0) => new DateTime(key / 10_000, key / 100 % 100, key % 100, h, m, s);

        private static (FeedStallWatchdog w, SessionClock c, ArmingInterlockBitmask mask) SessionRig(DateTime kstStart)
        {
            var c = new SessionClock();
            c.Set(kstStart);
            var (mask, il) = Bit28();
            var w = new FeedStallWatchdog(c.Now, c.Market);
            w.BindLatencyInterlock(il);
            return (w, c, mask);
        }

        [Fact]
        public void Every_Weekday_Holiday_2024_2027_Full_Day_Silence_Never_Trips()
        {
            long polls = 0;
            for (int i = 0; i < StockManager.Engines.Feeds.KrxTradingCalendar.HolidayCount; i++)
            {
                int key = StockManager.Engines.Feeds.KrxTradingCalendar.HolidayAt(i);
                var (w, c, mask) = SessionRig(Kst(key, 0, 0));
                w.Stamp(K); w.Stamp(S);                                                         // 자정 1회 수신 후 23:59:59 까지 무패킷(익일 00:00 = 다른 날)
                for (int sec = 0; sec < 86_399; sec++) { c.Advance(1_000_000); w.Poll(); polls++; }
                Assert.False(w.Tripped, $"휴장 {key} 오탐");
                Assert.Equal(0, w.Violations);
                Assert.Equal(0, mask.Mask & Bit28Mask);
            }
            _out.WriteLine($"[휴장] {StockManager.Engines.Feeds.KrxTradingCalendar.HolidayCount}일 × 24시간 1초 폴링 = {polls:N0}회 · 오탐 0");
        }

        [Fact]
        public void Auction_And_After_Hours_Silence_Never_Trips_Then_Session_Start_Rebudgets()
        {
            const int D = 20261021;
            var (w, c, mask) = SessionRig(Kst(D, 8, 30));
            w.Stamp(K);                                                                         // 08:30 장전 1회 수신 후 무패킷
            for (int i = 0; i < 18_000; i++) { c.Advance(100_000); Assert.False(w.Poll()); }    // 08:30 → 09:00(08:45 포함) = 동결
            Assert.Equal(Kst(D, 9, 0).AddHours(-9).Ticks, DateTime.UnixEpoch.Ticks + c.WallUtc * 10);
            Assert.False(w.Poll());                                                             // 09:00 = 장 진입 관측 · 예산 재기산
            long open = c.Mono;
            c.Advance(3_000_000); Assert.False(w.Poll());
            c.Advance(1); Assert.True(w.Poll());                                                // 개장 후 3,000.001ms 무수신 = 격발
            Assert.Equal(3_000_001, w.LastStallMicros);
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);
            Assert.Equal(open + 3_000_001, c.Mono);

            var (w2, c2, m2) = SessionRig(Kst(D, 15, 19, 58));
            w2.Stamp(S);                                                                        // 15:19:58 마지막 수신
            for (int i = 0; i < 6_000; i++) { c2.Advance(100_000); Assert.False(w2.Poll()); }   // 15:20 마감 동시호가 · 15:25 · 15:30 시간외 → 15:29:58
            for (int i = 0; i < 12_000; i++) { c2.Advance(1_000_000); Assert.False(w2.Poll()); } // 시간외 · 장외(18:00 이후 18:49:58)까지
            Assert.False(w2.Tripped);
            Assert.Equal(0, w2.Violations);
            Assert.Equal(0, m2.Mask & Bit28Mask);
        }

        [Theory]
        [InlineData(20261021, 9, 30)]                                                           // 평일 연속매매
        [InlineData(20261119, 10, 30)]                                                          // 수능일 순연 연속매매
        [InlineData(20261119, 16, 0)]                                                           // 수능일 16:00 = 연속(평일이면 시간외)
        [InlineData(20270104, 10, 30)]                                                          // 연초 첫 거래일(10:00 개장)
        public void Continuous_Trading_Silence_Trips_Exactly_Past_3000ms(int key, int h, int m)
        {
            var (w, c, mask) = SessionRig(Kst(key, h, m));
            Assert.False(w.Poll());
            w.Stamp(K); w.Stamp(S);
            c.Advance(3_000_000); Assert.False(w.Poll());
            c.Advance(1); Assert.True(w.Poll());
            Assert.Equal(3_000_001, w.LastStallMicros);
            Assert.Equal(Bit28Mask, mask.Mask & Bit28Mask);
        }

        [Fact]
        public void Csat_Day_Pre_Open_Hour_And_Regular_Day_Post_1520_Are_Frozen()
        {
            var (w, c, _) = SessionRig(Kst(20261119, 9, 0));                                    // 수능일 09:00~10:00 = 장외/장전 동시호가
            w.Stamp(K);
            for (int i = 0; i < 3_599; i++) { c.Advance(1_000_000); Assert.False(w.Poll()); }
            Assert.Equal(0, w.Violations);
            c.Advance(1_000_000); Assert.False(w.Poll());                                       // 10:00 진입 = 예산 재기산
            c.Advance(3_000_001); Assert.True(w.Poll());

            var (r, rc, _) = SessionRig(Kst(20261021, 16, 0));                                  // 평일 16:00 = 시간외 = 동결
            r.Stamp(K);
            for (int i = 0; i < 600; i++) { rc.Advance(1_000_000); Assert.False(r.Poll()); }
            Assert.Equal(0, r.Violations);
        }

        [Fact]
        public void Kis_Session_Silence_Uses_Off_Session_Budget_On_Holidays_And_Auctions()
        {
            foreach (var (kst, market) in new[] { (Kst(20260501, 10, 0), false), (Kst(20261231, 11, 0), false), (Kst(20260924, 10, 0), false),
                                                   (Kst(20261021, 8, 45), false), (Kst(20261021, 15, 25), false), (Kst(20261119, 9, 30), false),
                                                   (Kst(20261021, 9, 30), true), (Kst(20261119, 10, 30), true) })
            {
                var utc = DateTime.SpecifyKind(kst.AddHours(-9), DateTimeKind.Utc);
                Assert.Equal(market, StockManager.Adapters.KisSessionInterlock.IsKrxRegularSession(utc));
                long mono = 1_000_000;
                var s = new StockManager.Adapters.KisSessionInterlock(null, 1, () => mono, marketHours: () => StockManager.Adapters.KisSessionInterlock.IsKrxRegularSession(utc));
                s.MarkConnected();
                mono += 10_000_000;                                                             // 10초 침묵: 장중 = 3초 임계 초과 · 장외 = 60초 임계 미만
                s.Evaluate();
                Assert.Equal(market, s.Tripped);
            }
        }

        [Fact]
        public void Session_Predicates_Have_No_Legacy_Weekday_Clock_Logic()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string guard = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "StockManager", "Engines", "OrderbookDepthGuard.cs"));
            string kis = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "StockManager", "Adapters", "KisOpenApiAdapter.cs"));
            Assert.Contains("public static bool KrxContinuousSessionNow() => StockManager.Engines.Feeds.KrxSessionStateMachine.InContinuousWatchWindowNow();", guard);
            Assert.Contains("=> StockManager.Engines.Feeds.KrxSessionStateMachine.InContinuousWatchWindow(", kis);
            Assert.DoesNotContain("DayOfWeek", guard);
            Assert.DoesNotContain("DayOfWeek", kis);
            // 달력 범위 밖 = 감시 유지(엄격 측) · 범위 안 휴장 = 동결
            Assert.True(StockManager.Engines.Feeds.KrxSessionStateMachine.InContinuousWatchWindow(long.MaxValue));
            Assert.False(StockManager.Engines.Feeds.KrxSessionStateMachine.InContinuousWatchWindow((Kst(20261225, 10, 0).AddHours(-9).Ticks - DateTime.UnixEpoch.Ticks) / 10));
        }

        [Fact]
        public void HundredThousand_Session_Aligned_Polls_Allocate_Zero_Bytes()
        {
            var (w, c, mask) = SessionRig(Kst(20261119, 8, 0));                                 // 수능일 장전 → 순연 연속 → 마감 동시호가 → 시간외 횡단
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    c.Advance(400_000);                                                         // 10만 × 400ms ≈ 11.1시간(08:00 → 19:06)
                    if ((i & 15) < 4) { w.Stamp(K); w.Stamp(S); }
                    w.Poll();
                    if ((i & 1023) == 0) w.TryRearm(true, true);
                }
            }
            Run();
            long alloc = ZeroAllocProbe.MinAllocated(Run, reset: () => c.Set(Kst(20261119, 8, 0)));
            _out.WriteLine($"[δ] 세션 일원화 10만 폴링(수능일 페이즈 횡단) = {alloc}B · 래치 {w.Trips} · 위반 {w.Violations}");
            Assert.Equal(0, alloc);
            Assert.True(w.Trips > 0);
            GC.KeepAlive(mask);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S26-b — Horizon C-1: 시간 도메인 3분할 두-갈래 신선도 펜스(Market/Mono/Wall).
    //   결정론적 클록 인젝터로 (1)네트워크 지연 폭격 (2)피드 완전 정지 (3)OS 시간 역행(NTP)을 모사하고,
    //   오염된 제안이 온실에 단 1비트도 침투 못 하고 _stalePauses 를 정확히 트리거함을 입증한다.
    //   펜스는 옵트인(DataFreshnessWindow/LivenessWindow) — 미설정 시 레거시 동작(후방호환).
    public sealed class S26bDualFenceTests
    {
        // 결정론 워프 클록 — wall(시스템 시계) 과 mono(단조 클록)를 독립 제어(NTP 역행 모사용).
        private sealed class WarpClock
        {
            public DateTime Wall = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            public long     Mono = 1_000_000L;                 // 임의 단조 시작점
            public DateTime GetWall() => Wall;
            public long     GetMono() => Mono;
        }
        private const long MonoFreq = TimeSpan.TicksPerSecond; // mono 단위 = 100ns 틱(직관적 테스트 산술)

        private static List<Candle> CandlesEndingAt(DateTime newest, int n = 120)
        {
            var list = new List<Candle>(n);
            for (int i = 0; i < n; i++)
            {
                var dt = DateTime.SpecifyKind(newest.AddDays(-(n - 1 - i)), DateTimeKind.Utc);   // 오래된→최신(마지막=newest)
                decimal close = 10000m + (decimal)(Math.Sin(i * 0.5) * 800.0) + i * 12m;
                list.Add(new Candle { Date = dt, Open = close, High = close * 1.01m, Low = close * 0.99m, Close = close, Volume = 1000 });
            }
            return list;
        }

        private static (GreenhouseService gh, PromotionGateway gw, WarpClock clk) Fenced(TimeSpan? wData, TimeSpan? wLive)
        {
            var clk = new WarpClock();
            var gw  = new PromotionGateway();
            var gh  = new GreenhouseService(gw)
            {
                Clock = clk.GetWall, MonoClock = clk.GetMono, MonoFrequency = MonoFreq,
                DataFreshnessWindow = wData, LivenessWindow = wLive,
            };
            return (gh, gw, clk);
        }

        private static void Prime(GreenhouseService gh, DateTime newest)
            => gh.Prime(GreenhouseActivator.DefaultSeeds(), CandlesEndingAt(newest), Market.KRX_KOSPI);

        // ═══════════════════ 신선 경로 — As-Of = 마켓시각 ═══════════════════

        [Fact]
        public void Fresh_StampsMarketTimeAsOf_CapturesNetworkDelay()
        {
            // 캔들은 3분 전 마켓시각(네트워크 지연 δ=3min), wall=now. 둘 다 관대 → 통과·진화.
            //   As-Of 가 인제스트(wall=now)가 아닌 마켓시각(newest)으로 박제돼 now−As-Of=3min(진짜 나이) 임을 증명.
            var (gh, gw, clk) = Fenced(TimeSpan.FromHours(24), TimeSpan.FromHours(24));
            var newest = clk.Wall.AddMinutes(-3);
            Prime(gh, newest);
            gh.RunCycleOnce();

            Assert.NotNull(gw.Current);
            Assert.Equal(newest.Ticks, gw.Current!.AsOfTicks);          // As-Of = 마켓시각(인제스트 아님)
            Assert.Equal(0, gh.StalePauses);
            Assert.True(gh.Probe().Mutations > 0);
        }

        // ═══════════════════ 카오스 (1) 네트워크 지연 폭격 ═══════════════════

        [Fact]
        public void Chaos_NetworkDelay_DataAgeExceeded_FailsClosed()
        {
            // Δt_network > W_data — 캔들 마켓시각은 그대로인데 wall 이 6분 경과. liveness(W_live=30min)는 관대.
            var (gh, gw, clk) = Fenced(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
            Prime(gh, clk.Wall);                       // 신선 prime
            clk.Wall = clk.Wall.AddMinutes(6);         // 데이터 시장-나이 6min > 5min
            clk.Mono += 6 * 60 * MonoFreq;             // 단조도 동반(liveness 6min < 30min → 미발화)
            gh.RunCycleOnce();

            Assert.Equal(1, gh.StalePauses);           // 데이터-나이 펜스 발화
            Assert.Equal(0, gh.Probe().Mutations);     // 진화 0 — 오염 데이터 침투 0
            Assert.Null(gw.Current);                   // 챔피언 미제출
        }

        // ═══════════════════ 카오스 (2) 하드웨어 피드 완전 정지 ═══════════════════

        [Fact]
        public void Chaos_FeedDead_IngestLivenessExceeded_FailsClosed()
        {
            // ingestAge > W_live — 단조 31s 경과(피드 정지). 데이터-나이(W_data=24h)는 관대(마켓 신선).
            var (gh, gw, clk) = Fenced(TimeSpan.FromHours(24), TimeSpan.FromSeconds(30));
            Prime(gh, clk.Wall);
            clk.Mono += 31 * MonoFreq;                 // 단조 liveness 31s > 30s. wall/market 불변.
            gh.RunCycleOnce();

            Assert.Equal(1, gh.StalePauses);           // liveness 펜스 발화
            Assert.Equal(0, gh.Probe().Mutations);
            Assert.Null(gw.Current);
        }

        // ═══════════════════ 카오스 (3) OS 시간 역행 (NTP 스텝) ═══════════════════

        [Fact]
        public void Chaos_NtpStepBackward_MonotonicLiveness_StillFiresOnDeadFeed()
        {
            // 단조 liveness 는 wall 역행에 면역 — 피드 죽으면(mono 경과) wall 이 과거로 스텝해도 정확히 fail-closed.
            //   (wall 기반 liveness 였다면 역행으로 '신선'이라 속아 fail-OPEN 되었을 것.)
            var (gh, gw, clk) = Fenced(wData: null, wLive: TimeSpan.FromSeconds(30));
            Prime(gh, clk.Wall);
            clk.Mono += 31 * MonoFreq;                 // 피드 죽음(단조 31s)
            clk.Wall  = clk.Wall.AddHours(-1);         // NTP 역행 — wall 은 1시간 과거
            gh.RunCycleOnce();

            Assert.Equal(1, gh.StalePauses);           // 단조 기준 정확히 발화(wall 역행 무관)
            Assert.Equal(0, gh.Probe().Mutations);
        }

        [Fact]
        public void Chaos_MonotonicClockRewind_FailsClosed()
        {
            // ★적대적 리뷰(major) 회귀 — 단조 클록 '역행'(멀티코어 TSC 미동기·VM live-migration·비단조 주입)이면
            //   monoΔ<0. 과거 코드 `음수 > 임계값`=false → 미발화(fail-OPEN). 이제 음수 Δ=클록 모호 → fail-closed 트립.
            var (gh, gw, clk) = Fenced(wData: null, wLive: TimeSpan.FromSeconds(30));
            Prime(gh, clk.Wall);
            clk.Mono -= 5 * MonoFreq;                  // 단조 역행(5s 후퇴) — monoΔ = −5s
            gh.RunCycleOnce();

            Assert.Equal(1, gh.StalePauses);           // 음수 Δ fail-closed(과거엔 fail-open)
            Assert.Equal(0, gh.Probe().Mutations);
            Assert.Null(gw.Current);
        }

        [Fact]
        public void Chaos_NtpStepBackward_LiveFeed_DoesNotFalseTrip()
        {
            // 대조 — 피드 살아있고(mono 불변) wall 만 역행 → liveness 미발화(정상 진화). liveness 가 mono 추적 확증.
            var (gh, _, clk) = Fenced(wData: null, wLive: TimeSpan.FromSeconds(30));
            Prime(gh, clk.Wall);
            clk.Wall = clk.Wall.AddHours(-1);          // wall 역행만, 단조 불변(피드 신선)
            gh.RunCycleOnce();

            Assert.Equal(0, gh.StalePauses);           // 단조 기준 신선 → 미발화
            Assert.True(gh.Probe().Mutations > 0);     // 정상 진화(wall 역행 무관)
        }

        // ═══════════════════ 시간 도메인 정준화 — 미래 데이터(KST↔UTC 스큐) ═══════════════════

        [Fact]
        public void Chaos_FutureDated_DomainSkew_FailsClosed()
        {
            // 마켓시각이 wall+9h(KST 를 UTC 로 오인) → marketAge < −허용오차 → 도메인 스큐로 fail-closed.
            var (gh, gw, clk) = Fenced(TimeSpan.FromHours(24), TimeSpan.FromHours(24));
            Prime(gh, clk.Wall.AddHours(9));           // 9시간 미래 데이터
            gh.RunCycleOnce();

            Assert.Equal(1, gh.StalePauses);           // 미래-데이터 양면 바운드 발화
            Assert.Equal(0, gh.Probe().Mutations);
            Assert.Null(gw.Current);
        }

        // ═══════════════════ 후방호환 — 펜스 OFF 시 레거시 동작 ═══════════════════

        [Fact]
        public void FenceOff_LegacyBehavior_FutureDataNotBlockedByC1()
        {
            // C-1 미옵트인(DataFreshnessWindow/LivenessWindow/MonoClock 미설정) → 미래 데이터여도 C-1 미적용(레거시 진화).
            var clk = new WarpClock();
            var gw  = new PromotionGateway();
            var gh  = new GreenhouseService(gw) { Clock = clk.GetWall };   // C-1 전부 OFF
            gh.Prime(GreenhouseActivator.DefaultSeeds(), CandlesEndingAt(clk.Wall.AddHours(9)), Market.KRX_KOSPI);
            gh.RunCycleOnce();

            Assert.Equal(0, gh.StalePauses);           // C-1 펜스 미작동
            Assert.True(gh.Probe().Mutations > 0);     // 레거시 동작 보존(후방호환)
        }

        // ═══════════════════ 시간 도메인 정준화 — BulkheadCandleSource Local-kind ═══════════════════

        private sealed class LocalDateProvider : IQuoteProvider
        {
            private readonly List<Candle> _candles;
            public LocalDateProvider(List<Candle> c) => _candles = c;
            public DataSource Source => default;
            public Market[] SupportedMarkets => Array.Empty<Market>();
            public bool IsRealtime => false;
            public Task<Quote?> GetQuoteAsync(string s, Market m, CancellationToken ct = default) => Task.FromResult<Quote?>(null);
            public Task<List<Quote>> GetQuotesAsync(IEnumerable<string> s, Market m, CancellationToken ct = default) => Task.FromResult(new List<Quote>());
            public Task<List<Candle>> GetHistoryAsync(string s, Market m, DateTime from, DateTime to, CancellationToken ct = default) => Task.FromResult(_candles);
            public event EventHandler<Quote>? QuoteUpdated { add { } remove { } }   // CS0067 회피
            public Task SubscribeAsync(string s, Market m) => Task.CompletedTask;
            public Task UnsubscribeAsync(string s, Market m) => Task.CompletedTask;
        }

        [Fact]
        public void Bulkhead_LocalKindCandle_NormalizedToUtcInstant()
        {
            // ★적대적 리뷰(minor) 보강 — Local-kind 캔들이 ToUniversalTime 으로 '같은 순간'의 UTC 로 정준화(틱 이동).
            //   기존 테스트는 전부 Unspecified(SpecifyKind 분기)라 Local 분기가 미검증이었다.
            var localDt = new DateTime(2026, 6, 1, 18, 0, 0, DateTimeKind.Local);
            var list = new List<Candle> { new Candle { Date = localDt, Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1000 } };
            var src = new BulkheadCandleSource(new LocalDateProvider(list), "069500", Market.KRX_KOSPI);
            var buf = new Candle[1]; buf[0] = new Candle();

            int n = src.FillLatest(buf);
            Assert.Equal(1, n);
            Assert.Equal(localDt.ToUniversalTime().Ticks, buf[0].Date.Ticks);   // 같은 순간(틱 이동)
            Assert.Equal(DateTimeKind.Utc, buf[0].Date.Kind);                   // UTC 라벨
        }
    }
}

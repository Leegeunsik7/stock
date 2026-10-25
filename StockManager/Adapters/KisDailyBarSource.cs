// ============================================================================
// KisDailyBarSource — v12.18.80-DEV: 플루토 병렬 학습 스웜 국내(KRX) 실데이터 소스 = 호스트 KisProvider 일봉(inquire-daily-itemchartprice · 인증 구성 시에만)
//   [v12.18.81-DEV] 미국(US) 겸용 = KisProvider 해외 일봉(HHDFS76240000 · 시세 계열 EXCD) · 티커 → 상장 거래소 = UsListingExchange(미등재 = 조회 0 · NoData).
//
//   KIS App Key/Secret(OAuth2) 미구성 호스트 = 이 소스 미부착 → 스웜 KRX/US 레인 '실피드 연결 대기'(합성 점수 0).
//   종목 1건 = 일봉 1회(최근 150일 · 최대 200봉 · 해외는 응답 1회 최대 100봉) · 송신 간격 = PlutoRequestPacer 초당 1회 —
//   ★KRX/US 두 레인이 같은 KisProvider 게이트(초당 5회 · 실시간 시세와 공유)를 쓰므로 호스트는 페이서 1개를 두 소스에 공유한다(합산 초당 1회).
//   KisProvider 게이트 거부(RateGated 증가) = RateLimited(스윕 중단 · 재시도 0) · 빈 응답 = NoData · 예외 = Faulted.
//   가격 = KRX 원(정수) · US 센트(달러 × 100 반올림) · 거래량 = 주. 호가/체결 방향 없음 → 미시구조 판독 없음(0점 · 날조 0) · 밸류에이션 = 스웜 레인 PlutoValuationBuffer.
//   ★liveArmed:false — 시세 GET 만.
// ============================================================================
using System;
using System.Globalization;
using System.Threading;
using MS2E.Infrastructure;
using StockManager.Engines.Brokers.Kis;
using StockManager.Engines.Cognitive.Screening;

namespace StockManager.Adapters
{
    public sealed class KisDailyBarSource : IPlutoAssetDataSource
    {
        public const int LookbackDays = 150;

        private readonly KisProvider _kis;
        private readonly PlutoRequestPacer _pacer;
        private readonly PlutoScreenMarket _market;

        public KisDailyBarSource(KisProvider kis, PlutoRequestPacer? pacer = null, PlutoScreenMarket market = PlutoScreenMarket.Krx)
        {
            _kis = kis ?? throw new ArgumentNullException(nameof(kis));
            if (market != PlutoScreenMarket.Krx && market != PlutoScreenMarket.Us) throw new ArgumentOutOfRangeException(nameof(market));
            _pacer = pacer ?? new PlutoRequestPacer(1);
            _market = market;
        }

        public PlutoScreenMarket Market => _market;
        public bool IsConnected => true;
        public string Label => (_market == PlutoScreenMarket.Us ? "KIS 해외 일봉 REST" : "KIS 일봉 REST")
            + "(인증 구성 · 초당 " + _pacer.PerSecond.ToString(CultureInfo.InvariantCulture) + "회 이하)";

        /// <summary>US 티커 → KisProvider 시장(상장 거래소 미등재 = null).</summary>
        public static MS2E.Infrastructure.Market? UsMarketOf(string ticker) => UsListingExchange.Of(ticker) switch
        {
            UsExchange.Nasdaq => MS2E.Infrastructure.Market.US_NASDAQ,
            UsExchange.Nyse => MS2E.Infrastructure.Market.US_NYSE,
            UsExchange.Amex => MS2E.Infrastructure.Market.US_AMEX,
            _ => null,
        };

        public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(sample);
            if (entry.Market != _market) return PlutoFetchOutcome.Faulted;
            MS2E.Infrastructure.Market market;
            if (_market == PlutoScreenMarket.Us)
            {
                if (UsMarketOf(entry.Code) is not { } us) return PlutoFetchOutcome.NoData;      // 거래소 미상 = 조회 0(레이트 예산 보존)
                market = us;
            }
            else market = entry.Segment == PlutoUniverseSegment.Kosdaq ? MS2E.Infrastructure.Market.KRX_KOSDAQ : MS2E.Infrastructure.Market.KRX_KOSPI;
            if (!_pacer.Wait(ct)) { ct.ThrowIfCancellationRequested(); return PlutoFetchOutcome.Faulted; }
            long gatedBefore = _kis.RateGated;
            var to = DateTime.Now.Date;
            var candles = _kis.GetHistoryAsync(entry.Code, market, to.AddDays(-LookbackDays), to, ct).GetAwaiter().GetResult();
            if (_kis.RateGated != gatedBefore) return PlutoFetchOutcome.RateLimited;
            if (candles.Count == 0) return PlutoFetchOutcome.NoData;
            candles.Sort(static (a, b) => a.Date.CompareTo(b.Date));            // 오래된 것부터
            bool cents = _market == PlutoScreenMarket.Us;
            int start = Math.Max(0, candles.Count - sample.Bars.Length), n = 0;
            for (int i = start; i < candles.Count; i++)
            {
                var c = candles[i];
                if (c.High <= 0 || c.Low <= 0 || c.Close <= 0) continue;
                sample.Bars[n++] = new PlutoOhlcvBar(DateTime.SpecifyKind(c.Date, DateTimeKind.Utc).Ticks,
                    Px(c.Open, cents), Px(c.High, cents), Px(c.Low, cents), Px(c.Close, cents), Math.Max(0, c.Volume));
            }
            sample.BarCount = n;
            return n == 0 ? PlutoFetchOutcome.NoData : PlutoFetchOutcome.Ok;
        }

        private static long Px(decimal v, bool cents) => cents ? (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero) : (long)v;
    }
}

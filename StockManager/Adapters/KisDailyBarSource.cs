// ============================================================================
// KisDailyBarSource — v12.18.80-DEV: 플루토 병렬 학습 스웜 국내(KRX) 실데이터 소스 = 호스트 KisProvider 일봉(inquire-daily-itemchartprice · 인증 구성 시에만)
//
//   KIS App Key/Secret(OAuth2) 미구성 호스트 = 이 소스 미부착 → 스웜 KRX 레인 '실피드 연결 대기'(합성 점수 0).
//   종목 1건 = 일봉 1회(최근 150일 · 최대 200봉) · 송신 간격 = PlutoRequestPacer 초당 1회(KisProvider 자체 게이트 초당 5회 · 실시간 시세와 예산 공유 → 여유 보존).
//   KisProvider 게이트 거부(RateGated 증가) = RateLimited(스윕 중단 · 재시도 0) · 빈 응답 = NoData · 예외 = Faulted.
//   가격 = 원(정수) · 거래량 = 주. 호가/체결 방향 없음 → 미시구조 판독 없음(0점 · 날조 0) · 밸류에이션 = 스웜 레인 PlutoValuationBuffer.
//   ★미국(US)은 KisProvider 해외 일봉 미지원 → 소스 미부착(정직 '실피드 연결 대기'). ★liveArmed:false — 시세 GET 만.
// ============================================================================
using System;
using System.Globalization;
using System.Threading;
using MS2E.Infrastructure;
using StockManager.Engines.Cognitive.Screening;

namespace StockManager.Adapters
{
    public sealed class KisDailyBarSource : IPlutoAssetDataSource
    {
        public const int LookbackDays = 150;

        private readonly KisProvider _kis;
        private readonly PlutoRequestPacer _pacer;

        public KisDailyBarSource(KisProvider kis, PlutoRequestPacer? pacer = null)
        {
            _kis = kis ?? throw new ArgumentNullException(nameof(kis));
            _pacer = pacer ?? new PlutoRequestPacer(1);
        }

        public PlutoScreenMarket Market => PlutoScreenMarket.Krx;
        public bool IsConnected => true;
        public string Label => "KIS 일봉 REST(인증 구성 · 초당 " + _pacer.PerSecond.ToString(CultureInfo.InvariantCulture) + "회 이하)";

        public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(sample);
            if (!_pacer.Wait(ct)) { ct.ThrowIfCancellationRequested(); return PlutoFetchOutcome.Faulted; }
            long gatedBefore = _kis.RateGated;
            var to = DateTime.Now.Date;
            var market = entry.Segment == PlutoUniverseSegment.Kosdaq ? MS2E.Infrastructure.Market.KRX_KOSDAQ : MS2E.Infrastructure.Market.KRX_KOSPI;
            var candles = _kis.GetHistoryAsync(entry.Code, market, to.AddDays(-LookbackDays), to, ct).GetAwaiter().GetResult();
            if (_kis.RateGated != gatedBefore) return PlutoFetchOutcome.RateLimited;
            if (candles.Count == 0) return PlutoFetchOutcome.NoData;
            candles.Sort(static (a, b) => a.Date.CompareTo(b.Date));            // 오래된 것부터
            int start = Math.Max(0, candles.Count - sample.Bars.Length), n = 0;
            for (int i = start; i < candles.Count; i++)
            {
                var c = candles[i];
                if (c.High <= 0 || c.Low <= 0 || c.Close <= 0) continue;
                sample.Bars[n++] = new PlutoOhlcvBar(DateTime.SpecifyKind(c.Date, DateTimeKind.Utc).Ticks,
                    (long)c.Open, (long)c.High, (long)c.Low, (long)c.Close, Math.Max(0, c.Volume));
            }
            sample.BarCount = n;
            return n == 0 ? PlutoFetchOutcome.NoData : PlutoFetchOutcome.Ok;
        }
    }
}

// ============================================================================
// BulkheadCandleSource — S25-a: 방벽 최외각 격리 어댑터 (IQuoteProvider → ICandleSource)
//
//  안전 경계 '바깥'의 가장자리. 외부 API(IQuoteProvider, 비결정·가변·네트워크)를 시스템의 유일한
//  진입로인 FillLatest 계약으로 변환한다. 어떤 네트워크 예외·지연·null·불완전 청크가 와도 결코
//  내부 코어로 새어 들어가지 못한다.
//
//  ── 방벽 불변식 ─────────────────────────────────────────────────────────────
//  "네트워크는 지연·고갈만 시킬 수 있다(→ 0 반환 → Insufficient → 무프라임 → staleness fail-closed),
//   안전 결정은 오염시킬 수 없다." 모든 예외·null·빈응답 → 0 반환(부분 데이터도 통째 드롭).
//   값 검증(음수·단조성·OHLC)은 안쪽 LiveCandleFeed.Validate 가 마지막 관문으로 거른다(이중 방어).
//   버퍼 인스턴스 in-place 덮어쓰기는 GreenhouseService.Prime 의 값 복제(deep copy)로 온실과 격리됨(S25-a).
//
//  ⚠️ 라이브 왕복은 CI 미검증(실 provider 부재). 코어 계약은 fake provider 로 검증, 실환경 검증은
//     사용자 측. IQuoteProvider.GetHistoryAsync 는 List 를 할당하므로 이 가장자리는 무할당 아님 —
//     단 인그레스 스레드 한정(배양 핫패스 무접촉).
// ============================================================================
using System;

namespace StockManager.Common
{
    public sealed class BulkheadCandleSource : ICandleSource
    {
        private readonly IQuoteProvider  _provider;
        private readonly string          _symbol;
        private readonly Market          _market;
        private readonly int             _lookbackDays;
        private readonly Func<DateTime>? _clock;   // 테스트용 주입 클록

        public BulkheadCandleSource(
            IQuoteProvider  provider,
            string          symbol,
            Market          market,
            int             lookbackDays = 220,
            Func<DateTime>? clock        = null)
        {
            _provider     = provider ?? throw new ArgumentNullException(nameof(provider));
            _symbol       = symbol   ?? throw new ArgumentNullException(nameof(symbol));
            _market       = market;
            _lookbackDays = lookbackDays < 10 ? 10 : lookbackDays;
            _clock        = clock;
        }

        public int FillLatest(Candle[] buffer)
        {
            if (buffer is null || buffer.Length == 0) return 0;
            try
            {
                var to   = (_clock ?? (static () => DateTime.UtcNow))();
                var from = to.AddDays(-_lookbackDays);

                var candles = _provider.GetHistoryAsync(_symbol, _market, from, to).GetAwaiter().GetResult();
                if (candles is null || candles.Count == 0) return 0;

                int k     = Math.Min(candles.Count, buffer.Length);
                int start = candles.Count - k;   // 최신 k개
                for (int i = 0; i < k; i++)
                {
                    var c = candles[start + i];
                    if (c is null) return 0;      // 불완전 원소 → 통째 드롭(부분 오염 차단)
                    var b = buffer[i];            // 재사용 인스턴스 필드 덮어쓰기
                    b.Date   = c.Date;
                    b.Open   = c.Open;
                    b.High   = c.High;
                    b.Low    = c.Low;
                    b.Close  = c.Close;
                    b.Volume = c.Volume;
                }
                return k;
            }
            catch
            {
                // 네트워크 단절·타임아웃·역직렬화 오류 등 일체 → 0(고갈). 내부 코어로 예외/오염 무전파.
                return 0;
            }
        }
    }
}

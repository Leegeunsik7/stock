// ============================================================================
// LiveCandleFeed — S23-b: 라이브 캔들 → 온실 재-Prime 시seam (규칙1 메모리 + 규칙2 검증)
//
//  합성 BootstrapCandles 빗장을 풀고 실 캔들 윈도우를 주기적으로 온실에 재주입한다.
//  네트워크 경계(ICandleSource)는 주입식 — 코어는 결정론·CI 검증 가능, 실 provider 는 호스트가 배선.
//
//  ── 규칙1: isDirty + 제로 할당 fetch ───────────────────────────────────────
//  · fetch 는 '사전 할당된 단일 재사용 버퍼'에 소스가 필드를 덮어쓴다 → fetch 측 할당 0.
//  · 시그니처로 데이터 변화를 비트 판정 → 불변이면 Prime 자체를 숏서킷(할당·재진화 0).
//  · 더블버퍼는 불필요하다: GreenhouseService.Prime 이 ToArray 로 동기 방어 복사를 하므로,
//    재사용 버퍼는 Prime 반환 시점에 안전하게 재충전 가능. 유일한 할당은 '변화 시 1회'의 Prime 스냅샷.
//    (측정 없는 풀링은 미신 — 올바른 고도는 isDirty + 단일 버퍼다.)
//
//  ── 규칙2: 검증 → 손상/부족은 드롭(fail-closed 위임) ────────────────────────
//  · 개수 부족·단조성 위반·비유한/음수 OHLC 윈도우는 거부하고 마지막 유효 스냅샷을 유지한다.
//  · 소스 예외·부족·손상으로 Prime 을 못 하면, 온실 StalenessWindow 가 시간 경과 후 fail-closed.
//    (LiveCandleFeed 는 '나쁜 데이터를 막고', 온실은 '낡으면 멈춘다' — 책임 분리.)
//
//  ⚠️ Refresh 는 단일 드라이버 스레드 전용(스레드 세이프 아님). 온실 스레드는 이 버퍼를 만지지 않는다.
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    /// <summary>라이브 캔들 소스(네트워크 경계 추상화). buffer 의 기존 Candle 필드를 덮어쓰고 개수 반환.</summary>
    public interface ICandleSource
    {
        int FillLatest(Candle[] buffer);
    }

    public enum FeedOutcome
    {
        Reprimed,      // 새 유효 윈도우로 온실 재-Prime 완료
        Unchanged,     // 데이터 불변(isDirty=false) → 숏서킷
        Insufficient,  // 개수 부족(< minWindow) → 드롭
        Corrupt,       // 검증 실패(단조성·OHLC) → 드롭
        SourceError    // 소스 예외 → 드롭
    }

    public sealed class LiveCandleFeed
    {
        private readonly GreenhouseService           _gh;
        private readonly ICandleSource               _source;
        private readonly IReadOnlyList<StrategySpec> _seeds;
        private readonly Market                      _market;
        private readonly int                         _minWindow;
        private readonly Candle[]                     _buffer;   // 사전 할당 재사용 버퍼(fetch 할당 0)

        private long _lastSig;
        private bool _primed;
        private long _reprimes, _drops;

        public LiveCandleFeed(
            GreenhouseService           greenhouse,
            ICandleSource               source,
            IReadOnlyList<StrategySpec> seeds,
            Market                      market,
            int                         windowSize = 120,
            int                         minWindow  = 60)
        {
            _gh        = greenhouse ?? throw new ArgumentNullException(nameof(greenhouse));
            _source    = source     ?? throw new ArgumentNullException(nameof(source));
            _seeds     = seeds      ?? throw new ArgumentNullException(nameof(seeds));
            _market    = market;
            _minWindow = minWindow < 1 ? 1 : minWindow;
            if (windowSize < _minWindow) windowSize = _minWindow;
            _buffer = new Candle[windowSize];
            for (int i = 0; i < windowSize; i++) _buffer[i] = new Candle();   // 재사용 인스턴스 사전 할당
        }

        public long Reprimes => _reprimes;
        public long Drops     => _drops;

        /// <summary>1회 갱신: 소스 fetch → 검증 → isDirty 면 온실 재-Prime. 단일 드라이버 스레드에서 호출.</summary>
        public FeedOutcome Refresh()
        {
            int n;
            try { n = _source.FillLatest(_buffer); }
            catch { _drops++; return FeedOutcome.SourceError; }   // 소스 장애 흡수 → 마지막 유효 유지

            if (n < _minWindow) { _drops++; return FeedOutcome.Insufficient; }
            if (n > _buffer.Length) n = _buffer.Length;

            if (!Validate(_buffer, n)) { _drops++; return FeedOutcome.Corrupt; }   // 손상 윈도우 거부

            long sig = Signature(_buffer, n);
            if (_primed && sig == _lastSig) return FeedOutcome.Unchanged;          // isDirty=false → 숏서킷

            _lastSig = sig;
            _primed  = true;
            // ArraySegment<Candle> 는 IReadOnlyList<Candle> — 온실이 ToArray 로 스냅샷(변경 시 1회 할당)
            _gh.Prime(_seeds, new ArraySegment<Candle>(_buffer, 0, n), _market);
            _reprimes++;
            return FeedOutcome.Reprimed;
        }

        // 규칙2 검증 — 단조 증가 타임스탬프·유한/양수 OHLC·High≥Close≥Low. 하나라도 위반 시 거부.
        private static bool Validate(Candle[] buf, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is null) return false;
                if (c.Open <= 0m || c.Close <= 0m) return false;
                if (c.High < c.Low || c.High < c.Close || c.Close < c.Low) return false;
                if (c.Volume < 0) return false;
                if (i > 0 && buf[i].Date <= buf[i - 1].Date) return false;   // 역순·중복 거부
            }
            return true;
        }

        // 저비용 dirty 시그니처 — 첫·끝 캔들 시각 + 끝 종가 + 개수(변화 비트 판정).
        private static long Signature(Candle[] buf, int n)
        {
            var first = buf[0];
            var last  = buf[n - 1];
            unchecked
            {
                long s = first.Date.Ticks;
                s = (s * 397) ^ last.Date.Ticks;
                s = (s * 397) ^ (long)(last.Close * 1000m);
                s = (s * 397) ^ n;
                return s;
            }
        }
    }
}

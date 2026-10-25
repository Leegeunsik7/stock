// ============================================================================
// PlutoMultiAssetDataLake — v12.18.80-DEV: 플루토 3대 자산 실데이터 레이크(420슬롯 × 최근 봉 200개 · 슬롯 seqlock · 수집 통계)
//
//   스웜 워커가 종목 조회 성공 시 실측 봉(오래된 것부터)을 슬롯에 통째로 교체 기록 → 학습/백테스트 소비자가 TryCopyBars 로 판독(δ=0 · 찢어진 판독 0).
//   저장 = 시장별 슬롯 영역(카탈로그 순서 · PlutoUniverseMatrixBoard 와 같은 전역 슬롯 번호) · 연속 배열 1개(420 × 200 봉 ≈ 4MB · 구축 1회 · 이후 할당 0).
//   수집 통계 = 슬롯별 마지막 결과/시각 + 시장별 조회 · 성공 · 레이트 리밋 · 실패 누계(Interlocked).
//   ★실측만 저장(합성/보간 0) · 조회 실패/무데이터 = 슬롯 봉 수 0 으로 비움(오래된 봉 재사용 0 · fail-closed).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>시장 1개 수집 누계(판독 스냅샷 · 값 타입).</summary>
    public readonly struct PlutoLakeStats
    {
        public readonly long Fetches, Ok, NoData, RateLimited, Faulted, Sweeps;

        public PlutoLakeStats(long fetches, long ok, long noData, long rateLimited, long faulted, long sweeps)
        {
            Fetches = fetches; Ok = ok; NoData = noData; RateLimited = rateLimited; Faulted = faulted; Sweeps = sweeps;
        }
    }

    public sealed class PlutoMultiAssetDataLake
    {
        public const int BarsPerSlot = PlutoAssetSample.MaxBars, ReadSpinLimit = 64;

        private readonly PlutoUniverseMatrixBoard _layout;
        private readonly PlutoOhlcvBar[] _bars;
        private readonly int[] _barCount, _version;
        private readonly long[] _asOf;
        private readonly byte[] _lastOutcome;
        private readonly long[] _stats = new long[PlutoRecommendationSnapshot.Markets * 6];   // [market × (fetch · ok · noData · rate · fault · sweep)]

        public PlutoMultiAssetDataLake(PlutoUniverseMatrixBoard layout)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            int n = layout.SlotCount;
            _bars = new PlutoOhlcvBar[n * BarsPerSlot];
            _barCount = new int[n];
            _version = new int[n];
            _asOf = new long[n];
            _lastOutcome = new byte[n];
            for (int i = 0; i < n; i++) _lastOutcome[i] = (byte)PlutoFetchOutcome.NotConnected;
        }

        public PlutoUniverseMatrixBoard Layout => _layout;
        public int SlotCount => _barCount.Length;

        /// <summary>조회 결과 기록(그 슬롯 시장 워커 단일 작성자 · Ok = 봉 교체 · 그 외 = 봉 비움 · 할당 0).</summary>
        public void Record(int slot, PlutoFetchOutcome outcome, ReadOnlySpan<PlutoOhlcvBar> barsOldestFirst, long asOfUtcTicks)
        {
            if ((uint)slot >= (uint)_barCount.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            int keep = outcome == PlutoFetchOutcome.Ok ? Math.Min(barsOldestFirst.Length, BarsPerSlot) : 0;
            Interlocked.Increment(ref _version[slot]);
            if (keep > 0) barsOldestFirst.Slice(barsOldestFirst.Length - keep).CopyTo(_bars.AsSpan(slot * BarsPerSlot, keep));
            _barCount[slot] = keep;
            _asOf[slot] = asOfUtcTicks;
            _lastOutcome[slot] = (byte)outcome;
            Volatile.Write(ref _version[slot], _version[slot] + 1);
            int m = MarketOf(slot);
            if (outcome == PlutoFetchOutcome.NotConnected) return;
            Interlocked.Increment(ref _stats[m * 6]);
            Interlocked.Increment(ref _stats[m * 6 + 1 + Math.Min((int)outcome, 3)]);
        }

        /// <summary>시장 스윕 1회 완료 계수.</summary>
        public void CountSweep(PlutoScreenMarket m)
        {
            if ((uint)m < PlutoRecommendationSnapshot.Markets) Interlocked.Increment(ref _stats[(int)m * 6 + 5]);
        }

        /// <summary>슬롯 봉 복사(오래된 것부터 · 반환 = 복사 수 · 쓰는 중 재시도 초과 = −1 · δ=0).</summary>
        public int TryCopyBars(int slot, Span<PlutoOhlcvBar> destination, out long asOfUtcTicks)
        {
            asOfUtcTicks = 0;
            if ((uint)slot >= (uint)_barCount.Length) return -1;
            for (int spin = 0; spin < ReadSpinLimit; spin++)
            {
                int v1 = Volatile.Read(ref _version[slot]);
                if ((v1 & 1) != 0) { Thread.SpinWait(4); continue; }
                int count = Math.Clamp(Volatile.Read(ref _barCount[slot]), 0, BarsPerSlot);   // 1회 판독(찢어진 길이 → 범위 예외 0)
                int n = Math.Min(count, destination.Length);
                _bars.AsSpan(slot * BarsPerSlot + (count - n), n).CopyTo(destination);
                long asOf = _asOf[slot];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _version[slot]) != v1) continue;
                asOfUtcTicks = asOf;
                return n;
            }
            return -1;
        }

        public PlutoFetchOutcome LastOutcome(int slot)
            => (uint)slot < (uint)_lastOutcome.Length ? (PlutoFetchOutcome)Volatile.Read(ref _lastOutcome[slot]) : PlutoFetchOutcome.NotConnected;

        public PlutoLakeStats StatsOf(PlutoScreenMarket m)
        {
            if ((uint)m >= PlutoRecommendationSnapshot.Markets) return default;
            int b = (int)m * 6;
            return new PlutoLakeStats(Interlocked.Read(ref _stats[b]), Interlocked.Read(ref _stats[b + 1]), Interlocked.Read(ref _stats[b + 2]),
                Interlocked.Read(ref _stats[b + 3]), Interlocked.Read(ref _stats[b + 4]), Interlocked.Read(ref _stats[b + 5]));
        }

        private int MarketOf(int slot)
        {
            for (int m = PlutoRecommendationSnapshot.Markets - 1; m >= 0; m--)
                if (slot >= _layout.OffsetOf((PlutoScreenMarket)m)) return m;
            return 0;
        }
    }
}

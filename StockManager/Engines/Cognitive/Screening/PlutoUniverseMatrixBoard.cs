// ============================================================================
// PlutoUniverseMatrixBoard — v12.18.80-DEV: 플루토 3대 자산 420슬롯 유니버스 매트릭스(카탈로그 슬롯별 최신 스크리닝 입력 · 슬롯 seqlock · δ=0 인덱스 조회)
//
//   슬롯 배치 = 카탈로그 순서 고정: KRX [0, 200) · US [200, 300) · 업비트 KRW [300, 420)(PlutoMultiAssetUniverseCatalog.Of(market) 인덱스 + 시장 오프셋).
//   셀 = 상태(Awaiting 실피드 연결 대기 · Live 실데이터 판독 · Faulted 조회 실패 · NoData 응답 봉 없음) + 스크리닝 입력 + 판독 시각 + 봉 수.
//   ★단일 작성자 = 그 시장 스웜 워커(슬롯 영역이 시장별로 분리 → 슬롯당 작성자 1) · 판독자 다수(스크리너 · UI 집계) = 슬롯 seqlock(홀수 = 쓰는 중).
//   TryRead(slot) · TryFindSlot(시장, 종목 키 · 정렬 long[] 이분 탐색) · 시장별 상태 수(Interlocked 계수) — 전부 할당 0(δ=0).
//   ★Live 셀 = 소스가 실제로 받은 봉/호가로만 만든 입력(날조 0). Awaiting/NoData/Faulted 셀은 스크리너가 점수 산출 대상에서 제외.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.Screening
{
    public enum PlutoMatrixStatus : byte
    {
        Awaiting = 0,   // 감시 중 · 실피드 연결 대기(소스 미결선 · 첫 조회 전)
        Live = 1,       // 실데이터 판독
        NoData = 2,     // 응답 정상 · 봉/호가 없음(거래 정지 · 상장 폐지 등)
        Faulted = 3,    // 전송/파싱 실패 · 레이트 리밋(직전 Live 값 폐기 — 오래된 판독 재사용 0)
    }

    /// <summary>슬롯 1개 판독 값(불변 값 타입).</summary>
    public readonly struct PlutoMatrixCell
    {
        public readonly PlutoMatrixStatus Status;
        public readonly PlutoScreenInputs Inputs;
        public readonly long AsOfUtcTicks;
        public readonly int Bars;

        public PlutoMatrixCell(PlutoMatrixStatus status, in PlutoScreenInputs inputs, long asOfUtcTicks, int bars)
        {
            Status = status; Inputs = inputs; AsOfUtcTicks = asOfUtcTicks; Bars = bars;
        }

        public bool IsLive => Status == PlutoMatrixStatus.Live;
    }

    public sealed class PlutoUniverseMatrixBoard
    {
        public const int StatusKinds = 4, ReadSpinLimit = 64;

        private struct Slot
        {
            public int Version;
            public PlutoMatrixCell Cell;
        }

        private readonly Slot[] _slots;
        private readonly int[] _offset = new int[PlutoRecommendationSnapshot.Markets];
        private readonly int[] _count = new int[PlutoRecommendationSnapshot.Markets];
        private readonly int[] _statusCount;                         // [market × StatusKinds]
        private readonly long[][] _sortedKeys = new long[PlutoRecommendationSnapshot.Markets][];
        private readonly int[][] _sortedSlots = new int[PlutoRecommendationSnapshot.Markets][];
        private long _writes;

        public PlutoUniverseMatrixBoard()
        {
            int total = 0;
            for (int m = 0; m < PlutoRecommendationSnapshot.Markets; m++)
            {
                var list = PlutoMultiAssetUniverseCatalog.Of((PlutoScreenMarket)m);
                _offset[m] = total;
                _count[m] = list.Count;
                total += list.Count;
                int keyed = 0;
                for (int i = 0; i < list.Count; i++) if (list[i].Symbol > 0) keyed++;
                var keys = new long[keyed];
                var slots = new int[keyed];
                for (int i = 0, k = 0; i < list.Count; i++)
                    if (list[i].Symbol > 0) { keys[k] = list[i].Symbol; slots[k] = _offset[m] + i; k++; }
                Array.Sort(keys, slots);
                _sortedKeys[m] = keys;
                _sortedSlots[m] = slots;
            }
            _slots = new Slot[total];
            _statusCount = new int[PlutoRecommendationSnapshot.Markets * StatusKinds];
            for (int m = 0; m < PlutoRecommendationSnapshot.Markets; m++) _statusCount[m * StatusKinds + (int)PlutoMatrixStatus.Awaiting] = _count[m];
        }

        public int SlotCount => _slots.Length;
        public long Writes => Interlocked.Read(ref _writes);

        public int OffsetOf(PlutoScreenMarket m) => (uint)m < (uint)_offset.Length ? _offset[(int)m] : -1;
        public int CountOf(PlutoScreenMarket m) => (uint)m < (uint)_count.Length ? _count[(int)m] : 0;

        /// <summary>카탈로그 인덱스 → 전역 슬롯(범위 밖 = −1 · δ=0).</summary>
        public int SlotOf(PlutoScreenMarket m, int catalogIndex)
            => (uint)m < (uint)_count.Length && (uint)catalogIndex < (uint)_count[(int)m] ? _offset[(int)m] + catalogIndex : -1;

        /// <summary>종목 키 → 전역 슬롯(이분 탐색 · 미등록/키 인코딩 불가 = false · δ=0).</summary>
        public bool TryFindSlot(PlutoScreenMarket m, long symbol, out int slot)
        {
            slot = -1;
            if ((uint)m >= (uint)_sortedKeys.Length || symbol <= 0) return false;
            int i = Array.BinarySearch(_sortedKeys[(int)m], symbol);
            if (i < 0) return false;
            slot = _sortedSlots[(int)m][i];
            return true;
        }

        /// <summary>시장 상태별 슬롯 수(Interlocked 계수 · 판독 = 필드 · δ=0).</summary>
        public int CountOf(PlutoScreenMarket m, PlutoMatrixStatus status)
            => (uint)m < (uint)_count.Length && (uint)status < StatusKinds ? Volatile.Read(ref _statusCount[(int)m * StatusKinds + (int)status]) : 0;

        /// <summary>슬롯 판독(seqlock · 쓰는 중 재시도 ReadSpinLimit 회 초과 = false · 찢어진 값 반환 0 · δ=0).</summary>
        public bool TryRead(int slot, out PlutoMatrixCell cell)
        {
            if ((uint)slot >= (uint)_slots.Length) { cell = default; return false; }
            ref Slot s = ref _slots[slot];
            for (int spin = 0; spin < ReadSpinLimit; spin++)
            {
                int v1 = Volatile.Read(ref s.Version);
                if ((v1 & 1) != 0) { Thread.SpinWait(4); continue; }
                cell = s.Cell;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref s.Version) == v1) return true;
            }
            cell = default;
            return false;
        }

        /// <summary>슬롯 기록(그 시장 워커 단일 작성자 전용 · 상태 계수 갱신 · δ=0).</summary>
        public void Write(int slot, PlutoMatrixStatus status, in PlutoScreenInputs inputs, long asOfUtcTicks, int bars)
        {
            if ((uint)slot >= (uint)_slots.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            if ((uint)status >= StatusKinds) throw new ArgumentOutOfRangeException(nameof(status));
            int m = MarketOfSlot(slot);
            ref Slot s = ref _slots[slot];
            var old = s.Cell.Status;
            Interlocked.Increment(ref s.Version);                                   // 홀수 = 쓰는 중(전체 펜스)
            s.Cell = new PlutoMatrixCell(status, status == PlutoMatrixStatus.Live ? inputs : default, asOfUtcTicks, Math.Max(0, bars));
            Volatile.Write(ref s.Version, s.Version + 1);                           // 짝수 = 게시(릴리스)
            if (old != status)
            {
                Interlocked.Decrement(ref _statusCount[m * StatusKinds + (int)old]);
                Interlocked.Increment(ref _statusCount[m * StatusKinds + (int)status]);
            }
            Interlocked.Increment(ref _writes);
        }

        private int MarketOfSlot(int slot)
        {
            for (int m = _offset.Length - 1; m >= 0; m--) if (slot >= _offset[m]) return m;
            return 0;
        }

        /// <summary>시장 Live 셀 → 스크리너 유니버스(Live ∧ 종목 키 있음만 · 스크리너 스텝 스레드 · δ=0).</summary>
        public PlutoUniverseScanner Scanner(PlutoScreenMarket market)
        {
            if ((uint)market >= (uint)_count.Length) throw new ArgumentOutOfRangeException(nameof(market));
            return dest =>
            {
                int k = 0, off = _offset[(int)market], n = _count[(int)market];
                for (int i = 0; i < n && k < dest.Length; i++)
                    if (TryRead(off + i, out var c) && c.IsLive && c.Inputs.Symbol > 0 && c.Inputs.Market == market) dest[k++] = c.Inputs;
                return k;
            };
        }
    }

    /// <summary>스크리너 유니버스 합성(primary 우선 · 같은 종목 키 중복 = primary 유지 · 콜드 스캔 스레드).</summary>
    public static class PlutoUniverseMerge
    {
        /// <summary>조건부 유니버스(realFeed false = 0건 — 아레나 합성 데모 시세가 추천 점수에 섞이지 않게 · fail-closed).</summary>
        public static PlutoUniverseScanner When(Func<bool> realFeed, PlutoUniverseScanner scan)
        {
            ArgumentNullException.ThrowIfNull(realFeed);
            ArgumentNullException.ThrowIfNull(scan);
            return dest => realFeed() ? scan(dest) : 0;
        }

        public static PlutoUniverseScanner Merge(PlutoUniverseScanner primary, PlutoUniverseScanner secondary)
        {
            ArgumentNullException.ThrowIfNull(primary);
            ArgumentNullException.ThrowIfNull(secondary);
            var extra = new PlutoScreenInputs[PlutoMarketScreener.MaxUniverse];
            return dest =>
            {
                int n = Math.Clamp(primary(dest), 0, dest.Length);
                int e = Math.Clamp(secondary(extra), 0, extra.Length);
                for (int j = 0; j < e && n < dest.Length; j++)
                {
                    long sym = extra[j].Symbol;
                    bool dup = false;
                    for (int i = 0; i < n; i++) if (dest[i].Symbol == sym) { dup = true; break; }
                    if (!dup) dest[n++] = extra[j];
                }
                return n;
            };
        }
    }
}

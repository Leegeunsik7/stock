// ============================================================================
// PlutoEpisodicMemoryStore — v12.18.77-DEV: 플루토 지속 학습 Track B — 역사적 시장 국면 에피소딕 기억창고(특성 벡터 · 유사 국면 탐색 · 0~100%)
//
//   에피소드 1건 = 이름 · 국면 유형(PlutoEpisodeKind) · 특성 벡터 6차원(VIX×100 · 미 10년물 변화 bp · 달러 인덱스 변화 bp · 원/달러 변화 bp · 지수 추세 bp · 감성 −100~+100).
//   [v12.18.82-DEV] 유사도 = PlutoRegimeVectorLake 결합 척도(차원별 −100~+100 정규화 · 코사인 × 유클리드 커널 · 관측 ∩ 에피소드 공통 차원 · 최소 2차원)를
//     정수 %로 반올림(동일 = 100% · 단조 감소 · 차원 2개 미만 = −1 판정 불가) — 국면 판정기(PlutoRegimeMatcher)와 같은 척도라 UI 두 뱃지가 어긋나지 않는다.
//   시드 = 12대 역사적 경제 사이클(PlutoRegimeVectorLake · 공개 문헌 대표치의 근사 · 시계열 적합 아님 · IsSeed = true · UI '시드 근사' 표기). id = 레이크 국면 id(1~12).
//     학습 확장: Remember(이름 · 유형 · 관측) = 현재 국면을 새 에피소드로 저장(용량 32 · 포화 = 거부 · 덮어쓰기 0).
//   ★Track B 전용 콜드 경로(체결 스레드는 호출하지 않는다). FindNearest 힙 할당 0(고정 배열 · double 연산). 등록은 단일 잠금(쓰기 드묾).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.Learning
{
    public enum PlutoEpisodeKind : byte
    {
        Unknown = 0,
        Bull = 1,
        Bear = 2,
        Range = 3,
        Panic = 4,
        Liquidity = 5,
    }

    /// <summary>에피소드 1건(이름 = 표시용 · 특성 = PlutoMacroSnapshot 의 값 필드 재사용).</summary>
    public sealed record PlutoEpisode(int Id, string Name, PlutoEpisodeKind Kind, PlutoMacroSnapshot Features, bool IsSeed);

    /// <summary>유사 국면 매칭 결과(값 타입 · EpisodeId 0 = 판정 불가).</summary>
    public readonly struct PlutoEpisodeMatch
    {
        public readonly int EpisodeId, SimilarityPct, Dimensions;

        public PlutoEpisodeMatch(int episodeId, int similarityPct, int dimensions)
        {
            EpisodeId = episodeId; SimilarityPct = similarityPct; Dimensions = dimensions;
        }

        public bool Found => EpisodeId > 0;
    }

    public sealed class PlutoEpisodicMemoryStore
    {
        public const int Capacity = 32, MinDimensions = PlutoRegimeVectorLake.MinDimensions;

        private readonly PlutoEpisode?[] _episodes = new PlutoEpisode?[Capacity];
        private readonly object _gate = new();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public PlutoEpisode? Get(int id)
        {
            if (id <= 0 || id > Volatile.Read(ref _count)) return null;
            return Volatile.Read(ref _episodes[id - 1]);
        }

        /// <summary>에피소드 등록(관측 무결성 · 이름 필수 · 포화 = false). 반환 id = 1 기반.</summary>
        public int Remember(string name, PlutoEpisodeKind kind, in PlutoMacroSnapshot features, bool isSeed = false)
        {
            if (string.IsNullOrWhiteSpace(name) || !features.IsWellFormed || kind == PlutoEpisodeKind.Unknown) return 0;
            lock (_gate)
            {
                if (_count >= Capacity) return 0;
                int id = _count + 1;
                Volatile.Write(ref _episodes[_count], new PlutoEpisode(id, name, kind, features, isSeed));
                Volatile.Write(ref _count, id);
                return id;
            }
        }

        /// <summary>[v12.18.82-DEV] 12대 역사적 국면 시드(PlutoRegimeVectorLake · 6차원 전부 · id = 국면 id). 이미 등록된 것이 있으면 생략.</summary>
        public void SeedDefaults()
        {
            if (Count > 0) return;
            for (int id = 1; id <= PlutoRegimeVectorLake.Count; id++)
            {
                var c = PlutoRegimeVectorLake.Get(id)!;
                var raw = c.Raw;
                Remember(c.Name, c.Kind, in raw, isSeed: true);
            }
        }

        /// <summary>
        /// 관측 vs 에피소드 유사도(0~100 · 레이크 결합 척도 % 반올림). 공통 차원 = 관측 마스크 ∩ 에피소드 마스크 · 2차원 미만 = −1(판정 불가). dims = 사용 차원 수.
        /// </summary>
        public static int Similarity(in PlutoMacroSnapshot observed, in PlutoMacroSnapshot episode, out int dims)
        {
            int x10 = PlutoRegimeVectorLake.SimilarityX10(in observed, in episode, out dims, out _, out _);
            return x10 < 0 ? -1 : (x10 + 5) / 10;
        }

        /// <summary>가장 유사한 에피소드(δ=0). 기억 0건 · 공통 차원 부족 = default(Found false). 동률 = 먼저 등록된 에피소드.</summary>
        public PlutoEpisodeMatch FindNearest(in PlutoMacroSnapshot observed)
        {
            int n = Volatile.Read(ref _count), bestId = 0, bestSim = -1, bestDims = 0;
            for (int i = 0; i < n; i++)
            {
                var e = Volatile.Read(ref _episodes[i]);
                if (e is null) continue;
                var features = e.Features;
                int sim = Similarity(in observed, in features, out int dims);
                if (sim > bestSim) { bestSim = sim; bestId = e.Id; bestDims = dims; }
            }
            return bestSim < 0 ? default : new PlutoEpisodeMatch(bestId, bestSim, bestDims);
        }
    }
}

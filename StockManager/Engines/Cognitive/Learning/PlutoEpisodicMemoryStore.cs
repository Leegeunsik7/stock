// ============================================================================
// PlutoEpisodicMemoryStore — v12.18.77-DEV: 플루토 지속 학습 Track B — 역사적 시장 국면 에피소딕 기억창고(특성 벡터 · 유사 국면 탐색 · 0~100%)
//
//   에피소드 1건 = 이름 · 국면 유형(PlutoEpisodeKind) · 특성 벡터 6차원(VIX×100 · 미 10년물 변화 bp · 달러 인덱스 변화 bp · 원/달러 변화 bp · 지수 추세 bp · 감성 −100~+100).
//   유사도(현재 관측 vs 에피소드): 관측에 존재하는 차원만(PlutoMacroField 마스크 · 최소 2차원) 차원별 척도로 정규화한 제곱 거리 평균 d² →
//     유사도 = round(100 · exp(−d²)) (가우시안 커널 · 동일 = 100% · 척도 1배 차이 = 37% · 단조 감소). 차원 2개 미만 = 판정 불가(−1).
//   시드 에피소드 8종(SeedDefaults) = 대표 국면의 '특성 근사치'(손으로 보정한 정성 시드 · 실측 적합 아님 · IsSeed = true · UI 가 '시드 근사' 로 표기).
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
        public const int Capacity = 32, MinDimensions = 2;
        /// <summary>차원별 정규화 척도(이 만큼 차이 = 1σ).</summary>
        public const double VixScaleX100 = 1000, Us10yScaleBp = 50, DollarScaleBp = 300, UsdKrwScaleBp = 300, TrendScaleBp = 800, SentimentScale = 40;

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

        /// <summary>대표 국면 시드 8종(특성 근사치 · 6차원 전부). 이미 등록된 것이 있으면 생략.</summary>
        public void SeedDefaults()
        {
            if (Count > 0) return;
            long t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            Seed("2008 금융위기 패닉", PlutoEpisodeKind.Panic, 6000, -80, 600, 1500, -2500, -80, t);
            Seed("2020 코로나 폭락", PlutoEpisodeKind.Panic, 5500, -100, 300, 600, -3000, -70, t);
            Seed("2020-21 유동성 장세", PlutoEpisodeKind.Liquidity, 2200, 10, -200, -300, 800, 40, t);
            Seed("2022 금리 인상 하락장", PlutoEpisodeKind.Bear, 3000, 60, 300, 500, -900, -40, t);
            Seed("2024 AI 랠리", PlutoEpisodeKind.Bull, 1400, 5, 50, 100, 600, 50, t);
            Seed("2015-16 박스권", PlutoEpisodeKind.Range, 1800, 0, 0, 50, 0, 0, t);
            Seed("2018 4분기 급락", PlutoEpisodeKind.Bear, 2800, -20, 100, 200, -1300, -30, t);
            Seed("2017 저변동 상승", PlutoEpisodeKind.Bull, 1100, 5, -100, -100, 300, 30, t);
        }

        private void Seed(string name, PlutoEpisodeKind kind, int vix, int us10y, int dxy, int krw, int trend, int sentiment, long t) =>
            Remember(name, kind, new PlutoMacroSnapshot(PlutoMacroField.All, vix, us10y, dxy, krw, trend, sentiment, t), isSeed: true);

        /// <summary>
        /// 관측 vs 에피소드 유사도(0~100). 공통 차원 = 관측 마스크 ∩ 에피소드 마스크 · 2차원 미만 = −1(판정 불가). dims = 사용 차원 수.
        /// </summary>
        public static int Similarity(in PlutoMacroSnapshot observed, in PlutoMacroSnapshot episode, out int dims)
        {
            var common = observed.Fields & episode.Fields;
            double sum = 0;
            dims = 0;
            if ((common & PlutoMacroField.Vix) != 0) { sum += Sq(observed.VixX100 - episode.VixX100, VixScaleX100); dims++; }
            if ((common & PlutoMacroField.Us10y) != 0) { sum += Sq(observed.Us10yChangeBp - episode.Us10yChangeBp, Us10yScaleBp); dims++; }
            if ((common & PlutoMacroField.Dollar) != 0) { sum += Sq(observed.DollarIndexChangeBp - episode.DollarIndexChangeBp, DollarScaleBp); dims++; }
            if ((common & PlutoMacroField.UsdKrw) != 0) { sum += Sq(observed.UsdKrwChangeBp - episode.UsdKrwChangeBp, UsdKrwScaleBp); dims++; }
            if ((common & PlutoMacroField.EquityTrend) != 0) { sum += Sq(observed.EquityTrendBp - episode.EquityTrendBp, TrendScaleBp); dims++; }
            if ((common & PlutoMacroField.Sentiment) != 0) { sum += Sq(observed.SentimentIndex - episode.SentimentIndex, SentimentScale); dims++; }
            if (dims < MinDimensions) return -1;
            return (int)Math.Round(100 * Math.Exp(-sum / dims));
        }

        private static double Sq(int diff, double scale)
        {
            double z = diff / scale;
            return z * z;
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

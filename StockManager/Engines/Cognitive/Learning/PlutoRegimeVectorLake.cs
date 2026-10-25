// ============================================================================
// PlutoRegimeVectorLake — v12.18.82-DEV: 플루토 에피소딕 기억 고도화 — 12대 역사적 경제 사이클 레짐 벡터 레이크(정규화 −100~+100 · 코사인 × 유클리드 결합 유사도 · δ=0)
//
//   국면 1건(PlutoHistoricalCycle) = 이름 · 기간 · 유형(PlutoEpisodeKind) · 포지셔닝 전략(PlutoRegimeStrategy) · 해설 · 원 특성 6차원(PlutoMacroSnapshot 단위 그대로 ·
//     VIX×100 · 미 10년물 변화 bp · 달러 인덱스 변화 bp · 원/달러 변화 bp · 지수 추세 bp · 감성 −100~+100).
//   원 특성값 = 공개 문헌의 국면 대표치(VIX 고점/평균 · 금리/환율 월간 변화 · 지수 낙폭/상승폭)를 '국면 대표 1개월 창' 으로 환산한 근사치 — 시계열 적합이 아니다(정직 표기 ·
//     UI 는 기존과 같이 '시드 근사' 로 표기). ※ 볼린저 밴드 대역폭 · 장단기 금리차는 실시간 관측 파이프(PlutoGlobalDataIngestor)에 필드가 없어 차원에서 제외(매칭 불가 차원 0).
//   정규화(Normalize): 차원별 중심 · 척도로 z = round((원값 − 중심) × 100 / 척도) → −100~+100 포화(VIX 중심 20 · 척도 40pt / 금리 100bp / 달러 600bp / 원화 1,500bp /
//     추세 3,000bp / 감성 100). 국면 벡터는 형식 초기화 시 1회 정규화(콜드) → 고정 배열.
//   유사도(SimilarityX10 · 0~1000 = 0.0~100.0%): 관측 마스크 ∩ 국면 마스크 공통 차원만(최소 2차원 · 미만 = −1 판정 불가)
//     코사인 계수 = (1 + cos θ) / 2(방향 일치 · 양쪽 영벡터 = 1 · 한쪽만 영벡터 = 0.5) × 거리 계수 = exp(−평균 제곱 거리 / σ²)(σ = 40 정규화 점 · 크기 일치)
//     → 동일 벡터 = 1000 · 방향 반대 = 0 · 거리 단조 감소. 두 계수 곱 = 방향(국면 성격)과 강도(충격 크기)가 모두 맞아야 높은 유사도.
//   FindNearest(관측) = 12대 국면 최고 유사도(동률 = 앞선 국면 · δ=0 · 고정 배열 · double 연산 · 잠금 0).
//   ★QuantumVault · 피어 · 네트워크 접근 0(내장 상수 테이블). ★liveArmed:false — 가상 원장 진입 판정 입력만.
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>매칭된 과거 국면에서 승률이 높았던 포지셔닝 전략(거버너 가중치 편향으로 매핑).</summary>
    public enum PlutoRegimeStrategy : byte
    {
        None = 0,
        /// <summary>추세 추종(모멘텀 tilt 가산).</summary>
        TrendFollow = 1,
        /// <summary>평균 회귀(모멘텀 tilt 감산 · 돌파 추격 억제).</summary>
        MeanReversion = 2,
        /// <summary>악마의 변호인 강화(엄격도 가산 → 원확신도 감점 · 거부권 빈도 증가).</summary>
        DevilsAdvocate = 3,
    }

    /// <summary>역사적 경제 사이클 1건(불변 · 원 특성 = PlutoMacroSnapshot 단위).</summary>
    public sealed record PlutoHistoricalCycle(int Id, string Name, string Period, PlutoEpisodeKind Kind, PlutoRegimeStrategy Strategy, string Note,
        PlutoMacroSnapshot Raw);

    public static class PlutoRegimeVectorLake
    {
        public const int Dims = 6, MinDimensions = 2, Count = 12, MaxNorm = 100;
        /// <summary>거리 계수 σ(정규화 점 · 이 만큼 평균 차이 = exp(−1)).</summary>
        public const double Sigma = 40;
        /// <summary>차원별 정규화 중심(VIX×100 만 20pt 중심 · 나머지 = 0 변화).</summary>
        public const int VixCenterX100 = 2000;
        /// <summary>차원별 정규화 척도(이 만큼 = ±100 포화 경계).</summary>
        public const int VixScaleX100 = 4000, Us10yScaleBp = 100, DollarScaleBp = 600, UsdKrwScaleBp = 1500, TrendScaleBp = 3000, SentimentScale = 100;

        private static readonly long AsOf = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static readonly PlutoHistoricalCycle[] s_cycles =
        {
            C(1, "2000-02 닷컴 버블 붕괴", "2000.03–2002.10", PlutoEpisodeKind.Bear, PlutoRegimeStrategy.DevilsAdvocate,
                "기술주 밸류에이션 붕괴(나스닥 고점 대비 −78%) · 금리 인하에도 약세 지속 · 베어마켓 랠리 함정", 2600, -30, 50, 100, -1200, -45),
            C(2, "2008 서브프라임 금융위기", "2008.09–2009.03", PlutoEpisodeKind.Panic, PlutoRegimeStrategy.DevilsAdvocate,
                "VIX 80 돌파 · 신용 경색 · 안전자산 쏠림(달러 강세 · 원/달러 1,500원대) · 투매 연쇄", 6000, -80, 600, 1500, -2500, -80),
            C(3, "2011 미국 신용등급 강등", "2011.08–2011.10", PlutoEpisodeKind.Panic, PlutoRegimeStrategy.DevilsAdvocate,
                "S&P AAA 박탈 · 유럽 재정위기 동반 · VIX 48 · 국채 금리 급락(안전자산 역설)", 3500, -60, 100, 400, -1500, -60),
            C(4, "2015 중국 증시 쇼크", "2015.08–2016.02", PlutoEpisodeKind.Panic, PlutoRegimeStrategy.DevilsAdvocate,
                "위안화 기습 절하 · 8/24 블랙먼데이(VIX 순간 40) · 신흥국 자금 이탈 · 원화 약세", 2800, -15, -100, 300, -1000, -50),
            C(5, "2017 저변동 골디락스", "2017.01–2017.12", PlutoEpisodeKind.Bull, PlutoRegimeStrategy.TrendFollow,
                "VIX 10 하회 · 글로벌 동반 성장 · 약달러 · 저변동 꾸준한 추세 상승", 1100, 0, -150, -150, 350, 35),
            C(6, "2018 미중 무역분쟁", "2018.10–2018.12", PlutoEpisodeKind.Bear, PlutoRegimeStrategy.DevilsAdvocate,
                "관세 공방 격화 · 연준 긴축 우려 · 4분기 S&P −20% 근접 · 반등 실패 반복", 2500, -20, 100, 150, -1300, -35),
            C(7, "2019 연준 피벗 박스권", "2019.05–2019.10", PlutoEpisodeKind.Range, PlutoRegimeStrategy.MeanReversion,
                "보험성 금리 인하 · 무역 협상 헤드라인 등락 · 지수 박스권 · 돌파 실패 → 평균 회귀 우위", 1500, -20, 0, 50, 100, 0),
            C(8, "2020 코로나 팬데믹 폭락", "2020.02–2020.03", PlutoEpisodeKind.Panic, PlutoRegimeStrategy.DevilsAdvocate,
                "VIX 82.7 사상 최고 · 33거래일 −34% · 서킷브레이커 연속 · 달러 유동성 경색", 5700, -100, 300, 600, -3000, -75),
            C(9, "2020-21 V자 반등 유동성 장세", "2020.04–2021.12", PlutoEpisodeKind.Liquidity, PlutoRegimeStrategy.TrendFollow,
                "제로 금리 · 무제한 양적완화 · 개인 유동성 유입 · 약달러 · 추세 추종 우위", 2400, 10, -200, -300, 900, 45),
            C(10, "2022 연준 금리인상 충격", "2022.01–2022.10", PlutoEpisodeKind.Bear, PlutoRegimeStrategy.DevilsAdvocate,
                "자이언트 스텝 4연속(10년물 연 +240bp) · 달러 초강세 · 성장주 밸류에이션 압축", 2800, 60, 250, 450, -900, -45),
            C(11, "2023 SVB 뱅크런 충격", "2023.03", PlutoEpisodeKind.Bear, PlutoRegimeStrategy.DevilsAdvocate,
                "지역은행 뱅크런 · 국채 금리 급락 · 신용 스프레드 확대 · 금융주 중심 단기 충격", 2300, -60, -100, 150, -400, -40),
            C(12, "2024 AI 유동성 랠리", "2024.01–2024.12", PlutoEpisodeKind.Bull, PlutoRegimeStrategy.TrendFollow,
                "AI 반도체 주도 · 저변동 상승 · 실적 동반 모멘텀 · 추세 추종 우위", 1400, 5, 50, 100, 600, 50),
        };

        /// <summary>정규화 국면 벡터(국면 i 차원 d = [i × Dims + d]) · 제곱 노름.</summary>
        private static readonly int[] s_norm = BuildNorm(out s_normSq);
        private static readonly long[] s_normSq;

        private static PlutoHistoricalCycle C(int id, string name, string period, PlutoEpisodeKind kind, PlutoRegimeStrategy strategy, string note,
            int vix, int us10y, int dxy, int krw, int trend, int sentiment) =>
            new(id, name, period, kind, strategy, note, new PlutoMacroSnapshot(PlutoMacroField.All, vix, us10y, dxy, krw, trend, sentiment, AsOf));

        private static int[] BuildNorm(out long[] normSq)
        {
            var v = new int[Count * Dims];
            normSq = new long[Count];
            for (int i = 0; i < Count; i++)
            {
                var raw = s_cycles[i].Raw;
                for (int d = 0; d < Dims; d++)
                {
                    int z = Normalize(d, Field(in raw, d));
                    v[i * Dims + d] = z;
                    normSq[i] += (long)z * z;
                }
            }
            return v;
        }

        /// <summary>국면 1건(id 1~12 · 범위 밖 = null).</summary>
        public static PlutoHistoricalCycle? Get(int id) => id is >= 1 and <= Count ? s_cycles[id - 1] : null;

        /// <summary>국면 i(0 기반)의 정규화 차원 d 값(테스트 · 진단용).</summary>
        public static int NormalizedAt(int index, int dim) => s_norm[index * Dims + dim];

        /// <summary>차원 d(0 VIX · 1 금리 · 2 달러 · 3 원화 · 4 추세 · 5 감성) 원값 → −100~+100 정규화 점(δ=0).</summary>
        public static int Normalize(int dim, int raw)
        {
            int center = dim == 0 ? VixCenterX100 : 0;
            int scale = dim switch
            {
                0 => VixScaleX100,
                1 => Us10yScaleBp,
                2 => DollarScaleBp,
                3 => UsdKrwScaleBp,
                4 => TrendScaleBp,
                _ => SentimentScale,
            };
            double z = Math.Round(((long)raw - center) * 100.0 / scale, MidpointRounding.AwayFromZero);
            return (int)Math.Clamp(z, -MaxNorm, MaxNorm);
        }

        private static PlutoMacroField FieldBit(int dim) => (PlutoMacroField)(1 << dim);

        private static int Field(in PlutoMacroSnapshot s, int dim) => dim switch
        {
            0 => s.VixX100,
            1 => s.Us10yChangeBp,
            2 => s.DollarIndexChangeBp,
            3 => s.UsdKrwChangeBp,
            4 => s.EquityTrendBp,
            _ => s.SentimentIndex,
        };

        /// <summary>
        /// 관측 vs 임의 특성(에피소드) 유사도 0~1000(δ=0). 공통 차원 2개 미만 = −1. cosX1000 = 코사인 ×1000 · distX10 = RMS 정규화 거리 ×10.
        /// </summary>
        public static int SimilarityX10(in PlutoMacroSnapshot observed, in PlutoMacroSnapshot features, out int dims, out int cosX1000, out int distX10)
        {
            var common = observed.Fields & features.Fields;
            long dot = 0, oo = 0, ff = 0, dd = 0;
            dims = 0;
            for (int d = 0; d < Dims; d++)
            {
                if ((common & FieldBit(d)) == 0) continue;
                int o = Normalize(d, Field(in observed, d)), f = Normalize(d, Field(in features, d));
                dot += (long)o * f; oo += (long)o * o; ff += (long)f * f; dd += (long)(o - f) * (o - f);
                dims++;
            }
            return Combine(dims, dot, oo, ff, dd, out cosX1000, out distX10);
        }

        /// <summary>관측 vs 국면 index(0 기반) 유사도 0~1000(δ=0 · 국면 벡터 = 사전 정규화 고정 배열).</summary>
        public static int SimilarityX10(in PlutoMacroSnapshot observed, int index, out int dims, out int cosX1000, out int distX10)
        {
            long dot = 0, oo = 0, ff = 0, dd = 0;
            dims = 0;
            int baseIdx = index * Dims;
            for (int d = 0; d < Dims; d++)
            {
                if ((observed.Fields & FieldBit(d)) == 0) continue;
                int o = Normalize(d, Field(in observed, d)), f = s_norm[baseIdx + d];
                dot += (long)o * f; oo += (long)o * o; ff += (long)f * f; dd += (long)(o - f) * (o - f);
                dims++;
            }
            return Combine(dims, dot, oo, ff, dd, out cosX1000, out distX10);
        }

        private static int Combine(int dims, long dot, long oo, long ff, long dd, out int cosX1000, out int distX10)
        {
            cosX1000 = 0; distX10 = 0;
            if (dims < MinDimensions) return -1;
            double cos = oo == 0 && ff == 0 ? 1 : oo == 0 || ff == 0 ? 0 : Math.Clamp(dot / Math.Sqrt((double)oo * ff), -1, 1);
            double meanSq = (double)dd / dims;
            cosX1000 = (int)Math.Round(cos * 1000);
            distX10 = (int)Math.Round(Math.Sqrt(meanSq) * 10);
            return (int)Math.Round(1000 * ((1 + cos) / 2) * Math.Exp(-meanSq / (Sigma * Sigma)));
        }

        /// <summary>12대 국면 중 최고 유사도(δ=0). 공통 차원 부족 = default(Found false). 동률 = 앞선 국면.</summary>
        public static PlutoRegimeMatch FindNearest(in PlutoMacroSnapshot observed)
        {
            if (!observed.IsWellFormed) return default;
            int best = -1, bestSim = -1, bestDims = 0, bestCos = 0, bestDist = 0;
            for (int i = 0; i < Count; i++)
            {
                int sim = SimilarityX10(in observed, i, out int dims, out int cos, out int dist);
                if (sim > bestSim) { bestSim = sim; best = i; bestDims = dims; bestCos = cos; bestDist = dist; }
            }
            if (best < 0 || bestSim < 0) return default;
            var c = s_cycles[best];
            return new PlutoRegimeMatch(c.Id, bestSim, bestCos, bestDist, bestDims, c.Kind, c.Strategy, observed.AsOfUtcTicks);
        }
    }
}

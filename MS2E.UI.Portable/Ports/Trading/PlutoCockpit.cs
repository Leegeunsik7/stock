// ============================================================================
// PlutoCockpit — v12.18.87-DEV: DualArena 다크 슬레이트 콕핏(벤토 그리드) 순수 표시 모델(엔진 타입 참조 0 · 포트 DTO 만 입력)
//
//   PlutoWeightScale     = 듀얼-코어 시소 게이지 정규화 범위(엔진 PlutoCognitiveWeights 경계 미러 — 드리프트 = 테스트 실패).
//   PlutoScoreBreakdown  = 추천 점수 분해: 기본(4대 팩터 합) + 모멘텀 보정 − 악마의 변호인 감점 = 최종(0~100 포화 시 Saturated 표기 · 날조 0).
//   PlutoMatrixRow/Rows  = 추천 매트릭스 행(전체 = 3대 시장 통과 카드 점수 내림차순 통합 순위 · 시장 탭 = 엔진 순위 그대로) +
//                          피드 미판독 종목 = 시장별 집계 대기 행 1개(종목명 · 점수 생성 0 — CLAUDE.md §2 정직 렌더).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>학습 가중치 표시 범위(엔진 경계 미러 · 모멘텀 ±50% · 엄격도 −10~+20점).</summary>
    public static class PlutoWeightScale
    {
        public const int MaxMomentumTiltPct = 50, MinStrictnessPts = -10, MaxStrictnessPts = 20;

        /// <summary>모멘텀 → 중앙 기준 ±100 정규화(양수 = 공격 · 범위 밖 = 포화).</summary>
        public static int MomentumX100(int tiltPct) => Math.Clamp(tiltPct * 100 / MaxMomentumTiltPct, -100, 100);

        /// <summary>엄격도 → 중앙 기준 ±100 정규화(양수 = 방어 강화 · 음수 = 완화).</summary>
        public static int StrictnessX100(int pts) => pts >= 0
            ? Math.Clamp(pts * 100 / MaxStrictnessPts, 0, 100)
            : Math.Clamp(pts * 100 / -MinStrictnessPts, -100, 0);

        /// <summary>시소 기울기(−100 = 방어 우세 · 0 = 균형 · +100 = 공격 우세) = (공격 − 방어) / 2.</summary>
        public static int TiltX100(int tiltPct, int strictPts) => Math.Clamp((MomentumX100(tiltPct) - StrictnessX100(strictPts)) / 2, -100, 100);

        public static string TiltLabel(int tiltX100) => tiltX100 >= 15 ? "공격 우세(Bull)" : tiltX100 <= -15 ? "방어 우세(Devil)" : "균형";
    }

    /// <summary>추천 점수 분해(기본 = 4대 팩터 합 · Saturated = 기본 + 순 조정이 0~100 밖이라 최종이 포화됨).</summary>
    public readonly record struct PlutoScoreBreakdown(int Base, int Momentum, int Devil, int Final, bool Saturated)
    {
        public static PlutoScoreBreakdown Of(PlutoRecommendationCard c)
        {
            ArgumentNullException.ThrowIfNull(c);
            int b = c.ValuationPts + c.TechnicalPts + c.MicroPts + c.MacroPts;
            return new(b, c.LearnMomentumPts, c.LearnDevilPts, c.Score, b + c.LearnMomentumPts - c.LearnDevilPts != c.Score);
        }

        /// <summary>"기본 70 + 모멘텀 +12 − 변호인 4 = 88"(포화 = " (0~100 포화)" 덧붙임).</summary>
        public string Text => "기본 " + I(Base) + " + 모멘텀 " + (Momentum >= 0 ? "+" : "") + I(Momentum) + " − 변호인 " + I(Devil) + " = " + I(Final)
            + (Saturated ? " (0~100 포화)" : "");

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>매트릭스 행 1개 — Card 있음 = 추천 통과 종목(Rank = 표시 순위) · Card null = 시장 집계 대기 행(Awaiting 종목 수 · Note 사유).</summary>
    public sealed record PlutoMatrixRow(int Market, int Rank, PlutoRecommendationCard? Card, int Awaiting, string Note);

    public static class PlutoCockpitMatrix
    {
        /// <summary>전체 탭 값(3대 시장 통합).</summary>
        public const int All = -1;

        public static string AssetTag(int market) => market switch { PlutoRecoMarket.Us => "US", PlutoRecoMarket.Crypto => "Upbit", _ => "KRX" };

        /// <summary>탭 라벨 감시 종목 수(전체 = 3대 합).</summary>
        public static int Monitored(PlutoRecoBoardView b, int tab) => tab == All ? b.TotalMonitored : b.Of(tab).Monitored;

        /// <summary>
        /// 행 합성: 미결선 · 첫 스캔 전 = 빈 목록(페이지가 안내 문구 렌더). 카드 = 전체 탭 점수 내림차순 통합 순위(동점 = 시장 · 엔진 순위 순) / 시장 탭 = 엔진 순위.
        /// 대기 행 = 시장별 1개(피드 미판독 감시 종목 수 > 0 · 스웜 레인 미결선 = '실피드 연결 대기').
        /// </summary>
        public static IReadOnlyList<PlutoMatrixRow> Rows(PlutoRecoBoardView b, bool wired, int tab)
        {
            ArgumentNullException.ThrowIfNull(b);
            if (!wired || b.Generation == 0) return Array.Empty<PlutoMatrixRow>();
            var markets = tab == All ? new[] { PlutoRecoMarket.Krx, PlutoRecoMarket.Us, PlutoRecoMarket.Crypto } : new[] { Norm(tab) };
            var cards = new List<PlutoRecommendationCard>();
            foreach (int m in markets) cards.AddRange(b.Of(m).Cards);
            if (tab == All) cards.Sort(static (x, y) => y.Score != x.Score ? y.Score.CompareTo(x.Score) : x.Market != y.Market ? x.Market.CompareTo(y.Market) : x.Rank.CompareTo(y.Rank));
            var rows = new List<PlutoMatrixRow>(cards.Count + markets.Length);
            for (int i = 0; i < cards.Count; i++) rows.Add(new PlutoMatrixRow(cards[i].Market, tab == All ? i + 1 : cards[i].Rank, cards[i], 0, ""));
            foreach (int m in markets)
            {
                var v = b.Of(m);
                var lane = b.Swarm.Wired ? b.Swarm.Of(m) : null;
                if (v.AwaitingFeed > 0)
                    rows.Add(new PlutoMatrixRow(m, 0, null, v.AwaitingFeed, lane is { Connected: false }
                        ? "실피드 연결 대기 — 소스 미결선(점수 미산출)" : "시세 피드 판독 대기(점수 미산출)"));
            }
            return rows;
        }

        private static int Norm(int m) => m is PlutoRecoMarket.Us or PlutoRecoMarket.Crypto ? m : PlutoRecoMarket.Krx;
    }
}

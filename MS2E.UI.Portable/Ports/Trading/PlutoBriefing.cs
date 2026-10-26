// ============================================================================
// PlutoBriefing — v12.18.85-DEV: 플루토 인지 학습 브리핑(순수 DTO 합성 · 엔진 타입 참조 0 · UI 모달 · 호스트 CLI --pluto-briefing 공용)
//
//   입력 = IPlutoLearningPort 학습 상태(PlutoLearningView) + IPlutoRecommendationPort 추천 보드(PlutoRecoBoardView) — 두 포트 결선 여부 그대로.
//   브리핑 = ① 12대 국면 매칭(국면명 · 유사도 % · 해설 · 판정 전 = 정직 표기) ② 누적 학습 에피소드(영속 평생 누적 · 미결선 = 이번 기동 표본)
//            ③ 최적 가중치(학습 세대 · 모멘텀 조정 % · 악마의 변호인 엄격도 점) ④ 섀도우 성과(직전 판정 · 현행 대비 승률/MDD 개선치 · 현행 섀도우 승률)
//            ⑤ 자산군별(KRX · US · Upbit) 적응 추천 Top N(점수 · 학습 투영 순 조정 · 핵심 근거 1줄 · 리스크 요인).
//   ★정직 렌더: 미결선 포트 · 첫 스캔 전 · 통과 종목 0 = 안내 문구만(가짜 국면 · 가짜 점수 · 가짜 종목 0).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>브리핑 추천 1건(카드 요약).</summary>
    public sealed record PlutoBriefingPick(int Market, int Rank, string Name, string Code, int Score, int LearnNetPts, string Reason, string Risk);

    /// <summary>자산군 1개 브리핑(Status = 상태 문구 · Picks = 적응 추천 Top N · 빈 목록 = 추천 없음).</summary>
    public sealed record PlutoBriefingMarket(int Market, string Status, IReadOnlyList<PlutoBriefingPick> Picks);

    /// <summary>인지 학습 브리핑 1건(불변).</summary>
    public sealed record PlutoBriefingReport(
        bool LearningWired, bool RecoWired, string LearningState, string RegimeLine, string EpisodesLine, string WeightsLine, string ShadowLine,
        long Generation, int MomentumTiltPct, int DevilStrictnessPts, long Episodes, int RegimeSimilarityX10, PlutoShadowView Shadow,
        IReadOnlyList<PlutoBriefingMarket> Markets)
    {
        public PlutoBriefingMarket Of(int market) => Markets[market is PlutoRecoMarket.Us or PlutoRecoMarket.Crypto ? market : PlutoRecoMarket.Krx];

        /// <summary>추천 종목 합계(3대 자산).</summary>
        public int TotalPicks
        {
            get { int n = 0; foreach (var m in Markets) n += m.Picks.Count; return n; }
        }

        /// <summary>텍스트 브리핑(CLI · 로그 · 클립보드 공용 · 줄 단위).</summary>
        public IReadOnlyList<string> Lines()
        {
            var l = new List<string>(24)
            {
                "📊 플루토 인지 학습 브리핑",
                "  학습 상태 : " + LearningState,
                "  국면 매칭 : " + RegimeLine,
                "  누적 학습 : " + EpisodesLine,
                "  최적 가중 : " + WeightsLine,
                "  섀도우 검증: " + ShadowLine,
                "🌟 레짐 적응형 추천(학습 가중치 투영)",
            };
            foreach (var m in Markets)
            {
                l.Add("  [" + PlutoBriefingComposer.MarketTag(m.Market) + "] " + m.Status);
                foreach (var p in m.Picks)
                    l.Add("    #" + p.Rank.ToString(CultureInfo.InvariantCulture) + " " + p.Name + "(" + p.Code + ") " + p.Score.ToString(CultureInfo.InvariantCulture)
                        + "점 (학습 " + PlutoBriefingComposer.Signed(p.LearnNetPts) + ") · 근거: " + p.Reason + " · 리스크: " + p.Risk);
            }
            l.Add("※ 가상 원장 전용 브리핑 — 증권사로 나가는 주문은 없습니다(liveArmed:false).");
            return l;
        }
    }

    public static class PlutoBriefingComposer
    {
        public const int DefaultTopN = 3, MaxTopN = 5;

        public static string MarketTag(int market) => market switch { PlutoRecoMarket.Us => "US", PlutoRecoMarket.Crypto => "Upbit", _ => "KRX" };

        public static string Signed(int v) => (v >= 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture);

        /// <summary>bp → "12.34%".</summary>
        public static string Pct(int bp) => (bp / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        /// <summary>bp 증분 → "+1.20%p".</summary>
        public static string PctPoint(int bp) => (bp >= 0 ? "+" : "") + (bp / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%p";

        /// <summary>두 포트 판독 → 브리핑 합성(콜드 · 순수 함수). topN = 자산군별 표시 수(1~5 · 범위 밖 = 클램프).</summary>
        public static PlutoBriefingReport Compose(bool learningWired, in PlutoLearningView lv, bool recoWired, PlutoRecoBoardView board, int topN = DefaultTopN)
        {
            ArgumentNullException.ThrowIfNull(board);
            int n = Math.Clamp(topN, 1, MaxTopN);
            string state = !learningWired ? "학습 엔진 미결선" : lv.Running ? PlutoLearningHealth.Label(lv.Health) : "학습 엔진 정지";
            string regime = RegimeLine(learningWired, in lv);
            long episodes = !learningWired ? 0 : lv.LifetimeEpisodes >= 0 ? lv.LifetimeEpisodes : lv.Samples;
            string episodesLine = !learningWired ? "학습 엔진 미결선"
                : lv.LifetimeEpisodes >= 0
                    ? episodes.ToString("N0", CultureInfo.InvariantCulture) + "회(재기동 합산 평생 누적 · " + PlutoLearningPersist.Label(lv.PersistState) + ")"
                    : episodes.ToString("N0", CultureInfo.InvariantCulture) + "회(이번 기동 청산 회고 표본 · 영속화 미결선)";
            string weights = !learningWired ? "학습 엔진 미결선"
                : lv.Generation == 0 && lv.MomentumTiltPct == 0 && lv.DevilStrictnessPts == 0
                    ? "학습 전 중립(세대 #0 · 모멘텀 +0% · 엄격도 +0점)"
                    : "세대 #" + lv.Generation.ToString(CultureInfo.InvariantCulture) + " · 모멘텀 조정 " + Signed(lv.MomentumTiltPct) + "% · 악마의 변호인 엄격도 "
                      + Signed(lv.DevilStrictnessPts) + "점 · 핫스왑 " + lv.Swaps.ToString(CultureInfo.InvariantCulture) + "회 / 거부 " + lv.RejectedSwaps.ToString(CultureInfo.InvariantCulture) + "회";
            var markets = new PlutoBriefingMarket[]
            {
                Market(PlutoRecoMarket.Krx, recoWired, board, n),
                Market(PlutoRecoMarket.Us, recoWired, board, n),
                Market(PlutoRecoMarket.Crypto, recoWired, board, n),
            };
            return new PlutoBriefingReport(learningWired, recoWired, state, regime, episodesLine, weights, ShadowLine(learningWired, lv.Shadow),
                learningWired ? lv.Generation : 0, learningWired ? lv.MomentumTiltPct : 0, learningWired ? lv.DevilStrictnessPts : 0, episodes,
                learningWired && lv.HasRegime ? lv.RegimeSimilarityX10 : -1, learningWired ? lv.Shadow : default, Array.AsReadOnly(markets));
        }

        private static string RegimeLine(bool wired, in PlutoLearningView lv)
        {
            if (!wired) return "학습 엔진 미결선";
            if (lv.HasRegime)
                return lv.RegimeName + " · 유사도 " + (lv.RegimeSimilarityX10 / 10).ToString(CultureInfo.InvariantCulture) + "." + (lv.RegimeSimilarityX10 % 10).ToString(CultureInfo.InvariantCulture)
                    + "%" + (lv.RegimeEffect is null ? "" : " · " + lv.RegimeEffect);
            if (lv.RegimeName is not null) return lv.RegimeName + "(직전 기록 국면 · 유사도는 실시간 거시 관측 후 판정)";
            return "판정 전(12대 역사적 국면 · 거시 관측 2차원 이상 수집 대기)";
        }

        public static string ShadowLine(bool wired, in PlutoShadowView s)
        {
            if (!wired) return "학습 엔진 미결선";
            string live = s.LiveSamples > 0
                ? "현행 가중치 섀도우 승률 " + Pct(s.LiveWinRateBp) + " · MDD " + Pct(s.LiveMddBp) + " · 표본 " + s.LiveSamples.ToString(CultureInfo.InvariantCulture)
                : "현행 가중치 섀도우 기록 없음(학습 전 중립)";
            if (!s.HasComparison) return "직전 판정: " + PlutoShadowVerdictCode.Label(s.Verdict) + " · " + live;
            return "직전 판정: " + PlutoShadowVerdictCode.Label(s.Verdict) + " · 승률 " + Pct(s.BaselineWinRateBp) + " → " + Pct(s.CandidateWinRateBp)
                + " (" + PctPoint(s.WinRateGainBp) + ") · MDD " + Pct(s.BaselineMddBp) + " → " + Pct(s.CandidateMddBp) + " (개선 " + PctPoint(s.MddGainBp) + ") · 홀드아웃 "
                + s.Holdout.ToString(CultureInfo.InvariantCulture) + " · " + live;
        }

        private static PlutoBriefingMarket Market(int market, bool wired, PlutoRecoBoardView board, int n)
        {
            if (!wired) return new PlutoBriefingMarket(market, "추천 엔진 미결선 — 추천 없음", Array.Empty<PlutoBriefingPick>());
            if (board.Generation == 0) return new PlutoBriefingMarket(market, "첫 스캔 대기 — 추천 없음", Array.Empty<PlutoBriefingPick>());
            var m = board.Of(market);
            string cover = "감시 " + m.Monitored.ToString(CultureInfo.InvariantCulture) + " · 피드 판독 " + m.Scanned.ToString(CultureInfo.InvariantCulture)
                + " · 판독 대기 " + m.AwaitingFeed.ToString(CultureInfo.InvariantCulture) + " · 통과 " + m.Passed.ToString(CultureInfo.InvariantCulture);
            if (m.Cards.Count == 0)
                return new PlutoBriefingMarket(market, cover + " — 추천 기준 통과 종목 없음(근거 없는 추천 0)", Array.Empty<PlutoBriefingPick>());
            int take = Math.Min(n, m.Cards.Count);
            var picks = new PlutoBriefingPick[take];
            for (int i = 0; i < take; i++)
            {
                var c = m.Cards[i];
                picks[i] = new PlutoBriefingPick(c.Market, c.Rank, c.Name, c.Code, c.Score, c.LearnNetPts,
                    c.Reasons.Count > 0 ? c.Reasons[0] : "근거 문구 없음", c.CounterText);
            }
            return new PlutoBriefingMarket(market, cover, Array.AsReadOnly(picks));
        }
    }
}

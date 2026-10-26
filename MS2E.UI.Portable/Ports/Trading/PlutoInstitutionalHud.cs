// ============================================================================
// PlutoInstitutionalHud — v12.18.90-DEV: DualArena 기관용 인지 콕핏 표시 모델(순수 함수 · 엔진 타입 참조 0 · 포트 DTO 만 입력)
//
//   PlutoHudInsight      = 라이브 브리핑 2번째 줄(① 420종 전수 분석 현황 ② 12대 국면 연상 + 태세 ③ 리스크 비토 차단 실적) · 태세 라벨/요약 · 웜스타트 집계.
//   PlutoCouncilSummary  = 4대 위원회 발언권(3대 자산 레인 평균 bp · 합 10000 근사) + 주도 에이전트 — 게시 전/미결선 = Has false(가짜 가중치 0).
//   PlutoVetoMatrix      = [🛡️ 리스크 비토 차단 목록] 행(전체 = 3대 시장 · 시장 탭 = 해당 시장만) — 엔진이 실제 차단한 시세 판독 종목만(날조 0 · CLAUDE.md §2).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    public static class PlutoHudInsight
    {
        /// <summary>태세 아이콘 라벨(기울기 −100 방어 ↔ +100 공격 · PlutoWeightScale.TiltLabel 과 같은 ±15 경계).</summary>
        public static string Stance(int tiltX100) => tiltX100 >= 15 ? "⚔️ 공격 태세" : tiltX100 <= -15 ? "🛡️ 방어 태세" : "⚖️ 균형 태세";

        public static string StanceCss(int tiltX100) => tiltX100 >= 15 ? "atk" : tiltX100 <= -15 ? "def" : "bal";

        /// <summary>태세 한 줄 요약(사용자 문구).</summary>
        public static string StanceNote(int tiltX100) => tiltX100 >= 15
            ? "추세가 강한 종목에 가점 · 모멘텀 추종"
            : tiltX100 <= -15 ? "진입 문턱 강화 · 반대 논리 엄격 심사" : "추세와 위험을 같은 비중으로 심사";

        /// <summary>
        /// 브리핑 2번째 줄. 예: "📊 3대 시장 420종 전수 분석: 시세 판독 397종 중 6종이 추천 기준을 통과했습니다. 🧭 지금 시장은 과거 '2022 긴축 충격' 국면과 94.2% 닮았습니다 — 🛡️ 방어 태세로 대응합니다.
        /// 🛡️ 리스크 통제관 거부권 발동: VIX 38.2 (35 초과) — 패닉 투매 위험 차단 → 위험 종목 31종 진입 차단."
        /// </summary>
        public static string Compose(bool learningWired, in PlutoLearningView lv, bool recoWired, PlutoRecoBoardView board)
        {
            ArgumentNullException.ThrowIfNull(board);
            string analysis = !recoWired
                ? "📊 추천 엔진 미결선 — 종목 분석을 수행하지 않는 호스트입니다."
                : board.Generation == 0
                ? "📊 3대 시장 " + I(board.TotalMonitored) + "종 첫 전수 분석을 기다리고 있습니다."
                : "📊 3대 시장 " + I(board.TotalMonitored) + "종 전수 분석: 시세 판독 " + I(board.TotalScanned) + "종 중 " + I(board.TotalPassed) + "종이 추천 기준을 통과했습니다.";

            string regime = "";
            if (learningWired)
            {
                int tilt = PlutoWeightScale.TiltX100(lv.MomentumTiltPct, lv.DevilStrictnessPts);
                regime = lv.HasRegime
                    ? " 🧭 지금 시장은 과거 '" + lv.RegimeName + "' 국면과 " + Sim(lv.RegimeSimilarityX10) + " 닮았습니다 — " + Stance(tilt) + "로 대응합니다."
                    : " 🧭 12대 역사적 국면 판정 대기 — 거시 관측을 수집하고 있습니다.";
            }

            string veto = "";
            if (recoWired && board.Generation > 0)
            {
                int blocked = board.TotalBlocked;
                string? croReason = FirstCouncilReason(board.Vetoes);
                veto = board.Council.Vetoed || croReason is not null
                    ? " 🛡️ 리스크 통제관 거부권 발동" + (croReason is null ? "" : ": " + croReason) + " → 위험 종목 " + I(blocked) + "종 진입 차단."
                    : blocked > 0
                    ? " 🛡️ 위험 신호가 겹친 " + I(blocked) + "종을 선제 차단해 자본을 보존했습니다."
                    : " 🛡️ 직전 분석에서 차단할 고위험 종목은 없었습니다.";
            }
            return analysis + regime + veto;
        }

        /// <summary>웜스타트 집계(텐서 null = total 0 → "웜스타트 미결선").</summary>
        public static string WarmText(PlutoTensorView? t, out int ready, out int total)
        {
            ready = 0;
            total = t?.Lanes.Count ?? 0;
            if (t is null || total == 0) return "웜스타트 미결선";
            foreach (var l in t.Lanes) if (PlutoWarmCode.Normalize(l.Warm) == PlutoWarmCode.Ready) ready++;
            return ready == total
                ? "✅ 웜스타트 완료 (" + I(ready) + "/" + I(total) + ")"
                : "웜스타트 " + I(ready) + "/" + I(total) + " 완료";
        }

        /// <summary>"+4.2%p" · "−1.0%p" · "0.0%p".</summary>
        public static string PctP(int bp) => (bp > 0 ? "+" : bp < 0 ? "−" : "") + (Math.Abs(bp) / 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%p";

        private static string? FirstCouncilReason(IReadOnlyList<PlutoVetoCard> vetoes)
        {
            foreach (var v in vetoes)
                if (v.Source == PlutoVetoSourceCode.Council && v.Reason.Length > 0) return v.Reason;
            return null;
        }

        private static string Sim(int x10) => I(x10 / 10) + "." + I(x10 % 10) + "%";

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>4대 위원회 발언권(3대 자산 레인 평균 bp) + 주도 에이전트. Has false = 미결선 · 첫 게시 전 · 유효 레인 0(표시 = 정직 문구).</summary>
    public readonly record struct PlutoCouncilSummary(bool Has, int TrendBp, int ReversionBp, int LiquidityBp, int RiskBp)
    {
        public static PlutoCouncilSummary Of(PlutoCouncilView v)
        {
            ArgumentNullException.ThrowIfNull(v);
            if (!v.Wired || !v.Published) return default;
            int n = 0, t = 0, r = 0, l = 0, c = 0;
            foreach (var lane in v.Lanes)
            {
                if (lane.Sum <= 0) continue;
                n++; t += lane.TrendBp; r += lane.ReversionBp; l += lane.LiquidityBp; c += lane.RiskBp;
            }
            return n == 0 ? default : new PlutoCouncilSummary(true, t / n, r / n, l / n, c / n);
        }

        public int WeightOf(int agent) => agent switch
        {
            PlutoCouncilAgentCode.Trend => TrendBp,
            PlutoCouncilAgentCode.Reversion => ReversionBp,
            PlutoCouncilAgentCode.Liquidity => LiquidityBp,
            _ => RiskBp,
        };

        /// <summary>최대 발언권 에이전트(동률 = 코드 순서 앞).</summary>
        public int Lead
        {
            get
            {
                int lead = 0;
                for (int a = 1; a < PlutoCouncilAgentCode.Count; a++) if (WeightOf(a) > WeightOf(lead)) lead = a;
                return lead;
            }
        }
    }

    public static class PlutoVetoMatrix
    {
        /// <summary>탭 필터(전체 = 엔진 순서 그대로 KRX → US → 코인 · 미결선/첫 스캔 전 = 빈 목록).</summary>
        public static IReadOnlyList<PlutoVetoCard> Rows(PlutoRecoBoardView b, bool wired, int tab)
        {
            ArgumentNullException.ThrowIfNull(b);
            if (!wired || b.Generation == 0) return Array.Empty<PlutoVetoCard>();
            if (tab == PlutoCockpitMatrix.All) return b.Vetoes;
            var rows = new List<PlutoVetoCard>();
            foreach (var v in b.Vetoes) if (v.Market == tab) rows.Add(v);
            return rows;
        }

        /// <summary>탭 차단 전수 집계(전체 = 3대 합 · 목록 상한과 무관).</summary>
        public static int Blocked(PlutoRecoBoardView b, int tab) => tab == PlutoCockpitMatrix.All ? b.TotalBlocked : b.Of(tab).BlockedTotal;
    }
}

// ============================================================================
// PlutoCoLearning — v12.18.93-DEV: 플루토 퀀트 코-러닝 허브 UI 경계(순수 문장 합성 · 순수 계산 · 엔진 타입 참조 0)
//
//   ① PlutoThoughtComposer  = AI 근거 카드(이미 게시된 DTO) → 1인칭 사고 독백 "[관찰] → [논쟁] → [결정]" — 렌더 콜드 경로(체결 핫패스 무관 · 근거 카드에 없는 수치 날조 0).
//   ② PlutoPostMortemComposer = 손실 청산 · 리스크 비토 → 오답 노트(왜 틀렸나 · 어떤 교훈 압력으로 텐서 가중치가 보정되는가 · 현재 세대/진행도 인용 — '보정 완료' 위장 0).
//   ③ PlutoConceptGuide     = CVD · 켈리 비중 · 볼린저 밴드 · 거시 레짐 원포인트 과외 카드(정적 교육 문구).
//   ④ PlutoOrderCoach       = 휴먼 주문 접수 직후 스프레드 · 진입 비중(과다 베팅) · 리스크 상태 원포인트 조언(가상 원장 · 접수 이후 표시만 — 주문 변경 · 차단 0).
//   ★판독/표시 전용 — 주문 · 무장 · 브로커 경로 0. 상승/공격 = 적색 · 하강/방어 = 청색(CSS 는 컴포넌트).
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>사고 독백 1줄(Side +1 매수 · −1 매도).</summary>
    public sealed record PlutoThoughtLine(long Seq, DateTime AtKst, string Name, int Side, string Observe, string Debate, string Decide)
    {
        public string Text => "[관찰] " + Observe + " → [논쟁] " + Debate + " → [결정] " + Decide;
    }

    public static class PlutoThoughtComposer
    {
        public const int MaxLines = 6;

        public static PlutoThoughtLine Of(KrxRationaleView r, string name) => Compose(r.Seq, r.AtKst, name, r.Side, r.Trigger, r.SentimentScore, r.ConfidenceScore,
            r.RiskRewardRatio, r.PriceWon, r.TargetPriceWon, r.StopLossPriceWon, r.RealizedPnlWon, r.Governance, null);

        public static PlutoThoughtLine Of(UsRationaleView r, string name) => Compose(r.Seq, r.AtKst, name, r.Side, r.Trigger, r.SentimentScore, r.ConfidenceScore,
            r.RiskRewardRatio, r.PriceCents, r.TargetPriceCents, r.StopLossPriceCents, r.RealizedPnlCents, r.Governance, null);

        public static PlutoThoughtLine Of(CryptoRationaleView r, string name) => Compose(r.Seq, r.AtKst, name, r.Side, r.Trigger, r.SentimentScore, r.ConfidenceScore,
            r.RiskRewardRatio, r.PriceRaw, r.TargetPriceRaw, r.StopLossPriceRaw, r.RealizedPnlRaw, r.Governance, r.ImbalanceBp);

        /// <summary>
        /// 근거 1건 → 독백. imbalanceBp(코인 호가 불균형 · null = 미산출 → 미표기) · 목표/손절 0 = 미표기 · 거버넌스 null = 논쟁 '판독 없음'.
        /// </summary>
        public static PlutoThoughtLine Compose(long seq, DateTime at, string name, int side, string trigger, int sentiment, int confidence, double riskReward,
            long price, long target, long stop, long realizedPnl, ArenaGovernanceCard? gov, int? imbalanceBp)
        {
            name = string.IsNullOrWhiteSpace(name) ? "종목" : name;
            string observe = (string.IsNullOrWhiteSpace(trigger) ? "시그널 감지" : trigger.Trim()) + " · 감성 " + Mood(sentiment);
            if (imbalanceBp is { } imb && imb != 0)
                observe += " · " + (imb > 0 ? "호가 매수벽 우위 " : "호가 매도벽 우위 ") + Pct(Math.Abs(imb));
            if (gov is { IsExit: false } g0 && ArenaRegimeCode.Normalize(g0.Regime) != ArenaRegimeCode.Unknown) observe += " · " + ArenaRegimeCode.Badge(g0.Regime);

            string debate, decide;
            if (side > 0)
            {
                if (gov is { IsExit: false } g)
                {
                    debate = "추세관 진입 찬성(원확신도 " + g.RawConfidence.ToString(CultureInfo.InvariantCulture) + ") vs "
                        + (g.Penalties.Count == 0 ? "리스크관 이견 없음" : "리스크관 경고(" + Penalties(g.Penalties) + ")");
                    if (g.StreakCut) debate += " · 연패 " + g.LossStreak.ToString(CultureInfo.InvariantCulture) + "회 → 비중 절반";
                }
                else debate = "추세관 진입 찬성(확신도 " + confidence.ToString(CultureInfo.InvariantCulture) + ") vs 리스크관 — 거버넌스 판독 없음";
                decide = (gov is { IsExit: false } g2 ? "최종 확신도 " + g2.Confidence.ToString(CultureInfo.InvariantCulture) + " · 동적 비중 " + (g2.WeightBp / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "% 진입"
                        : "확신도 " + confidence.ToString(CultureInfo.InvariantCulture) + " 진입");
                string plan = Plan(price, target, stop, riskReward);
                if (plan.Length > 0) decide += " · " + plan;
            }
            else
            {
                debate = "보유 논리 vs 청산 논리 — " + (gov is { IsExit: true } ge ? ge.PostMortem! : "청산 근거: " + (string.IsNullOrWhiteSpace(trigger) ? "시그널" : trigger.Trim()));
                decide = realizedPnl > 0 ? "청산 — 이익 확정(익절)" : realizedPnl < 0 ? "청산 — 손실 확정(오답 노트 기록)" : "청산 — 본전";
            }
            return new PlutoThoughtLine(seq, at, name, side, observe, debate, decide);
        }

        private static string Mood(int s) => s >= 30 ? "낙관 " + Signed(s) : s <= -30 ? "비관 " + Signed(s) : "중립 " + Signed(s);

        private static string Penalties(IReadOnlyList<ArenaPenaltyTag> p)
        {
            var parts = new string[p.Count];
            for (int i = 0; i < p.Count; i++) parts[i] = p[i].Label + " " + p[i].Points.ToString(CultureInfo.InvariantCulture);
            return string.Join(" · ", parts);
        }

        /// <summary>"목표 +3.0% · 손절 −2.0% (손익비 1.50)" · 가격 0 = "".</summary>
        public static string Plan(long price, long target, long stop, double rr)
        {
            if (price <= 0 || target <= 0 || stop <= 0) return "";
            long up = (long)((Int128)(target - price) * 10000 / price), down = (long)((Int128)(stop - price) * 10000 / price);
            return "목표 " + Pct(up, true) + " · 손절 " + Pct(down, true) + (double.IsFinite(rr) && rr > 0 ? " (손익비 " + rr.ToString("0.00", CultureInfo.InvariantCulture) + ")" : "");
        }

        private static string Signed(int v) => (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture);

        private static string Pct(long bp, bool signed = false) => (signed && bp > 0 ? "+" : "") + (bp / 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>오답 노트 유형 코드 — 1 손절 · 2 익절 실패(손실 마감) · 3 리스크 비토 차단.</summary>
    public static class PlutoPostMortemKind
    {
        public const int StopLoss = 1, MissedProfit = 2, Veto = 3;

        public static string Label(int k) => k switch
        {
            StopLoss => "손절",
            MissedProfit => "익절 실패",
            Veto => "비토 차단",
            _ => "회고",
        };

        public static string Css(int k) => k switch { StopLoss => "sl", MissedProfit => "miss", Veto => "veto", _ => "etc" };
    }

    /// <summary>오답 노트 1장(WhyWrong = 왜 틀렸나 · Calibration = 텐서 가중치 보정 경로 · Lesson = 인간 투자자 교훈).</summary>
    public sealed record PlutoPostMortemNote(long Seq, DateTime AtKst, string Name, int Kind, string Headline, string WhyWrong, string Calibration, string Lesson);

    public static class PlutoPostMortemComposer
    {
        public const int MaxNotes = 4;

        /// <summary>손실 청산 → 오답 노트(이익 · 본전 = null).</summary>
        public static PlutoPostMortemNote? FromClosed(ArenaClosedTradeView t, bool learningWired, in PlutoLearningView lv)
        {
            ArgumentNullException.ThrowIfNull(t);
            bool loss = t.RealizedPnl < 0 || (t.HasReturn && t.ReturnBp < 0);
            if (!loss) return null;
            int kind = t.ExitKind == ArenaExitKind.StopLoss ? PlutoPostMortemKind.StopLoss : PlutoPostMortemKind.MissedProfit;
            string headline = PlutoPostMortemKind.Label(kind) + " · " + t.Name + " " + (t.HasReturn ? Bp(t.ReturnBp) : "손실") + " · " + t.ExitLabel;
            (string why, string pressure, string lesson) = t.ExitKind switch
            {
                ArenaExitKind.StopLoss => ("진입 근거가 손절선 도달 전에 무효화됐습니다 — 추세 확인보다 진입이 앞섰거나, 변동성 대비 손절폭이 좁았습니다.",
                    "반대 논리 무시 · 외부 충격 패착 → 악마의 변호인 엄격도 압력 +1(약한 확신 진입을 더 많이 거부하는 방향)",
                    "손절은 실패가 아니라 비용입니다 — 손절선을 미리 정하고 지켰기 때문에 손실이 이 크기에서 멈췄습니다."),
                ArenaExitKind.NewsShock => ("악재 뉴스(외부 충격)로 긴급 청산했습니다 — 가격 신호로는 미리 알 수 없던 사건입니다.",
                    "외부 충격 패착 → 엄격도 압력 +1(충격 국면 비중 축소 방향)",
                    "예측 불가 사건은 맞히는 것이 아니라 비중으로 대비합니다 — 한 종목에 몰지 마세요."),
                ArenaExitKind.SignalReversal => ("보유 중 진입 근거(모멘텀 · 수급)가 반전됐습니다 — 신호가 생각보다 빨리 식었습니다.",
                    "시그널 약화 · 국면 전환 패착 → 모멘텀 tilt 압력 −1(추세 추격 가중 축소 방향)",
                    "근거가 사라지면 이익이 아니어도 나옵니다 — '본전 오면 팔자'는 근거가 아닙니다."),
                ArenaExitKind.SessionClose => ("장마감 시간 제한으로 목표 도달 전에 청산했습니다 — 보유 가능 시간 대비 목표가 컸습니다.",
                    "시그널 약화 패착 → 모멘텀 tilt 압력 −1(목표를 짧게 잡는 방향)",
                    "시간도 리스크입니다 — 보유 기간에 맞는 목표를 잡으세요."),
                ArenaExitKind.ValuationBubble => ("과열(밸류에이션 버블) 판정으로 조기 청산했습니다 — 진입 시점이 이미 비쌌습니다.",
                    "반대 논리 무시 패착 → 엄격도 압력 +1(과열 감점 강화 방향)",
                    "좋은 회사도 비싸게 사면 나쁜 투자가 됩니다."),
                _ => ("목표에 닿지 못하고 손실로 마감했습니다.",
                    "패착 교훈 → 다음 보정 심사의 학습 표본",
                    "진입 전에 목표 · 손절 · 비중 세 가지를 먼저 정하세요."),
            };
            string calib = learningWired
                ? pressure + " · 이 회고는 보정기 표본으로 편입되어 " + lv.Evolution.Text + " — 섀도우 검증 통과 시에만 다음 세대로 채택(세대당 엄격도 ±2점 · 모멘텀 ±5% 상한) · 현재 G"
                  + lv.Generation.ToString(CultureInfo.InvariantCulture) + " 모멘텀 " + Signed(lv.MomentumTiltPct) + "% · 엄격도 " + Signed(lv.DevilStrictnessPts) + "점"
                : "학습 엔진 미결선 — 이 회고는 텐서 보정에 반영되지 않습니다";
            return new PlutoPostMortemNote(t.Seq, t.AtKst, t.Name, kind, headline, why + (string.IsNullOrWhiteSpace(t.Reason) ? "" : " (엔진 사유: " + t.Reason + ")"), calib, lesson);
        }

        /// <summary>리스크 통제관 비토 → 오답 노트(체결 0 → 보정 표본 0 · 정직 표기).</summary>
        public static PlutoPostMortemNote FromVeto(ArenaCouncilVetoView v, string name)
        {
            ArgumentNullException.ThrowIfNull(v);
            return new PlutoPostMortemNote(v.Seq, v.AtKst, string.IsNullOrWhiteSpace(name) ? v.Symbol : name, PlutoPostMortemKind.Veto,
                "비토 차단 · " + (string.IsNullOrWhiteSpace(name) ? v.Symbol : name) + " 원점수 " + Signed(v.Score),
                "진입 점수는 기준을 넘었지만 리스크 통제관이 거부했습니다 — " + v.Reason,
                "비토는 체결이 없어 보정 표본 0 — 차단의 옳고 그름은 이후 가격 경로로만 사후 확인됩니다(이 노트는 기록용 · 가중치 변경 없음)",
                "점수가 높아도 '잃지 않을 이유'가 없으면 들어가지 않습니다 — 기회를 놓치는 비용은 손실보다 작습니다.");
        }

        /// <summary>청산 저널 + 비토 피드 → 최신순 노트(최대 MaxNotes · 손실 청산 우선 → 비토).</summary>
        public static IReadOnlyList<PlutoPostMortemNote> Collect(IReadOnlyList<ArenaClosedTradeView> closed, ArenaCouncilVetoFeed? vetoes, Func<string, string> name,
            bool learningWired, in PlutoLearningView lv)
        {
            ArgumentNullException.ThrowIfNull(closed);
            ArgumentNullException.ThrowIfNull(name);
            var list = new List<PlutoPostMortemNote>(MaxNotes);
            foreach (var t in closed)
            {
                if (list.Count >= MaxNotes) break;
                if (FromClosed(t, learningWired, in lv) is { } n) list.Add(n);
            }
            if (vetoes is { Attached: true })
                foreach (var v in vetoes.Recent)
                {
                    if (list.Count >= MaxNotes) break;
                    list.Add(FromVeto(v, name(v.Symbol)));
                }
            return list;
        }

        private static string Bp(long bp) => (bp > 0 ? "+" : "") + (bp / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        private static string Signed(long v) => (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>개념 과외 카드 1장.</summary>
    public sealed record PlutoConcept(string Key, string Icon, string Title, string Formula, string WhyPluto, string HumanLesson, string Pitfall);

    /// <summary>퀀트 지표 원포인트 과외(CVD · 켈리 비중 · 볼린저 밴드 · 거시 레짐).</summary>
    public static class PlutoConceptGuide
    {
        public const string Cvd = "cvd", Kelly = "kelly", Bollinger = "bollinger", Regime = "regime";

        public static readonly IReadOnlyList<PlutoConcept> All = new[]
        {
            new PlutoConcept(Cvd, "📊", "CVD · 누적 거래량 델타",
                "CVD = Σ(매수 체결량 − 매도 체결량) — 시장가로 '사러 온' 물량과 '팔러 온' 물량의 누적 차이",
                "가격이 오르는데 CVD 가 내려가면(다이버전스) 상승을 떠받치는 공격적 매수가 없다는 뜻입니다. 플루토는 이 경우 수급 점수를 깎고 악마의 변호인 감점을 붙입니다.",
                "가격만 보지 말고 '누가 급하게 사고 있는가' 를 보세요. 거래량 없는 상승은 쉽게 되돌려집니다.",
                "짧은 구간 CVD 는 대량 체결 한두 건에 크게 흔들립니다 — 추세(방향)로 읽고 절대값에 집착하지 마세요."),
            new PlutoConcept(Kelly, "⚖️", "켈리 비중 · 얼마나 걸 것인가",
                "f* = p − (1 − p) / b  (p = 승률 · b = 평균 이익 ÷ 평균 손실) — 플루토는 이 값의 일부(분수 켈리)만 사용",
                "승률과 손익비가 표본으로 확인된 만큼만 비중을 키웁니다. 표본이 적으면 켈리를 쓰지 않고 동적 비중 5~25% 범위에서 보수적으로 들어갑니다.",
                "확신이 크다고 많이 거는 것이 아니라, 검증된 엣지가 클 때만 많이 겁니다. 풀 켈리는 변동이 너무 커서 프로도 1/4~1/2 만 씁니다.",
                "승률을 과대평가하면 켈리는 바로 과다 베팅이 됩니다 — 추정치가 불확실할수록 더 작게."),
            new PlutoConcept(Bollinger, "📐", "볼린저 밴드 · 스퀴즈",
                "중심 = 20봉 이동평균 · 상단/하단 = 중심 ± 2 × 표준편차 · 밴드폭 = (상단 − 하단) ÷ 중심",
                "밴드폭이 역사적 저점까지 좁아지는 '스퀴즈' 뒤에는 큰 변동이 오는 경향이 있습니다. 플루토는 스퀴즈 + 거래량 동반 돌파를 기술적 타점으로 채점합니다.",
                "밴드 상단 터치 = 무조건 매도 신호가 아닙니다. 추세장에서는 밴드를 타고 오르고, 박스장에서만 상·하단이 되돌림 지점이 됩니다.",
                "스퀴즈는 '언제' 를 알려줄 뿐 '어느 방향' 인지는 알려주지 않습니다 — 방향은 수급(CVD)으로 확인하세요."),
            new PlutoConcept(Regime, "🌐", "거시 레짐 · 지금은 어떤 장인가",
                "현재 관측(VIX · 금리 · 달러 · 원화 · 지수 추세 · 심리)을 12대 역사적 국면 벡터와 코사인 × 거리 유사도로 비교",
                "같은 신호도 장의 성격에 따라 승률이 다릅니다. 플루토는 가장 닮은 과거 국면의 승리 전략(추세 추종 · 평균 회귀 · 방어)으로 가중치를 기울입니다. FRED 키가 없으면 로컬 캔들(실현변동성 · 추세 · BTC 흐름)로 추정합니다.",
                "'이번엔 다르다' 는 말을 조심하되, 과거 국면과 지금이 어디가 닮고 어디가 다른지 적어 보세요. 국면이 바뀌면 전략도 바뀌어야 합니다.",
                "유사도는 근사일 뿐입니다 — 유사도 60% 미만이면 플루토도 국면 편향을 적용하지 않습니다."),
        };

        public static PlutoConcept? Get(string? key)
        {
            foreach (var c in All) if (string.Equals(c.Key, key, StringComparison.Ordinal)) return c;
            return null;
        }
    }

    /// <summary>코칭 등급 — 0 양호 · 1 주의 · 2 경고.</summary>
    public static class PlutoCoachGrade
    {
        public const int Good = 0, Caution = 1, Warn = 2;

        public static string Label(int g) => g switch { Warn => "🔴 경고", Caution => "🟡 주의", _ => "🟢 양호" };
        public static string Css(int g) => g switch { Warn => "warn", Caution => "caution", _ => "good" };
    }

    /// <summary>코칭 1줄(Topic = 스프레드 · 비중 · 리스크).</summary>
    public readonly record struct PlutoCoachItem(string Topic, int Grade, string Text);

    /// <summary>휴먼 주문 코칭 피드백(Grade = 항목 중 최악 · Accepted = 접수 여부).</summary>
    public sealed record PlutoCoachFeedback(int Mode, int Side, bool Accepted, int Grade, IReadOnlyList<PlutoCoachItem> Items)
    {
        public string Headline => "🤖 플루토 코칭 " + PlutoCoachGrade.Label(Grade) + (Accepted ? "" : " · 접수 거부된 주문");
    }

    /// <summary>휴먼 주문 원포인트 코칭(순수 · 가격/금액 단위 = 아레나 원장 단위 · qtyScale = 수량 스케일(주 = 1 · 코인 raw = 10^8)).</summary>
    public static class PlutoOrderCoach
    {
        public const int SpreadCautionBp = 15, SpreadWarnBp = 50, SizeCautionBp = 1000, SizeWarnBp = 2500, PlutoStopBp = 700;

        public static PlutoCoachFeedback Evaluate(int mode, int side, bool accepted, long bid, long ask, long qty, long qtyScale, long equity, long cash, long heldQty,
            int regime, bool inCooldown)
        {
            var items = new List<PlutoCoachItem>(3);
            // ① 스프레드
            if (bid > 0 && ask >= bid)
            {
                long mid = (bid + ask) / 2;
                long spreadBp = mid > 0 ? (long)((Int128)(ask - bid) * 10000 / mid) : 0;
                string s = "스프레드 " + Pct(spreadBp, 2) + " — ";
                items.Add(spreadBp >= SpreadWarnBp ? new PlutoCoachItem("스프레드", PlutoCoachGrade.Warn, s + "시장가는 체결 즉시 약 " + Pct(spreadBp / 2, 2) + " 불리하게 출발합니다. 호가가 얇은 종목은 지정가 · 분할 접수가 유리합니다.")
                    : spreadBp >= SpreadCautionBp ? new PlutoCoachItem("스프레드", PlutoCoachGrade.Caution, s + "다소 넓습니다. 플루토는 이 정도면 진입 점수에서 유동성 감점을 줍니다.")
                    : new PlutoCoachItem("스프레드", PlutoCoachGrade.Good, s + "촘촘합니다. 시장가 비용이 작습니다."));
            }
            else items.Add(new PlutoCoachItem("스프레드", PlutoCoachGrade.Caution, "호가 미수신 — 스프레드를 판단할 수 없습니다."));

            // ② 비중
            long px = side > 0 ? ask : bid;
            if (side > 0)
            {
                if (px <= 0 || qty <= 0 || qtyScale <= 0 || equity <= 0) items.Add(new PlutoCoachItem("비중", PlutoCoachGrade.Caution, "가격 · 자본 판독 불가 — 비중을 계산할 수 없습니다."));
                else
                {
                    Int128 notional = (Int128)px * qty / qtyScale;
                    long sizeBp = (long)Int128.Min(notional * 10000 / equity, 1_000_000);
                    string s = "이번 매수 = 자본의 " + Pct(sizeBp, 1) + " — ";
                    if (notional > cash) items.Add(new PlutoCoachItem("비중", PlutoCoachGrade.Warn, s + "예수금을 넘습니다. 가상 체결이 거부되거나 일부만 체결될 수 있습니다."));
                    else if (sizeBp >= SizeWarnBp) items.Add(new PlutoCoachItem("비중", PlutoCoachGrade.Warn, s + "과다 베팅입니다. 플루토의 동적 비중 상한(25%)을 넘습니다 — 한 번의 손절이 계좌 전체에 큰 상처를 냅니다."));
                    else if (sizeBp >= SizeCautionBp) items.Add(new PlutoCoachItem("비중", PlutoCoachGrade.Caution, s + "검증된 엣지가 없다면 큽니다. 플루토는 표본 확인 전 5~10% 분할 진입부터 시작합니다."));
                    else items.Add(new PlutoCoachItem("비중", PlutoCoachGrade.Good, s + "분할 진입 범위입니다. 확신이 커지면 나눠서 추가하세요."));
                }
            }
            else
            {
                items.Add(heldQty <= 0 ? new PlutoCoachItem("비중", PlutoCoachGrade.Warn, "보유 수량이 없는 매도입니다 — 가상 원장은 공매도를 허용하지 않아 거부됩니다.")
                    : qty > heldQty ? new PlutoCoachItem("비중", PlutoCoachGrade.Warn, "보유 수량보다 많은 매도입니다 — 보유분만 체결되거나 거부됩니다.")
                    : new PlutoCoachItem("비중", PlutoCoachGrade.Good, qty * 2 <= heldQty ? "부분 매도 — 일부 이익 확정 후 나머지로 추세를 따라가는 분할 익절입니다." : "보유분 정리 매도입니다."));
            }

            // ③ 리스크
            int r = ArenaRegimeCode.Normalize(regime);
            if (side > 0 && r == ArenaRegimeCode.PanicLiquidation)
                items.Add(new PlutoCoachItem("리스크", PlutoCoachGrade.Warn, "패닉 국면입니다 — 플루토 거버너는 지금 신규 매수를 차단합니다. 떨어지는 칼날은 바닥 확인 후에 잡으세요."));
            else if (inCooldown)
                items.Add(new PlutoCoachItem("리스크", PlutoCoachGrade.Caution, "AI 도 연패 쿨다운 중인 구간입니다 — 손실을 만회하려는 '복수 매매' 인지 한 번 점검하세요."));
            else if (side > 0)
                items.Add(new PlutoCoachItem("리스크", r == ArenaRegimeCode.TrendingBear ? PlutoCoachGrade.Caution : PlutoCoachGrade.Good,
                    (r == ArenaRegimeCode.TrendingBear ? "하락 추세 국면의 역추세 매수입니다 — " : "") + "휴먼 티켓에는 자동 손절이 없습니다. 진입가 대비 " + Pct(-PlutoStopBp, 0) + " 손절선을 미리 정하세요(플루토 기본 손절 규칙)."));
            else
                items.Add(new PlutoCoachItem("리스크", PlutoCoachGrade.Good, "매도는 리스크를 줄이는 방향입니다 — 청산 이유(목표 도달 · 근거 소멸)를 기록해 두면 다음 판단에 도움이 됩니다."));

            int grade = PlutoCoachGrade.Good;
            foreach (var it in items) grade = Math.Max(grade, it.Grade);
            return new PlutoCoachFeedback(mode, side, accepted, grade, items);
        }

        private static string Pct(long bp, int decimals) => (bp / 100m).ToString(decimals == 0 ? "0" : decimals == 1 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "%";
    }
}

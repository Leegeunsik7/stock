// ============================================================================
// PlutoPostMortemReviewer — v12.18.67-DEV: 플루토 브레인 고도화 — 청산 자가 회고(PlutoPostMortem · 값 타입 · 진단 = 정수/플래그 · δ=0)
//
//   AI 청산 근거 1건 = 회고 1건(1:1 · 같은 AiTradeRationale 값에 내장 → 근거 기록과 원자적으로 함께 게시).
//   진입 시 엔진이 포지션에 남긴 메모(PlutoEntryMemo: 진입 근거 순번 · 시각 · 원가 · 국면 · 반대 논리 · 감점 · 확신도 · 비중)와
//   청산 사유 · 청산 국면 · 누적 손익 · ATR 로 진단한다.
//     판정: 누적 손익 > 0 = 승리(Win) · ≤ 0 = 패착(Loss — 엔진 승패 집계 · 연패 기준과 동일).
//     요인(PostMortemFactor 플래그): 추세 순응 진입 · 국면 전환 · 반대 논리 무시 진입 · 목표가/손절가 · 시그널 약화 · 외부 충격(뉴스/밸류 과열) ·
//       고변동성(ATR > 20bp) · 장 마감 강제 · 연패 축소 진입.
//     교훈(PlutoLesson): 패착 = 외부 충격 > 반대 논리 무시 > 국면 전환 > 고변동 손절 > 마감 강제 > 시그널 약화 ·
//       승리 = 마감 강제 > 추세 순응 익절 > 반대 논리 극복 > 규율 청산 — 우선순위 1개.
//   한국어 문장(PlutoPostMortemText.Compose)은 판독 시점에 조립하는 콜드 경로(UI/어댑터 전용 — 체결 루프는 호출하지 않는다).
//   ★δ=0: Review 힙 할당 0(Int128 수익률 · 플래그 연산만). 부분 청산 = IsFinal false(누적 손익 기준 중간 회고 · 연패 집계 제외).
// ============================================================================
using System;
using System.Globalization;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분

namespace StockManager.Engines.Cognitive.Governance
{
    public enum PostMortemVerdict : byte
    {
        None = 0,
        Win = 1,
        Loss = 2,
    }

    [Flags]
    public enum PostMortemFactor : ushort
    {
        None = 0,
        RegimeAligned = 1,
        RegimeShift = 2,
        IgnoredCounterThesis = 4,
        TargetHit = 8,
        StopHit = 16,
        SignalDecay = 32,
        ExogenousShock = 64,
        HighVolatility = 128,
        SessionForced = 256,
        StreakCut = 512,
    }

    public enum PlutoLesson : byte
    {
        None = 0,
        RespectCounterThesis = 1,
        RegimeShiftExit = 2,
        VolatilityStop = 3,
        SignalDecay = 4,
        ExogenousShock = 5,
        SessionTimebox = 6,
        TrendFollow = 7,
        ContrarianWin = 8,
        DisciplinedExit = 9,
    }

    /// <summary>진입 시 포지션에 남기는 회고용 메모(값 타입 · EntrySeq = 진입 근거 순번 · 0 = 메모 없음).</summary>
    public struct PlutoEntryMemo
    {
        public long EntrySeq, EntryTicks, EntryCost;
        public int Penalty, Confidence, WeightBp;
        public MarketRegime Regime;
        public CounterThesis Theses;
        public bool StreakCut;

        public readonly bool HasMemo => EntrySeq > 0;
    }

    /// <summary>청산 자가 회고(값 타입 · default = 회고 없음).</summary>
    public readonly struct PlutoPostMortem
    {
        public readonly long EntrySeq;
        public readonly long HoldTicks;
        public readonly long TradePnl;
        public readonly int ReturnBp;
        public readonly int EntryPenalty;
        public readonly MarketRegime EntryRegime, ExitRegime;
        public readonly CounterThesis EntryTheses;
        public readonly ArenaTrigger ExitTrigger;
        public readonly PostMortemVerdict Verdict;
        public readonly PostMortemFactor Factors;
        public readonly PlutoLesson Lesson;
        public readonly bool IsFinal;

        public PlutoPostMortem(long entrySeq, long holdTicks, long tradePnl, int returnBp, int entryPenalty, MarketRegime entryRegime, MarketRegime exitRegime,
            CounterThesis entryTheses, ArenaTrigger exitTrigger, PostMortemVerdict verdict, PostMortemFactor factors, PlutoLesson lesson, bool isFinal)
        {
            EntrySeq = entrySeq; HoldTicks = holdTicks; TradePnl = tradePnl; ReturnBp = returnBp; EntryPenalty = entryPenalty;
            EntryRegime = entryRegime; ExitRegime = exitRegime; EntryTheses = entryTheses; ExitTrigger = exitTrigger;
            Verdict = verdict; Factors = factors; Lesson = lesson; IsFinal = isFinal;
        }

        public bool HasReview => Verdict != PostMortemVerdict.None;

        /// <summary>무결성: 회고 없음(전 필드 default) 또는 진입 순번 양수 ∧ 보유 시간 ≥ 0 ∧ 판정 = 손익 부호 ∧ 청산 사유 ∧ 교훈 ∧ 판정과 교훈 계열 일치.</summary>
        public bool IsWellFormed
        {
            get
            {
                if (Verdict == PostMortemVerdict.None)
                    return EntrySeq == 0 && HoldTicks == 0 && TradePnl == 0 && ReturnBp == 0 && Factors == PostMortemFactor.None && Lesson == PlutoLesson.None && !IsFinal;
                return EntrySeq > 0 && HoldTicks >= 0 && ArenaTriggerText.IsExit(ExitTrigger)
                    && Verdict == (TradePnl > 0 ? PostMortemVerdict.Win : PostMortemVerdict.Loss)
                    && (Verdict == PostMortemVerdict.Win ? PlutoPostMortemReviewer.IsWinLesson(Lesson) : PlutoPostMortemReviewer.IsLossLesson(Lesson))
                    && MarketRegimeText.IsDefined(EntryRegime) && MarketRegimeText.IsDefined(ExitRegime);
            }
        }
    }

    public static class PlutoPostMortemReviewer
    {
        public static bool IsLossLesson(PlutoLesson l) => l is >= PlutoLesson.RespectCounterThesis and <= PlutoLesson.SessionTimebox;
        public static bool IsWinLesson(PlutoLesson l) => l is PlutoLesson.SessionTimebox or >= PlutoLesson.TrendFollow and <= PlutoLesson.DisciplinedExit;

        /// <summary>청산 1건 회고(δ=0). memo 없음(EntrySeq 0) = default(회고 없음). tradePnl = 포지션 누적 실현손익(이번 청산분 포함).</summary>
        public static PlutoPostMortem Review(in PlutoEntryMemo memo, ArenaTrigger exit, MarketRegime exitRegime, long tradePnl, bool isFinal, long nowTicks, int atrCbp)
        {
            if (!memo.HasMemo || !ArenaTriggerText.IsExit(exit)) return default;
            var verdict = tradePnl > 0 ? PostMortemVerdict.Win : PostMortemVerdict.Loss;
            int ret = memo.EntryCost <= 0 ? 0 : (int)Int128.Clamp((Int128)tradePnl * 10000 / memo.EntryCost, -1_000_000, 1_000_000);
            long hold = Math.Max(0, nowTicks - memo.EntryTicks);

            var f = PostMortemFactor.None;
            if (memo.Regime == MarketRegime.TrendingBull) f |= PostMortemFactor.RegimeAligned;
            if (memo.Regime != MarketRegime.Unknown && exitRegime != MarketRegime.Unknown && exitRegime != memo.Regime) f |= PostMortemFactor.RegimeShift;
            if (memo.Theses != CounterThesis.None) f |= PostMortemFactor.IgnoredCounterThesis;
            if (atrCbp > PlutoDynamicSizer.TargetAtrCbp) f |= PostMortemFactor.HighVolatility;
            if (memo.StreakCut) f |= PostMortemFactor.StreakCut;
            f |= exit switch
            {
                ArenaTrigger.TakeProfit => PostMortemFactor.TargetHit,
                ArenaTrigger.StopLoss => PostMortemFactor.StopHit,
                ArenaTrigger.SignalReversal => PostMortemFactor.SignalDecay,
                ArenaTrigger.NewsShock or ArenaTrigger.ValuationBubble => PostMortemFactor.ExogenousShock,
                ArenaTrigger.SessionClose => PostMortemFactor.SessionForced,
                _ => PostMortemFactor.None,
            };

            PlutoLesson lesson;
            if (verdict == PostMortemVerdict.Loss)
            {
                if ((f & PostMortemFactor.ExogenousShock) != 0) lesson = PlutoLesson.ExogenousShock;
                else if ((f & PostMortemFactor.IgnoredCounterThesis) != 0) lesson = PlutoLesson.RespectCounterThesis;
                else if ((f & PostMortemFactor.RegimeShift) != 0) lesson = PlutoLesson.RegimeShiftExit;
                else if ((f & (PostMortemFactor.StopHit | PostMortemFactor.HighVolatility)) == (PostMortemFactor.StopHit | PostMortemFactor.HighVolatility)) lesson = PlutoLesson.VolatilityStop;
                else if ((f & PostMortemFactor.SessionForced) != 0) lesson = PlutoLesson.SessionTimebox;
                else lesson = PlutoLesson.SignalDecay;
            }
            else if ((f & PostMortemFactor.SessionForced) != 0) lesson = PlutoLesson.SessionTimebox;
            else if ((f & (PostMortemFactor.TargetHit | PostMortemFactor.RegimeAligned)) == (PostMortemFactor.TargetHit | PostMortemFactor.RegimeAligned)) lesson = PlutoLesson.TrendFollow;
            else if ((f & PostMortemFactor.IgnoredCounterThesis) != 0) lesson = PlutoLesson.ContrarianWin;
            else lesson = PlutoLesson.DisciplinedExit;

            return new PlutoPostMortem(memo.EntrySeq, hold, tradePnl, ret, memo.Penalty, memo.Regime, exitRegime, memo.Theses, exit, verdict, f, lesson, isFinal);
        }
    }

    /// <summary>자가 회고 한국어 문장(콜드 경로 — 호출마다 문자열 조립).</summary>
    public static class PlutoPostMortemText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string LessonText(PlutoLesson l) => l switch
        {
            PlutoLesson.RespectCounterThesis => "반대 논리 경고가 있던 진입은 다음엔 관망하거나 비중을 더 줄인다",
            PlutoLesson.RegimeShiftExit => "국면 전환 신호가 나오면 손절가 도달 전에 선제적으로 비중을 줄인다",
            PlutoLesson.VolatilityStop => "고변동성 구간에선 손절 폭 대비 비중을 낮춰 소음성 손절을 피한다",
            PlutoLesson.SignalDecay => "진입 근거의 지속성을 더 엄격히 확인하고, 근거가 약해지면 미련 없이 정리한다",
            PlutoLesson.ExogenousShock => "외부 악재·과열 판정은 기술적 신호보다 우선한다 — 진입 전 이벤트 리스크를 재확인한다",
            PlutoLesson.SessionTimebox => "마감 강제 청산 — 장 후반 진입은 남은 보유 시간을 고려해 비중을 줄인다",
            PlutoLesson.TrendFollow => "추세 순응(정배열) 진입이 목표가에 도달했다 — 같은 국면의 진입 원칙을 유지한다",
            PlutoLesson.ContrarianWin => "반대 논리를 이겨낸 수익이지만 운의 몫을 경계한다 — 감점 규칙은 그대로 유지한다",
            PlutoLesson.DisciplinedExit => "계획한 청산 규칙대로 이익을 확정했다 — 규율을 유지한다",
            _ => "교훈 없음",
        };

        private static string FactorLabel(PostMortemFactor f) => f switch
        {
            PostMortemFactor.RegimeAligned => "추세 순응 진입",
            PostMortemFactor.RegimeShift => "보유 중 국면 전환",
            PostMortemFactor.IgnoredCounterThesis => "반대 논리 경고 속 진입",
            PostMortemFactor.TargetHit => "목표가 도달",
            PostMortemFactor.StopHit => "손절가 이탈",
            PostMortemFactor.SignalDecay => "시그널 약화",
            PostMortemFactor.ExogenousShock => "외부 충격(뉴스/밸류 과열)",
            PostMortemFactor.HighVolatility => "고변동성",
            PostMortemFactor.SessionForced => "장 마감 강제",
            PostMortemFactor.StreakCut => "연패 축소 진입",
            _ => "",
        };

        public static string Compose(in PlutoPostMortem pm)
        {
            if (!pm.HasReview) return "";
            string factors = "";
            for (int bit = 0; bit < 16; bit++)
            {
                var f = (PostMortemFactor)(1 << bit);
                if ((pm.Factors & f) == 0) continue;
                string label = FactorLabel(f);
                if (label.Length > 0) factors = factors.Length == 0 ? label : factors + ", " + label;
            }
            if (factors.Length == 0) factors = "특이 요인 없음";
            long a = Math.Abs((long)pm.ReturnBp);
            string ret = (pm.ReturnBp > 0 ? "+" : pm.ReturnBp < 0 ? "-" : "") + $"{(a / 100).ToString(Inv)}.{(a % 100).ToString("D2", Inv)}%";
            string verdict = pm.Verdict == PostMortemVerdict.Win ? "승리 요인" : "패착 요인";
            string scope = pm.IsFinal ? "" : "(부분 청산 중간 회고)";
            string theses = pm.EntryTheses == CounterThesis.None ? "" : string.Create(Inv, $" · 진입 시 악마의 변호인 감점 {pm.EntryPenalty}점");
            return $"플루토 자가 회고{scope}: {ret}(보유 {(pm.HoldTicks / TimeSpan.TicksPerMinute).ToString(Inv)}분 · 진입 근거 #{pm.EntrySeq.ToString(Inv)}) — 청산 {ArenaTriggerText.Label(pm.ExitTrigger)} · " +
                   $"국면 {MarketRegimeText.Label(pm.EntryRegime)}→{MarketRegimeText.Label(pm.ExitRegime)}{theses} · {verdict}: {factors} · 교훈: {LessonText(pm.Lesson)}.";
        }
    }
}

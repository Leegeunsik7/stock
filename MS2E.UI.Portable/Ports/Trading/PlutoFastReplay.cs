// ============================================================================
// PlutoFastReplay — v12.18.93-DEV: 원클릭 자율 학습 가속기 결과 · 플루토 공부 노트(학습 일지) UI 경계(순수 DTO · 순수 문장 합성 · 엔진 타입 참조 0)
//
//   PlutoReplayRunView = 직전 가속 리플레이 1회(자산군별 라운드 G→G+1→G+2 · 섀도우 홀드아웃 승률 기준 → 후보 · 가중치 Δ · 교훈 집계).
//   PlutoStudyJournal.Compose = 결과 → 자연어 학습 일지(무엇을 공부했고 · 어떤 리스크 교훈을 얻었는지) — 결과에 없는 수치 날조 0.
//   ★합성 리플레이 표본 = 실 청산 표본과 분리 표기('합성') · 판독 전용 — 주문 · 무장 · 브로커 경로 0.
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>가속 리플레이 상태 코드 — 0 미결선 · 1 대기(미실행) · 2 실행 중 · 3 완료 · 4 로컬 캔들 없음 · 5 판독 장애(미상 = 미결선).</summary>
    public static class PlutoReplayStateCode
    {
        public const int Off = 0, Idle = 1, Running = 2, Completed = 3, NoData = 4, Failed = 5;

        public static int Normalize(int s) => s is >= Idle and <= Failed ? s : Off;

        public static string Label(int s) => Normalize(s) switch
        {
            Idle => "대기 — 버튼을 누르면 로컬 과거 캔들로 100회 가상 회고를 실행합니다",
            Running => "⏳ 가상 회고 100회 실행 중…",
            Completed => "완료",
            NoData => "로컬 과거 캔들 없음 — 리플레이할 시계열이 없습니다(가짜 시계열 생성 0 · 실시간 학습만 진행)",
            Failed => "캔들 판독 장애 — 이번 가속 학습 중단(기존 가중치 유지)",
            _ => "자율 학습 가속기 미결선(학습 엔진 · 로컬 캔들 저장소 비활성)",
        };
    }

    /// <summary>
    /// 라운드 1회. Gen/Tilt/Strict Before·After = 대상 셀 가중치(미보정 = 사전 가중치 출발점) · Verdict = PlutoShadowVerdictCode ·
    /// BaseWinBp/CandWinBp = 섀도우 홀드아웃 승률(기준선 = 전량 진입 · 후보 = 후보 엄격도로 남는 거래).
    /// </summary>
    public readonly record struct PlutoReplayRoundView(int Round, int Samples, int Wins, long GenBefore, long GenAfter, int TiltBefore, int TiltAfter,
        int StrictBefore, int StrictAfter, int Verdict, int BaseWinBp, int CandWinBp)
    {
        public bool Swapped => GenAfter > GenBefore;
        public int DeltaTilt => TiltAfter - TiltBefore;
        public int DeltaStrict => StrictAfter - StrictBefore;

        /// <summary>승률 개선치 bp(섀도우 비교가 있는 판정만 · 그 외 0).</summary>
        public int GainBp => PlutoShadowVerdictCode.Normalize(Verdict) is PlutoShadowVerdictCode.Passed or PlutoShadowVerdictCode.WinRateDegraded or PlutoShadowVerdictCode.DrawdownWorse
            ? CandWinBp - BaseWinBp : 0;

        public string GenText => "G" + GenBefore.ToString(CultureInfo.InvariantCulture) + (Swapped ? " → G" + GenAfter.ToString(CultureInfo.InvariantCulture) : " (유지)");

        public string GainText => PlutoReplayText.Pp(GainBp);

        public string DeltaText => "모멘텀 " + PlutoReplayText.Signed(DeltaTilt) + "% · 엄격도 " + PlutoReplayText.Signed(DeltaStrict) + "점";
    }

    /// <summary>교훈 집계(승 = 추세 순응 · 규율 청산 / 패 = 외부 충격 · 반대 논리 무시 · 국면 전환 · 시그널 약화) · 평균 승/패 bp.</summary>
    public readonly record struct PlutoLessonTallyView(int TrendWins, int DisciplinedWins, int ShockLosses, int CounterLosses, int RegimeShiftLosses, int DecayLosses,
        int AvgWinBp, int AvgLossBp);

    /// <summary>자산군 1레인(Asset = PlutoRecoMarket 값 · TargetName = 대상 셀 국면명 · 판정 전 = "자산 기준선").</summary>
    public sealed record PlutoReplayLaneView(int Asset, int Series, int Samples, int Wins, string TargetName, IReadOnlyList<PlutoReplayRoundView> Rounds, PlutoLessonTallyView Lessons)
    {
        public int WinRateBp => Samples <= 0 ? 0 : (int)((long)Wins * 10000 / Samples);
        public int Evolutions { get { int n = 0; foreach (var r in Rounds) if (r.Swapped) n++; return n; } }
        public long FirstGen => Rounds.Count == 0 ? 0 : Rounds[0].GenBefore;
        public long LastGen => Rounds.Count == 0 ? 0 : Rounds[^1].GenAfter;
    }

    /// <summary>가속 리플레이 1회 결과(State = PlutoReplayStateCode).</summary>
    public sealed record PlutoReplayRunView(int State, long ElapsedMs, string? RegimeName, IReadOnlyList<PlutoReplayLaneView> Lanes, DateTime? CompletedUtc, int Runs, string? Error = null)
    {
        public static readonly PlutoReplayRunView Unwired = new(PlutoReplayStateCode.Off, 0, null, Array.Empty<PlutoReplayLaneView>(), null, 0);
        public static readonly PlutoReplayRunView Idle = new(PlutoReplayStateCode.Idle, 0, null, Array.Empty<PlutoReplayLaneView>(), null, 0);

        public bool Completed => PlutoReplayStateCode.Normalize(State) == PlutoReplayStateCode.Completed;
        public int Samples { get { int n = 0; foreach (var l in Lanes) n += l.Samples; return n; } }
        public int Evolutions { get { int n = 0; foreach (var l in Lanes) n += l.Evolutions; return n; } }

        /// <summary>상단 요약: "자율 학습 가속 #2 완료 — 가상 회고 300표본(합성) · 진화 4회 · 412ms".</summary>
        public string Summary => !Completed ? PlutoReplayStateCode.Label(State)
            : "자율 학습 가속 #" + Runs.ToString(CultureInfo.InvariantCulture) + " 완료 — 가상 회고 " + Samples.ToString(CultureInfo.InvariantCulture) + "표본(합성) · 진화 "
              + Evolutions.ToString(CultureInfo.InvariantCulture) + "회 · " + ElapsedMs.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    /// <summary>학습 일지 1항목(Icon · 제목 · 본문).</summary>
    public readonly record struct PlutoJournalEntry(string Icon, string Title, string Body);

    public static class PlutoReplayText
    {
        public static string Signed(long v) => (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture);

        /// <summary>bp → "+3.2%p".</summary>
        public static string Pp(int bp) => (bp > 0 ? "+" : "") + (bp / 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%p";

        /// <summary>bp → "+1.25%".</summary>
        public static string Pct(int bp) => (bp > 0 ? "+" : "") + (bp / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>플루토 공부 노트 — 가속 리플레이 결과 → 자연어 학습 일지(순수 · 결과 수치만 인용).</summary>
    public static class PlutoStudyJournal
    {
        public static IReadOnlyList<PlutoJournalEntry> Compose(PlutoReplayRunView run)
        {
            ArgumentNullException.ThrowIfNull(run);
            var list = new List<PlutoJournalEntry>();
            if (!run.Completed)
            {
                list.Add(new PlutoJournalEntry("📓", "학습 일지 비어 있음", PlutoReplayStateCode.Label(run.State)));
                return list;
            }
            list.Add(new PlutoJournalEntry("📚", "오늘 공부한 것",
                "로컬 과거 일봉으로 " + string.Join(" · ", Lanes(run)) + " 가상 회고를 복기했습니다"
                + (run.RegimeName is null ? " — 거시 국면 판정 전이라 자산 기준선 셀을 학습했습니다." : " — 현재 국면 '" + run.RegimeName + "' 슬롯과 자산 기준선을 함께 학습했습니다.")
                + " 모든 표본은 합성 리플레이이며 실 청산 표본 · 평생 에피소드에는 포함되지 않습니다."));
            foreach (var l in run.Lanes)
            {
                string asset = PlutoTensorLaneView.AssetLabel(l.Asset);
                var t = l.Lessons;
                int losses = l.Samples - l.Wins;
                list.Add(new PlutoJournalEntry("🧪", asset + " 복기 결과",
                    l.Samples.ToString(CultureInfo.InvariantCulture) + "회 중 " + l.Wins.ToString(CultureInfo.InvariantCulture) + "승 " + losses.ToString(CultureInfo.InvariantCulture)
                    + "패(승률 " + (l.WinRateBp / 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%) · 평균 승 " + PlutoReplayText.Pct(t.AvgWinBp) + " / 평균 패 " + PlutoReplayText.Pct(t.AvgLossBp)
                    + " · 진화 " + EvolutionText(l)));
                list.Add(new PlutoJournalEntry(RiskIcon(t), asset + " 리스크 교훈", RiskLesson(t, losses)));
            }
            list.Add(new PlutoJournalEntry("🧭", "다음 공부 계획",
                "가상 회고는 출발점일 뿐입니다 — 실제 청산 회고가 20표본 쌓이면 실측 보정 심사가 시작되고, 섀도우 검증을 통과한 변화만 다음 세대로 채택합니다(세대당 엄격도 ±2점 · 모멘텀 ±5% 상한)."));
            return list;
        }

        private static IEnumerable<string> Lanes(PlutoReplayRunView run)
        {
            foreach (var l in run.Lanes)
                yield return PlutoTensorLaneView.AssetLabel(l.Asset) + " " + l.Series.ToString(CultureInfo.InvariantCulture) + "종 " + l.Samples.ToString(CultureInfo.InvariantCulture) + "회";
        }

        public static string EvolutionText(PlutoReplayLaneView l)
        {
            if (l.Rounds.Count == 0) return "라운드 없음";
            if (l.Evolutions == 0)
                return "0회 — 섀도우 검증이 모든 후보를 거부했거나 변화가 없어 G" + l.FirstGen.ToString(CultureInfo.InvariantCulture) + " 를 유지했습니다(과최적화 방지)";
            int gain = 0;
            foreach (var r in l.Rounds) if (r.Swapped) gain += r.GainBp;
            return l.Evolutions.ToString(CultureInfo.InvariantCulture) + "회(G" + l.FirstGen.ToString(CultureInfo.InvariantCulture) + " → G" + l.LastGen.ToString(CultureInfo.InvariantCulture)
                + ") · 채택 라운드 섀도우 승률 개선 합계 " + PlutoReplayText.Pp(gain);
        }

        private static string RiskIcon(in PlutoLessonTallyView t) => t.ShockLosses + t.CounterLosses > t.RegimeShiftLosses + t.DecayLosses ? "🛡️" : "📉";

        /// <summary>패배 교훈 비중 → 리스크 관리 문장(가장 많은 패착 유형 중심).</summary>
        public static string RiskLesson(in PlutoLessonTallyView t, int losses)
        {
            if (losses <= 0) return "패배 표본이 없어 리스크 교훈을 끌어낼 수 없습니다 — 표본이 한쪽으로 치우친 구간일 수 있어 다음 복기에서 다시 확인합니다.";
            int defensive = t.ShockLosses + t.CounterLosses, tempo = t.RegimeShiftLosses + t.DecayLosses;
            string mix = "패착 " + losses.ToString(CultureInfo.InvariantCulture) + "건 = 외부 충격 " + t.ShockLosses.ToString(CultureInfo.InvariantCulture)
                + " · 역추세 진입 " + t.CounterLosses.ToString(CultureInfo.InvariantCulture) + " · 국면 전환 " + t.RegimeShiftLosses.ToString(CultureInfo.InvariantCulture)
                + " · 시그널 약화 " + t.DecayLosses.ToString(CultureInfo.InvariantCulture) + ". ";
            if (defensive > tempo)
                return mix + "하락 추세에서 들어간 진입과 급락 충격이 손실의 중심이었습니다 → 악마의 변호인 엄격도를 높여(감점 강화) 확신이 약한 역추세 진입을 걸러야 합니다. 손절선(−7%)은 지키되 충격 봉에서는 비중을 줄이는 것이 교훈입니다.";
            if (tempo > defensive)
                return mix + "진입 근거가 보유 중에 식거나 추세가 꺾인 사례가 많았습니다 → 모멘텀 추격 비중을 낮추고 목표를 짧게 잡아 분할 익절하는 것이 교훈입니다.";
            return mix + "방어형 패착과 타이밍형 패착이 비슷합니다 → 진입 엄격도와 보유 기간을 함께 점검합니다.";
        }
    }
}

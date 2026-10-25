// ============================================================================
// PlutoWeightCalibrator — v12.18.77-DEV: 플루토 지속 학습 Track B — 청산 자가 회고(Post-Mortem) 기반 가중치 자가 보정 + 과최적화 방지 섀도우 검증 게이트
//
//   표본(PlutoLearningSample) = 최종 청산 회고 1건(부분 청산 = 제외) · 수익률 bp · 진입 최종 확신도(미상 = −1) · 진입 당시 엄격도 · 교훈 · 판정. 최근 Window(256) 링.
//   제안(Propose): 링을 짝수 순번 = 학습(train) · 홀수 순번 = 검증(holdout) 으로 분할 — 제안은 학습 절반의 교훈 압력만으로 계산한다.
//     엄격도 압력 = (반대 논리 무시 패착 + 외부 충격 패착) − ⌊(추세 순응 승리 + 규율 청산 승리) / 2⌋ → 부호 방향으로 세대당 ±MaxStrictnessStep(2) 이내.
//       (반대 논리 극복 승리 = 0 — '운의 몫' 교훈 · 감점 규칙 유지)
//     모멘텀 압력 = 추세 순응 승리 − (국면 전환 패착 + 시그널 약화 패착) → 세대당 ±MaxTiltStep(5)% 이내.
//     표본 < MinSamples(20) = 제안 없음(현재 유지). 범위 = PlutoCognitiveWeights 상·하한 포화.
//   섀도우 검증(ShadowValidate · 홀드아웃 절반만): 후보 엄격도 Δ = 후보 − 진입 당시 엄격도 → 확신도 기지 표본은 (확신도 − Δ) < 30 이면 '건너뛴 거래' 로 재생.
//     기준선(실제 전량 진입) vs 후보(남은 거래) — 승률 bp · 누적 수익률(bp 가산) 곡선 MDD bp.
//     통과 = 후보 잔존 ≥ MinHoldout(10) ∧ 후보 승률 ≥ 기준 − 200bp ∧ 후보 MDD ≤ 기준 MDD + 100bp. 불통과 = 핫스왑 거부(기존 가중치 유지).
//     ※ 모멘텀 tilt 는 점수 구성요소가 표본에 없어 재생 불가 — 세대당 ±5% 상한 + 같은 홀드아웃 게이트(성과 악화 시 동반 거부)로만 묶는다(정직 표기).
//   ★Track B 전용 콜드 경로(단일 잠금 · 체결 스레드 무간섭). Observe/Propose/ShadowValidate 힙 할당 0(고정 링 · 값 타입).
// ============================================================================
using System;
using StockManager.Engines.Cognitive.Governance;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>학습 표본 1건(값 타입).</summary>
    public readonly struct PlutoLearningSample
    {
        public readonly int ReturnBp, EntryConfidence, StrictnessAtEntry;
        public readonly PlutoLesson Lesson;
        public readonly PostMortemVerdict Verdict;

        public PlutoLearningSample(int returnBp, int entryConfidence, int strictnessAtEntry, PlutoLesson lesson, PostMortemVerdict verdict)
        {
            ReturnBp = returnBp; EntryConfidence = entryConfidence; StrictnessAtEntry = strictnessAtEntry; Lesson = lesson; Verdict = verdict;
        }

        public bool IsWin => Verdict == PostMortemVerdict.Win;
    }

    public enum PlutoShadowReason : byte
    {
        None = 0,
        Passed = 1,
        InsufficientHoldout = 2,
        WinRateDegraded = 3,
        DrawdownWorse = 4,
        NoChange = 5,
    }

    /// <summary>섀도우 검증 판정(값 타입).</summary>
    public readonly struct PlutoShadowVerdict
    {
        public readonly PlutoShadowReason Reason;
        public readonly int BaselineWinRateBp, ShadowWinRateBp, BaselineMddBp, ShadowMddBp, Holdout, Kept;

        public PlutoShadowVerdict(PlutoShadowReason reason, int baselineWinRateBp, int shadowWinRateBp, int baselineMddBp, int shadowMddBp, int holdout, int kept)
        {
            Reason = reason; BaselineWinRateBp = baselineWinRateBp; ShadowWinRateBp = shadowWinRateBp; BaselineMddBp = baselineMddBp; ShadowMddBp = shadowMddBp;
            Holdout = holdout; Kept = kept;
        }

        public bool Passed => Reason == PlutoShadowReason.Passed;
    }

    public sealed class PlutoWeightCalibrator
    {
        public const int Window = 256, MinSamples = 20, MinHoldout = 10;
        public const int MaxStrictnessStep = 2, MaxTiltStep = 5;
        public const int WinRateToleranceBp = 200, MddToleranceBp = 100;

        private readonly PlutoLearningSample[] _ring = new PlutoLearningSample[Window];
        private readonly object _gate = new();
        private long _total;

        /// <summary>누적 표본 수(링 덮어쓰기 포함).</summary>
        public long Observed { get { lock (_gate) return _total; } }
        public int Count { get { lock (_gate) return (int)Math.Min(_total, Window); } }

        /// <summary>청산 회고 1건 학습(δ=0). 최종 청산 ∧ 무결 회고만(부분 청산 · 회고 없음 = false).</summary>
        public bool Observe(in PlutoPostMortem pm, int entryConfidence, int strictnessAtEntry)
        {
            if (!pm.HasReview || !pm.IsFinal || !pm.IsWellFormed) return false;
            int conf = entryConfidence is >= 0 and <= 100 ? entryConfidence : -1;
            var s = new PlutoLearningSample(pm.ReturnBp, conf, strictnessAtEntry, pm.Lesson, pm.Verdict);
            lock (_gate)
            {
                _ring[(int)(_total % Window)] = s;
                _total++;
            }
            return true;
        }

        /// <summary>
        /// 학습 절반(링 순서 짝수 번째) 교훈 압력 → 후보 가중치(δ=0). 표본 부족 = false(candidate = current).
        /// </summary>
        public bool Propose(in PlutoCognitiveWeights current, out PlutoCognitiveWeights candidate)
        {
            candidate = current;
            int strictP = 0, winsCalm = 0, tiltP = 0;
            lock (_gate)
            {
                int n = (int)Math.Min(_total, Window);
                if (n < MinSamples) return false;
                long start = _total - n;
                for (int k = 0; k < n; k += 2)
                {
                    ref readonly var s = ref _ring[(int)((start + k) % Window)];
                    switch (s.Lesson)
                    {
                        case PlutoLesson.RespectCounterThesis or PlutoLesson.ExogenousShock: strictP++; break;
                        case PlutoLesson.TrendFollow: winsCalm++; tiltP++; break;
                        case PlutoLesson.DisciplinedExit: winsCalm++; break;
                        case PlutoLesson.RegimeShiftExit or PlutoLesson.SignalDecay: tiltP--; break;
                    }
                }
            }
            strictP -= winsCalm / 2;
            int strict = Math.Clamp(current.DevilStrictnessPts + Math.Clamp(strictP, -MaxStrictnessStep, MaxStrictnessStep),
                PlutoCognitiveWeights.MinStrictnessPts, PlutoCognitiveWeights.MaxStrictnessPts);
            int tilt = Math.Clamp(current.MomentumTiltPct + Math.Clamp(tiltP, -1, 1) * MaxTiltStep,
                -PlutoCognitiveWeights.MaxMomentumTiltPct, PlutoCognitiveWeights.MaxMomentumTiltPct);
            candidate = new PlutoCognitiveWeights(current.Generation, tilt, strict);
            return true;
        }

        /// <summary>홀드아웃 절반(링 순서 홀수 번째) 섀도우 재생(δ=0). 통과 시 candidate 에 섀도우 계수를 실어 돌려준다.</summary>
        public PlutoShadowVerdict ShadowValidate(in PlutoCognitiveWeights current, ref PlutoCognitiveWeights candidate)
        {
            if (candidate.MomentumTiltPct == current.MomentumTiltPct && candidate.DevilStrictnessPts == current.DevilStrictnessPts)
                return new PlutoShadowVerdict(PlutoShadowReason.NoChange, 0, 0, 0, 0, 0, 0);
            int holdout = 0, kept = 0, baseWins = 0, candWins = 0;
            long baseEq = 0, basePeak = 0, baseMdd = 0, candEq = 0, candPeak = 0, candMdd = 0;
            lock (_gate)
            {
                int n = (int)Math.Min(_total, Window);
                long start = _total - n;
                for (int k = 1; k < n; k += 2)
                {
                    ref readonly var s = ref _ring[(int)((start + k) % Window)];
                    holdout++;
                    if (s.IsWin) baseWins++;
                    baseEq += s.ReturnBp; basePeak = Math.Max(basePeak, baseEq); baseMdd = Math.Max(baseMdd, basePeak - baseEq);
                    int delta = candidate.DevilStrictnessPts - s.StrictnessAtEntry;
                    if (s.EntryConfidence >= 0 && s.EntryConfidence - delta < PlutoDevilsAdvocateFilter.MinEntryConfidence) continue;   // 후보라면 건너뛴 거래
                    kept++;
                    if (s.IsWin) candWins++;
                    candEq += s.ReturnBp; candPeak = Math.Max(candPeak, candEq); candMdd = Math.Max(candMdd, candPeak - candEq);
                }
            }
            int baseWr = holdout == 0 ? 0 : baseWins * 10000 / holdout;
            int candWr = kept == 0 ? 0 : candWins * 10000 / kept;
            int bMdd = (int)Math.Min(baseMdd, int.MaxValue), cMdd = (int)Math.Min(candMdd, int.MaxValue);
            PlutoShadowReason reason =
                kept < MinHoldout ? PlutoShadowReason.InsufficientHoldout
                : candWr < baseWr - WinRateToleranceBp ? PlutoShadowReason.WinRateDegraded
                : cMdd > bMdd + MddToleranceBp ? PlutoShadowReason.DrawdownWorse
                : PlutoShadowReason.Passed;
            if (reason == PlutoShadowReason.Passed)
                candidate = new PlutoCognitiveWeights(candidate.Generation, candidate.MomentumTiltPct, candidate.DevilStrictnessPts, candWr, cMdd, kept);
            return new PlutoShadowVerdict(reason, baseWr, candWr, bMdd, cMdd, holdout, kept);
        }
    }
}

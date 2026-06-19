// ============================================================================
// MutationBoundsMatrix — S19 #2: 변이 제약 매트릭스 (유전자 방화벽 B층)
//
//  CartridgeGenome 의 교차·변이 산물이 난수 환각·과적합·논리 모순에 빠지지 않도록
//  강제하는 순수 함수 게이트. 3계층 파이프라인의 B층을 담당한다:
//    (A) 범위 클램프(기존 Mutate) → (B) Reconcile B1~B5(본 모듈) → (C) StrategyCompiler.Validate
//
//  B1 이평 역전 방지   — MA-vs-MA 비교는 항상 단기(Left) < 장기(Right) 정준화
//  B2 타임프레임 하한   — 주기 항을 [MinEffectivePeriod, MaxPeriod]로 클램프
//  B3 손익비(RR) 가드   — SL·TP 범위 클램프 + TakeProfit ≥ StopLoss × MinRR
//  B4 조건 복잡도 상한   — 각 측(진입/청산) 조건 수를 MaxConditionsPerSide로 절단
//  B5 중복·모순 제거     — 완전 중복 + (And 한정) 공집합 구간(Gt≥Lt) 제거
//  B6 표본 하한          — 평가 입력 캔들 수 사전 검문 (CanEvaluate)
//
//  설계: 사이드 이펙트 0 · 결정론(난수·시계 없음) · 외부 의존성 0.
//  StrategyCompiler.Id 는 Id 제외 정준 해시이므로 수리 후 재계산이 안전하다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    // ── 제약 상수 (전부 init — 환경별 튜닝 허용, 기본값은 보수적) ────────────────
    public sealed record MutationBounds
    {
        /// <summary>유효 타임프레임 하한 — 1~2봉 과민 신호 차단 (StrategyCompiler.MinPeriod=2 보다 높음).</summary>
        public int    MinEffectivePeriod   { get; init; } = 3;
        public int    MaxPeriod            { get; init; } = StrategyCompiler.MaxPeriod;   // 200
        public double MaxStopLossPct       { get; init; } = 50.0;
        public double MaxTakeProfitPct     { get; init; } = 100.0;
        /// <summary>최소 손익비 — 익절 ≥ 손절 × MinRR 강제.</summary>
        public double MinRR                { get; init; } = 1.0;
        /// <summary>각 측(진입/청산) 최대 조건 수 — 과적합 트리 팽창 차단(오캄의 면도날).</summary>
        public int    MaxConditionsPerSide { get; init; } = 3;
        /// <summary>평가에 필요한 최소 캔들 수(B6).</summary>
        public int    MinCandles           { get; init; } = 60;

        public static MutationBounds Default { get; } = new();
    }

    public sealed record MutationReconcileResult(
        bool          Ok,        // 수리 후 사용 가능한가 (진입 조건이 1개 이상인가)
        string        Reason,    // 수리 내역 요약 또는 거부 사유
        StrategySpec  Repaired,  // 정준화·수리된 스펙 (Id 재계산됨)
        int           RepairCount);

    public static class MutationBoundsMatrix
    {
        // ── 공개 진입점 ──────────────────────────────────────────────────────────

        /// <summary>변이 산물을 결정론적으로 정준화·수리한다. 진입 조건이 비면 Ok=false.</summary>
        public static MutationReconcileResult Reconcile(StrategySpec spec, MutationBounds? bounds = null)
        {
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            var b = bounds ?? MutationBounds.Default;
            var repairs = new List<string>();

            // B1·B2 — 진입/청산 조건의 항(term) 정준화
            var entry = ReconcileConds(spec.Entry, b, "진입", repairs);
            var exit_ = ReconcileConds(spec.Exit,  b, "청산", repairs);

            // B5 — 중복·모순 제거 (논리 연산자에 따라 모순 판정)
            entry = DedupAndDecontradict(entry, spec.EntryLogic, "진입", repairs);
            exit_ = DedupAndDecontradict(exit_, spec.ExitLogic,  "청산", repairs);

            // B4 — 조건 복잡도 상한 (각 측 절단)
            entry = CapConditions(entry, b.MaxConditionsPerSide, "진입", repairs);
            exit_ = CapConditions(exit_, b.MaxConditionsPerSide, "청산", repairs);

            // B3 — 손익비·범위
            var (sl, tp) = ReconcileRiskReward(spec.StopLossPct, spec.TakeProfitPct, b, repairs);

            var repaired = new StrategySpec("", spec.Title, entry, spec.EntryLogic, exit_, spec.ExitLogic, sl, tp);
            repaired = repaired with { Id = StrategyCompiler.Id(repaired) };

            bool ok = entry.Count > 0;     // 진입 조건이 없으면 시장 진입 불가 → 사용 불가
            string reason = !ok
                ? "진입 조건 없음 — 시장 진입 불가"
                : repairs.Count == 0 ? "무변경" : string.Join(", ", repairs);

            return new MutationReconcileResult(ok, reason, repaired, repairs.Count);
        }

        /// <summary>B6 — 평가 입력 캔들 수가 충분한지 사전 검문.</summary>
        public static bool CanEvaluate(int candleCount, int warmupNeeded, MutationBounds? bounds = null)
        {
            var b = bounds ?? MutationBounds.Default;
            return candleCount >= Math.Max(b.MinCandles, warmupNeeded + 1);
        }

        // ── B1·B2: 조건별 항 정준화 ───────────────────────────────────────────────

        private static List<SpecCond> ReconcileConds(
            IReadOnlyList<SpecCond> conds, MutationBounds b, string side, List<string> repairs)
        {
            var result = new List<SpecCond>(conds?.Count ?? 0);
            if (conds == null) return result;

            foreach (var c in conds)
            {
                // B2 — 좌/우 항 주기 하한·상한 클램프
                var left  = ClampPeriod(c.Left,  b, side, repairs);
                var right = c.Right is null ? null : ClampPeriod(c.Right, b, side, repairs);

                var cond = new SpecCond(left, c.Op, c.RightIsConst, c.Const, right);

                // B1 — MA-vs-MA 비교는 단기(Left) < 장기(Right) 로 정준화
                if (!cond.RightIsConst && cond.Right is not null &&
                    IsMa(cond.Left.Kind) && IsMa(cond.Right.Kind))
                {
                    int lo = Math.Min(cond.Left.Param, cond.Right.Param);
                    int hi = Math.Max(cond.Left.Param, cond.Right.Param);
                    if (lo == hi)
                    {
                        // 동일 주기 = 자기 자신과 교차(절대 발화 안 함) → 결정론적으로 분리
                        if (hi < b.MaxPeriod) hi += 1;
                        else                  lo = Math.Max(b.MinEffectivePeriod, lo - 1);
                    }
                    if (cond.Left.Param != lo || cond.Right.Param != hi)
                    {
                        repairs.Add($"{side}이평역전 {cond.Left.Param}/{cond.Right.Param}->{lo}/{hi}");
                        cond = cond with
                        {
                            Left  = cond.Left  with { Param = lo },
                            Right = cond.Right with { Param = hi },
                        };
                    }
                }
                result.Add(cond);
            }
            return result;
        }

        private static SpecTerm ClampPeriod(SpecTerm t, MutationBounds b, string side, List<string> repairs)
        {
            // Price·MacdHist 는 주기 무의미 → 변경 없음
            if (t.Kind is SpecKind.Price or SpecKind.MacdHist) return t;
            int np = Math.Clamp(t.Param, b.MinEffectivePeriod, b.MaxPeriod);
            if (np != t.Param)
            {
                repairs.Add($"{side}{t.Kind}주기 {t.Param}->{np}");
                return t with { Param = np };
            }
            return t;
        }

        // ── B5: 중복 + 모순 제거 ──────────────────────────────────────────────────

        private static List<SpecCond> DedupAndDecontradict(
            List<SpecCond> conds, SpecLogic logic, string side, List<string> repairs)
        {
            // 1) 완전 중복 제거 (첫 출현 유지 — 결정론)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dedup = new List<SpecCond>(conds.Count);
            foreach (var c in conds)
            {
                if (seen.Add(CondKey(c))) dedup.Add(c);
                else                      repairs.Add($"{side}중복조건 제거");
            }

            // 2) 모순(공집합) 제거 — And 한정: 같은 항에 Gt g, Lt l 이면서 g >= l 이면 불가능 구간
            if (logic != SpecLogic.And) return dedup;

            // 항(term)별 const 비교의 최대 Gt·최소 Lt 수집
            var maxGt = new Dictionary<string, double>(StringComparer.Ordinal);
            var minLt = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var c in dedup)
            {
                if (!c.RightIsConst) continue;
                string k = TermKey(c.Left);
                if (c.Op == SpecOp.Gt) maxGt[k] = maxGt.TryGetValue(k, out var g) ? Math.Max(g, c.Const) : c.Const;
                if (c.Op == SpecOp.Lt) minLt[k] = minLt.TryGetValue(k, out var l) ? Math.Min(l, c.Const) : c.Const;
            }
            var emptyTerms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in maxGt)
                if (minLt.TryGetValue(kv.Key, out var l) && kv.Value >= l)
                    emptyTerms.Add(kv.Key);

            if (emptyTerms.Count == 0) return dedup;

            // 공집합 항의 Lt 조건을 제거(Gt 측 보존) — 결정론
            var result = new List<SpecCond>(dedup.Count);
            foreach (var c in dedup)
            {
                if (c.RightIsConst && c.Op == SpecOp.Lt && emptyTerms.Contains(TermKey(c.Left)))
                {
                    repairs.Add($"{side}모순조건 제거(공집합)");
                    continue;
                }
                result.Add(c);
            }
            return result;
        }

        // ── B4: 복잡도 상한 ───────────────────────────────────────────────────────

        private static List<SpecCond> CapConditions(
            List<SpecCond> conds, int maxPerSide, string side, List<string> repairs)
        {
            if (conds.Count <= maxPerSide) return conds;
            repairs.Add($"{side}조건 {conds.Count}->{maxPerSide} 절단");
            return conds.Take(maxPerSide).ToList();
        }

        // ── B3: 손익비·범위 ───────────────────────────────────────────────────────

        private static (double sl, double tp) ReconcileRiskReward(
            double sl, double tp, MutationBounds b, List<string> repairs)
        {
            double nsl = Math.Clamp(sl, 0.0, b.MaxStopLossPct);
            double ntp = Math.Clamp(tp, 0.0, b.MaxTakeProfitPct);
            if (Math.Abs(nsl - sl) > 1e-9) repairs.Add($"손절범위 {sl:0.#}->{nsl:0.#}");
            if (Math.Abs(ntp - tp) > 1e-9) repairs.Add($"익절범위 {tp:0.#}->{ntp:0.#}");

            // 손절·익절 둘 다 활성일 때만 RR 가드 (0 = 미사용 → 정당)
            if (nsl > 0.0 && ntp > 0.0)
            {
                double minTp = Math.Min(nsl * b.MinRR, b.MaxTakeProfitPct);
                if (ntp < minTp - 1e-9)
                {
                    repairs.Add($"손익비 익절 {ntp:0.#}->{minTp:0.#}(RR≥{b.MinRR:0.#})");
                    ntp = minTp;
                }
            }
            return (nsl, ntp);
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────────

        private static bool IsMa(SpecKind k) => k is SpecKind.Sma or SpecKind.Ema;

        private static string TermKey(SpecTerm t) => $"{(int)t.Kind}:{t.Param}";

        private static string CondKey(SpecCond c) =>
            c.RightIsConst
                ? $"{TermKey(c.Left)}|{(int)c.Op}|K{c.Const:R}"
                : $"{TermKey(c.Left)}|{(int)c.Op}|T{(c.Right is null ? "?" : TermKey(c.Right))}";
    }
}

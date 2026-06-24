// ============================================================================
// QChaos — S28 Mission 3: 카오스 인젝션(거시 폭풍 모사) + 비대칭 진단 + 리밸런싱 밸브
//
//  낙관적 우상향만 그리는 것을 배제한다. 사용자가 "2008 금융위기 재림하면?"이라 카오스를 내리면, 자산군별
//  '시스템 베타'(systemic beta = 상관/민감도 가중)를 발동해 자산이 비대칭적으로 박살나는 한계선을 시험한다.
//  stressed_i = V0_i · max(0, 1 − severity·β(class_i)). β 가 높은 위험자산(크립토)은 같은 폭풍에 더 깊이 무너진다
//  → 위험자산 편중 포트폴리오의 최대낙폭이 자연히 커지고, 진단이 "방어막이 없습니다"를 숫자로 입증한다.
//
//  ★경계 규율(Gemini 룰1): 진단은 '숫자만' 낸다(집중도·방어점수·최악낙폭). "영리한 공격수, 골키퍼 부재" 같은
//  비유적 서술은 LLM 페르소나가 이 숫자를 받아 표현한다 — 코어는 한 자리도 환각하지 않는다.
//
//  ★리밸런싱 밸브(Gemini 룰2): UI 스크롤바(basis point 정수)를 ShadowMutation 으로 정준화해 ScenarioPublisher
//  에 주입한다. 범위 이탈 레버는 모호하게 넘기지 않고 fail-closed(거부) — 낙관 곡선을 거짓 표시하지 않는다.
// ============================================================================
using System;

namespace StockManager.Common
{
    /// <summary>거시 폭풍 시나리오. None 은 무충격(기준).</summary>
    public enum ChaosScenario : byte { None = 0, Gfc2008, RateSpike, InflationShock, CryptoWinter }

    /// <summary>비대칭 진단 결과(숫자 전용 — LLM 이 서술로 윤색). 모든 비율은 %.</summary>
    public readonly record struct DiagnosisReport(
        decimal ConcentrationPct, AssetClass TopClass, decimal DefenseScorePct,
        ChaosScenario WorstScenario, decimal WorstDrawdownPct, decimal TotalValueKrw);

    /// <summary>
    /// Mission 3 — 카오스 스트레스 엔진. 시나리오별 자산군 시스템 베타로 폭풍 충격을 결정론적으로 가한다.
    /// StressTotal/ApplyStress 무할당(고정소수점 곱·가산). severity·β 는 시나리오 고정 테이블.
    /// </summary>
    public static class ChaosEngine
    {
        /// <summary>시나리오 시스템 충격 강도(0..1).</summary>
        public static decimal Severity(ChaosScenario s) => s switch
        {
            ChaosScenario.Gfc2008      => 0.50m,
            ChaosScenario.RateSpike    => 0.25m,
            ChaosScenario.InflationShock => 0.20m,
            ChaosScenario.CryptoWinter => 0.55m,   // 크립토 β2.0 → 0.55×2.0=1.10>1 → 크립토 ~전손(복원 클램프의 실 도달 경로)
            _                          => 0.00m,   // None
        };

        /// <summary>자산군 시스템 베타(상관/민감도 가중) — 높을수록 같은 폭풍에 더 깊이 박살.</summary>
        public static decimal Beta(ChaosScenario s, AssetClass c) => s switch
        {
            ChaosScenario.Gfc2008        => c switch { AssetClass.DomesticStock => 1.0m, AssetClass.ForeignStock => 1.1m, AssetClass.Crypto => 1.5m, _ => 1.0m },
            ChaosScenario.RateSpike      => c switch { AssetClass.DomesticStock => 0.9m, AssetClass.ForeignStock => 1.0m, AssetClass.Crypto => 1.8m, _ => 1.0m },
            ChaosScenario.InflationShock => c switch { AssetClass.DomesticStock => 1.0m, AssetClass.ForeignStock => 0.9m, AssetClass.Crypto => 1.3m, _ => 1.0m },
            ChaosScenario.CryptoWinter   => c switch { AssetClass.DomesticStock => 0.3m, AssetClass.ForeignStock => 0.4m, AssetClass.Crypto => 2.0m, _ => 0.5m },
            _                            => 0.0m,   // None → 충격 0
        };

        /// <summary>자산군 잔존 배수 max(0, 1 − severity·β) 를 raw 로. 클램프로 전손 이하(음수) 차단.</summary>
        private static long SurvivalFactorRaw(ChaosScenario s, AssetClass c)
        {
            decimal f = 1m - Severity(s) * Beta(s, c);
            // ★적대적 리뷰 + 사용자 결정 — 음수 survival(>100% 손실)은 QMath.Notional 이 throw 하므로 0 클램프로 fail-closed.
            //   CryptoWinter×Crypto(0.55×2.0=1.10)가 실제 도달 경로 → 죽은 코드 아님(테스트로 0 클램프·무크래시 고정).
            if (f < 0m) f = 0m;
            return QMath.FromDecimal(f);
        }

        /// <summary>자산별 폭풍 후 가치를 호출자 버퍼에 기입(δ=0). 시뮬레이터에 폭풍-후 시작값으로 재투입 가능.</summary>
        public static bool ApplyStress(ReadOnlySpan<QMoney> present, ReadOnlySpan<AssetClass> classes,
                                       ChaosScenario scenario, Span<QMoney> stressedOut)
        {
            int n = present.Length;
            if (classes.Length != n || stressedOut.Length != n) return false;
            for (int i = 0; i < n; i++)
            {
                if (present[i].Raw < 0) return false;   // poison
                stressedOut[i] = new QMoney(QMath.Notional(present[i].Raw, SurvivalFactorRaw(scenario, classes[i]), roundUp: false));
            }
            return true;
        }

        /// <summary>폭풍 전/후 총가치 합산(δ=0). 낙폭 = (base − stressed)/base.</summary>
        public static void StressTotal(ReadOnlySpan<QMoney> present, ReadOnlySpan<AssetClass> classes,
                                       ChaosScenario scenario, out QMoney baseTotal, out QMoney stressedTotal)
        {
            long b = 0, st = 0;
            int n = Math.Min(present.Length, classes.Length);
            for (int i = 0; i < n; i++)
            {
                long v = present[i].Raw; if (v < 0) v = 0;   // 방어적: 음수(poison) 0 흡수 — 진단은 비권위 표시용(권위 경로 ApplyStress/Project는 거부)
                b  = QMath.Add(b, v);
                st = QMath.Add(st, QMath.Notional(v, SurvivalFactorRaw(scenario, classes[i]), roundUp: false));
            }
            baseTotal = new QMoney(b); stressedTotal = new QMoney(st);
        }
    }

    /// <summary>
    /// Mission 3 — 비대칭 진단. 집중도(최대 자산군 비중)·방어점수(최악 폭풍 생존율)·최악 시나리오를 숫자로 낸다.
    /// 순수 함수. 모든 폭풍을 돌려 가장 깊은 낙폭을 방어점수로 환산(100 − 최악낙폭%, 클램프).
    /// </summary>
    public static class PortfolioDiagnosis
    {
        private static readonly ChaosScenario[] AllStorms =
            { ChaosScenario.Gfc2008, ChaosScenario.RateSpike, ChaosScenario.InflationShock, ChaosScenario.CryptoWinter };

        public static DiagnosisReport Diagnose(ReadOnlySpan<QMoney> present, ReadOnlySpan<AssetClass> classes)
        {
            int n = Math.Min(present.Length, classes.Length);
            // 자산군별 합·총합 → 집중도.
            var classSum = new decimal[3]; decimal total = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal v = present[i].Raw < 0 ? 0m : present[i].ToDecimal();   // 방어적: 음수 poison 0 흡수(비권위 진단)
                int ci = (int)classes[i];
                if ((uint)ci < 3u) classSum[ci] += v;
                total += v;
            }
            decimal concentration = 0m; AssetClass topClass = AssetClass.DomesticStock;
            for (int c = 0; c < 3; c++)
            {
                decimal w = total > 0m ? classSum[c] / total * 100m : 0m;
                if (w > concentration) { concentration = w; topClass = (AssetClass)c; }
            }

            // 모든 폭풍 → 최악 낙폭.
            decimal worstDd = 0m; ChaosScenario worst = ChaosScenario.None;
            foreach (var s in AllStorms)
            {
                ChaosEngine.StressTotal(present, classes, s, out var bTot, out var sTot);
                decimal bd = bTot.ToDecimal();
                decimal dd = bd > 0m ? (bd - sTot.ToDecimal()) / bd * 100m : 0m;
                if (dd > worstDd) { worstDd = dd; worst = s; }
            }
            decimal defense = 100m - worstDd;
            // 방어적 클램프 — survival≥0(클램프) → worstDd∈[0,100] → defense∈[0,100]. <0/>100 분기는 표 확장 대비 유지.
            if (defense < 0m) defense = 0m; else if (defense > 100m) defense = 100m;

            return new DiagnosisReport(concentration, topClass, defense, worst, worstDd, total);
        }
    }

    /// <summary>
    /// Mission 3 — 리밸런싱 밸브. UI 스크롤바(basis point 정수)를 자산군 비중 배수 ShadowMutation 으로 정준화한다.
    /// 10000bp = 1.000× (변화 없음). 범위 [0, MaxLeverBp]. 이탈/음수는 fail-closed(거부) — 모호한 낙관 곡선 금지.
    /// </summary>
    public static class RebalanceLever
    {
        public const int NeutralBp = 10_000;    // 1.000×
        public const int MaxLeverBp = 50_000;   // 5.000× 상한

        /// <summary>스크롤바 bp → 자산군 비중 배수 변형. 범위 이탈은 false(밸브 fail-closed).</summary>
        public static bool TryScaleClass(AssetClass target, int leverBp, out ShadowMutation mutation)
        {
            mutation = ShadowMutation.None;
            if (leverBp < 0 || leverBp > MaxLeverBp) return false;   // fail-closed
            mutation = ShadowMutation.ScaleClass(target, leverBp / 10_000m);
            return true;
        }

        /// <summary>스크롤바 bp → 특정 심볼 비중 배수 변형. 범위 이탈은 false.</summary>
        public static bool TryScaleSymbol(string symbol, int leverBp, out ShadowMutation mutation)
        {
            mutation = ShadowMutation.None;
            if (string.IsNullOrEmpty(symbol) || leverBp < 0 || leverBp > MaxLeverBp) return false;
            mutation = ShadowMutation.ScaleSymbol(symbol, leverBp / 10_000m);
            return true;
        }
    }
}

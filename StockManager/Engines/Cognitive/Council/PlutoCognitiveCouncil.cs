// ============================================================================
// PlutoCognitiveCouncil — v12.18.88-DEV: 플루토 4대 인지 에이전트 위원회(국면 적응형 합의 가중치 · 리스크 통제관 비토 · 정수 산술 · δ=0)
//
//   에이전트(각 −100~+100 · 판독 없는 팩터 = 0 기여 — 날조 0):
//     TrendScout(추세)          = 기술 팩터(스퀴즈 · POC) + 종목 국면 상승(+20)/하락(−20) 추세.
//     MeanReversionHunter(역발상) = 주식: 밸류에이션 점수(저평가 +) − 기술 과열 ½ · 박스권(+10) / 코인: − 기술 과열 ½(밸류에이션 없음).
//     LiquidityPredator(호가 수급) = 미시구조 점수(CVD · 뎁스) + 매수벽(+15) + 매수 우위(+10) − 허매수 함정(−25) − 코인 스프레드 ≥ 50bp(−30).
//     ChiefRiskOfficer(위험 거버너) = −30 × 반대 논리 수 − VIX 초과(25 초과분 × 2) − 과거 약세/패닉 국면 유사(−30) − 파급 충격(−40) · 무위험 평온 = +10.
//   합의 가중치(W1+W2+W3+W4 = 10000bp): 39셀 표(인지 텐서와 같은 셀 번호 = PlutoCognitiveTensorLake.CellOf · 13행[기준선 + 12대 국면] × 3열[KRX · US · Upbit]) —
//     행 = 국면 전략(추세 추종 → TrendScout · 평균 회귀 → MeanReversionHunter · 악마의 변호인 → ChiefRiskOfficer) + 유동성 국면(→ LiquidityPredator) · 열 = Upbit 수급 +500.
//     그 위에 셀 학습 가중치 투영: 모멘텀 tilt% × 20bp(TrendScout ↔ MeanReversionHunter) · 엄격도 점 × 50bp(→ ChiefRiskOfficer) · 각 에이전트 하한 500bp · 합 10000 보정.
//   합의 점수 = Σ Wi × Si ÷ 10000(−100~+100).
//   비토(ChiefRiskOfficer 거부권 · 진입 강제 차단): VIX ≥ 35 · 파급 충격(10년물 급변 · 빅테크 급락 · BTC 변동성 급등) · 과거 패닉 국면 유사도 ≥ 60% · 종목 패닉 투매 국면.
//   ★δ=0: Weights/Convene 힙 할당 0(값 타입 · 정적 표 1회 구축). ★liveArmed:false — 추천 판정 입력뿐(주문 · 무장 경로 0) · QuantumVault/피어 0.
// ============================================================================
using System;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Screening;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분

namespace StockManager.Engines.Cognitive.Council
{
    public enum PlutoCouncilAgent : byte
    {
        TrendScout = 0,
        MeanReversionHunter = 1,
        LiquidityPredator = 2,
        ChiefRiskOfficer = 3,
    }

    [Flags]
    public enum PlutoVetoReason : byte
    {
        None = 0,
        VixSpike = 1,
        SpilloverShock = 2,
        PanicRegimeMatch = 4,
        PanicLiquidation = 8,
    }

    /// <summary>합의 가중치 4개(bp · 합 10000 · 값 타입).</summary>
    public readonly struct PlutoCouncilWeights
    {
        public readonly int TrendBp, ReversionBp, LiquidityBp, RiskBp;

        public PlutoCouncilWeights(int trendBp, int reversionBp, int liquidityBp, int riskBp)
        {
            TrendBp = trendBp; ReversionBp = reversionBp; LiquidityBp = liquidityBp; RiskBp = riskBp;
        }

        public int Sum => TrendBp + ReversionBp + LiquidityBp + RiskBp;

        public int Of(PlutoCouncilAgent a) => a switch
        {
            PlutoCouncilAgent.TrendScout => TrendBp,
            PlutoCouncilAgent.MeanReversionHunter => ReversionBp,
            PlutoCouncilAgent.LiquidityPredator => LiquidityBp,
            _ => RiskBp,
        };

        /// <summary>최대 가중 에이전트(동률 = 열거 순서 앞).</summary>
        public PlutoCouncilAgent Lead
        {
            get
            {
                var lead = PlutoCouncilAgent.TrendScout;
                int best = TrendBp;
                if (ReversionBp > best) { best = ReversionBp; lead = PlutoCouncilAgent.MeanReversionHunter; }
                if (LiquidityBp > best) { best = LiquidityBp; lead = PlutoCouncilAgent.LiquidityPredator; }
                if (RiskBp > best) lead = PlutoCouncilAgent.ChiefRiskOfficer;
                return lead;
            }
        }
    }

    /// <summary>거시 위험 입력(값 타입 · default = 판독 없음 → VIX/국면 비토 미발동). VixX100 &lt; 0 = 미판독.</summary>
    public readonly struct PlutoCouncilMacro
    {
        public readonly int VixX100;
        public readonly PlutoEpisodeKind RegimeKind;
        public readonly int RegimeSimilarityPct;
        public readonly PlutoSpilloverSignal Spillover;

        public PlutoCouncilMacro(int vixX100, PlutoEpisodeKind regimeKind, int regimeSimilarityPct, in PlutoSpilloverSignal spillover)
        {
            VixX100 = vixX100; RegimeKind = regimeKind; RegimeSimilarityPct = Math.Clamp(regimeSimilarityPct, 0, 100); Spillover = spillover;
        }

        public static PlutoCouncilMacro None => new(-1, PlutoEpisodeKind.Unknown, 0, default);
    }

    /// <summary>위원회 판정 1건(값 타입).</summary>
    public readonly struct PlutoCouncilVerdict
    {
        public readonly int TrendScore, ReversionScore, LiquidityScore, RiskScore, Consensus;
        public readonly PlutoCouncilWeights Weights;
        public readonly PlutoVetoReason Veto;
        public readonly int Cell;

        public PlutoCouncilVerdict(int trend, int reversion, int liquidity, int risk, int consensus, in PlutoCouncilWeights weights, PlutoVetoReason veto, int cell)
        {
            TrendScore = trend; ReversionScore = reversion; LiquidityScore = liquidity; RiskScore = risk; Consensus = consensus; Weights = weights; Veto = veto; Cell = cell;
        }

        public bool Vetoed => Veto != PlutoVetoReason.None;
        public bool IsConvened => Weights.Sum == PlutoCognitiveCouncil.TotalBp;

        public int ScoreOf(PlutoCouncilAgent a) => a switch
        {
            PlutoCouncilAgent.TrendScout => TrendScore,
            PlutoCouncilAgent.MeanReversionHunter => ReversionScore,
            PlutoCouncilAgent.LiquidityPredator => LiquidityScore,
            _ => RiskScore,
        };
    }

    public static class PlutoCognitiveCouncil
    {
        public const int Agents = 4, TotalBp = 10000, MinAgentBp = 500;
        public const int Cells = PlutoCognitiveTensorLake.Cells;
        public const int TiltBpPerPct = 20, StrictBpPerPt = 50;
        public const int VetoVixX100 = 3500, StressVixX100 = 2500, VetoPanicSimilarityPct = 60;
        public const int RiskPerThesis = 30, RegimeStress = 30, ShockStress = 40, CalmBonus = 10;

        private static readonly int[] s_base = BuildBase();

        /// <summary>셀 기본 가중치(학습 투영 전 · δ=0).</summary>
        public static PlutoCouncilWeights BaseOf(int cell)
        {
            if ((uint)cell >= Cells) cell = 0;
            int o = cell * Agents;
            return new PlutoCouncilWeights(s_base[o], s_base[o + 1], s_base[o + 2], s_base[o + 3]);
        }

        /// <summary>셀 합의 가중치(δ=0): 기본 표 + 학습 가중치 투영(중립/무결성 위반 = 기본 그대로) → 하한 500 · 합 10000.</summary>
        public static PlutoCouncilWeights Weights(PlutoAssetClass asset, int regimeId, in PlutoCognitiveWeights learned)
        {
            var b = BaseOf(PlutoCognitiveTensorLake.CellOf(asset, regimeId));
            if (!learned.IsWellFormed || learned.IsNeutral) return b;
            int t = b.TrendBp, r = b.ReversionBp, l = b.LiquidityBp, c = b.RiskBp;
            int tilt = learned.MomentumTiltPct * TiltBpPerPct;      // + = 추세 강화(역발상에서 이전)
            t += tilt; r -= tilt;
            int strict = learned.DevilStrictnessPts * StrictBpPerPt; // + = 리스크 통제관 강화(나머지 3인 균등 부담)
            c += strict; t -= strict / 3; r -= strict / 3; l -= strict - 2 * (strict / 3);
            return Normalize(t, r, l, c);
        }

        /// <summary>하한 500bp 클램프 후 합 10000 보정(잔차 = 최대 가중 에이전트 흡수 · δ=0).</summary>
        public static PlutoCouncilWeights Normalize(int t, int r, int l, int c)
        {
            t = Math.Max(MinAgentBp, t); r = Math.Max(MinAgentBp, r); l = Math.Max(MinAgentBp, l); c = Math.Max(MinAgentBp, c);
            long sum = (long)t + r + l + c;
            int nt = (int)(t * (long)TotalBp / sum), nr = (int)(r * (long)TotalBp / sum), nl = (int)(l * (long)TotalBp / sum), nc = (int)(c * (long)TotalBp / sum);
            nt = Math.Max(MinAgentBp, nt); nr = Math.Max(MinAgentBp, nr); nl = Math.Max(MinAgentBp, nl); nc = Math.Max(MinAgentBp, nc);
            int rest = TotalBp - (nt + nr + nl + nc);
            var w = new PlutoCouncilWeights(nt, nr, nl, nc);
            switch (w.Lead)
            {
                case PlutoCouncilAgent.TrendScout: nt += rest; break;
                case PlutoCouncilAgent.MeanReversionHunter: nr += rest; break;
                case PlutoCouncilAgent.LiquidityPredator: nl += rest; break;
                default: nc += rest; break;
            }
            return new PlutoCouncilWeights(nt, nr, nl, nc);
        }

        public static int TrendScout(in PlutoScreenInputs x)
        {
            int s = x.HasTechnical ? x.TechnicalScore : 0;
            if (x.Regime == MarketRegime.TrendingBull) s += 20;
            else if (x.Regime == MarketRegime.TrendingBear) s -= 20;
            return Math.Clamp(s, -100, 100);
        }

        public static int MeanReversionHunter(in PlutoScreenInputs x)
        {
            int s = x.HasTechnical ? -x.TechnicalScore / 2 : 0;
            if (x.Market != PlutoScreenMarket.Crypto)
            {
                if (x.HasValuation) s += x.ValuationScore;
                if (x.Regime == MarketRegime.ChoppyRange) s += 10;
            }
            return Math.Clamp(s, -100, 100);
        }

        public static int LiquidityPredator(in PlutoScreenInputs x)
        {
            int s = 0;
            if (x.HasMicro)
            {
                s = x.MicroScore;
                if (x.BidWall) s += 15;
                if (x.CvdBuyDominant) s += 10;
                if (x.DepthTrap) s -= 25;
            }
            if (x.Market == PlutoScreenMarket.Crypto && x.SpreadBps >= PlutoDiscoveryScorer.IlliquidSpreadBps) s -= 30;
            return Math.Clamp(s, -100, 100);
        }

        public static int ChiefRiskOfficer(in PlutoDiscoveryScore s, in PlutoCouncilMacro m)
        {
            int risk = -RiskPerThesis * s.RiskCount;
            if (m.VixX100 > StressVixX100) risk -= (m.VixX100 - StressVixX100) / 100 * 2;
            if (m.RegimeKind is PlutoEpisodeKind.Bear or PlutoEpisodeKind.Panic && m.RegimeSimilarityPct >= VetoPanicSimilarityPct) risk -= RegimeStress;
            if (m.Spillover.IsShock) risk -= ShockStress;
            if (risk == 0) risk = CalmBonus;
            return Math.Clamp(risk, -100, 100);
        }

        /// <summary>비토 사유(δ=0) — 하나라도 = 진입 강제 차단.</summary>
        public static PlutoVetoReason VetoOf(in PlutoScreenInputs x, in PlutoCouncilMacro m)
        {
            var v = PlutoVetoReason.None;
            if (m.VixX100 >= VetoVixX100) v |= PlutoVetoReason.VixSpike;
            if (m.Spillover.IsShock) v |= PlutoVetoReason.SpilloverShock;
            if (m.RegimeKind == PlutoEpisodeKind.Panic && m.RegimeSimilarityPct >= VetoPanicSimilarityPct) v |= PlutoVetoReason.PanicRegimeMatch;
            if (x.Regime == MarketRegime.PanicLiquidation) v |= PlutoVetoReason.PanicLiquidation;
            return v;
        }

        /// <summary>위원회 소집(δ=0): 4대 에이전트 점수 × 셀 합의 가중치 → 합의 점수 + 비토.</summary>
        public static PlutoCouncilVerdict Convene(in PlutoScreenInputs x, in PlutoDiscoveryScore s, in PlutoCouncilMacro m, int regimeId, in PlutoCognitiveWeights learned)
        {
            var asset = PlutoRecommendationEngine.AssetOf(x.Market);
            var w = Weights(asset, regimeId, in learned);
            int t = TrendScout(in x), r = MeanReversionHunter(in x), l = LiquidityPredator(in x), c = ChiefRiskOfficer(in s, in m);
            int consensus = (int)(((long)w.TrendBp * t + (long)w.ReversionBp * r + (long)w.LiquidityBp * l + (long)w.RiskBp * c) / TotalBp);
            return new PlutoCouncilVerdict(t, r, l, c, Math.Clamp(consensus, -100, 100), in w, VetoOf(in x, in m), PlutoCognitiveTensorLake.CellOf(asset, regimeId));
        }

        public static string AgentLabel(PlutoCouncilAgent a) => a switch
        {
            PlutoCouncilAgent.TrendScout => "TrendScout(추세)",
            PlutoCouncilAgent.MeanReversionHunter => "MeanReversionHunter(역발상)",
            PlutoCouncilAgent.LiquidityPredator => "LiquidityPredator(호가 수급)",
            _ => "ChiefRiskOfficer(위험 통제)",
        };

        public static string VetoText(PlutoVetoReason v)
        {
            if (v == PlutoVetoReason.None) return "";
            var sb = new System.Text.StringBuilder("⛔ 리스크 통제관 비토:");
            if ((v & PlutoVetoReason.VixSpike) != 0) sb.Append(" VIX ≥ 35");
            if ((v & PlutoVetoReason.SpilloverShock) != 0) sb.Append(" 교차 자산 충격");
            if ((v & PlutoVetoReason.PanicRegimeMatch) != 0) sb.Append(" 과거 패닉 국면 유사");
            if ((v & PlutoVetoReason.PanicLiquidation) != 0) sb.Append(" 패닉 투매 국면");
            return sb.ToString();
        }

        /// <summary>39셀 기본 표(구축 1회): 행 0 = 균등 · 행 1~12 = 국면 전략/유형 · 열 Upbit = 수급 +500(역발상 −500).</summary>
        private static int[] BuildBase()
        {
            var table = new int[Cells * Agents];
            for (int cell = 0; cell < Cells; cell++)
            {
                var (regimeId, asset) = PlutoCognitiveTensorLake.KeyOf(cell);
                int t = 2500, r = 2500, l = 2500, c = 2500;
                if (PlutoRegimeVectorLake.Get(regimeId) is { } cycle)
                {
                    switch (cycle.Strategy)
                    {
                        case PlutoRegimeStrategy.TrendFollow: t = 4000; r = 1500; l = 2500; c = 2000; break;
                        case PlutoRegimeStrategy.MeanReversion: t = 1500; r = 4000; l = 2500; c = 2000; break;
                        case PlutoRegimeStrategy.DevilsAdvocate: t = 1500; r = 2000; l = 2000; c = 4500; break;
                    }
                    if (cycle.Kind == PlutoEpisodeKind.Liquidity) { l += 1000; t -= 500; r -= 500; }
                }
                if (asset == PlutoAssetClass.Upbit) { l += 500; r -= 500; }
                var w = Normalize(t, r, l, c);
                int o = cell * Agents;
                table[o] = w.TrendBp; table[o + 1] = w.ReversionBp; table[o + 2] = w.LiquidityBp; table[o + 3] = w.RiskBp;
            }
            return table;
        }
    }
}

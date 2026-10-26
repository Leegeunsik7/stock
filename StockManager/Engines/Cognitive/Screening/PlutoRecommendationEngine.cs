// ============================================================================
// PlutoRecommendationEngine — v12.18.85-DEV: 레짐 적응형 추천 랭커(학습 가중치 투영 · Track B 콜드 · 스크리너 위임)
//
//   입력 = 유니버스 1회 판독(매트릭스 Live 셀 420종 + 아레나 보조) × 4대 팩터 점수(PlutoDiscoveryScorer) × 현재 학습 가중치(seqlock 스냅샷 값).
//   투영(Project · 값 타입 · δ=0):
//     ① 모멘텀 조정 = (기술 팩터 점수 − 중립 12점) × MomentumTiltPct ÷ 100 — 학습이 모멘텀을 강화(+)하면 추세 강한 종목 가산 · 축소(−)하면 감산(±6점 이내).
//     ② 악마의 변호인 감점 = DevilStrictnessPts × 반대 논리 수 ÷ 2 — 엄격도 상승(+) = 반대 논리 보유 종목 감점 · 완화(−) = 가점(반대 논리 0건 = 0).
//     ③ 적응 점수 = 기본 점수 + ① − ② (0~100 포화). 중립 가중치(학습 전) = 기본 점수와 완전 동일(기존 스크리너 동작 보존).
//   랭킹(Rank): 판독 전무(피드 미결선 · Insufficient) = 점수 산출 0 · 집계만(가짜 점수 0) · 반대 논리 ≥ 2 = 탈락 · 적응 점수 50점 미만 = 제외 →
//     적응 점수 내림차순(동점 = 반대 논리 적은 순 → 종목 오름차순 · 결정적) 상위 N.
//   [v12.18.86-DEV] 자산군 독립 투영(AssetWeights): 시장(KRX · US · 코인=Upbit)마다 36슬롯 인지 텐서 활성 국면 슬롯 → 자산 기준선 → 전역 순으로 해석한
//     전용 가중치를 그 시장 랭킹에만 투영(한 자산군 학습이 다른 자산군 추천 점수를 흔들지 않음 · 텐서 미결선/미보정 = 전역 그대로 = v12.18.85 동작).
//   [v12.18.88-DEV] 4대 인지 에이전트 위원회(PlutoCouncilInputs · 미결선 = 기존 동작 그대로): ⑤ 리스크 통제관 비토 = 탈락(CouncilVetoed 계수) ·
//     ⑥ KRX = 교차 자산 선행 파급 점수(LeadPts ±6) 적응 점수 가산 · ⑦ 합의 점수 · 프랙탈 켈리 권장 비중 = 추천에 첨부(켈리 Sized = 추천 수량도 켈리 비중 기준).
//   ★체결 핫패스 무간섭: 가중치는 호출자가 seqlock TryRead 1회로 넘긴 값 스냅샷만 사용(잠금 · 공유 쓰기 0). 결과는 불변 스냅샷 원자 교체로만 게시.
//   ★liveArmed:false — 추천 = 가상 원장 휴먼 티켓 보조 입력뿐(주문 · 무장 · 브로커 경로 0) · QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Engines.Cognitive.Council;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>학습 가중치 투영 결과(값 타입 · default = 미적용). MomentumPts = 모멘텀 조정(±) · DevilPts = 악마의 변호인 감점(+ = 감점 · − = 가점).</summary>
    public readonly struct PlutoAdaptiveAdjust : IEquatable<PlutoAdaptiveAdjust>
    {
        public readonly int MomentumPts, DevilPts;
        public readonly long Generation;
        /// <summary>[v12.18.88-DEV] 교차 자산 선행 파급 점수(KRX 전용 · ± · 위원회 미결선 = 0).</summary>
        public readonly int LeadPts;

        public PlutoAdaptiveAdjust(int momentumPts, int devilPts, long generation, int leadPts = 0)
        {
            MomentumPts = momentumPts; DevilPts = devilPts; Generation = generation; LeadPts = leadPts;
        }

        /// <summary>순 조정 점수(모멘텀 − 감점 + 선행 파급).</summary>
        public int Net => MomentumPts - DevilPts + LeadPts;
        public bool IsZero => MomentumPts == 0 && DevilPts == 0 && LeadPts == 0;

        public PlutoAdaptiveAdjust WithLead(int leadPts) => new(MomentumPts, DevilPts, Generation, leadPts);

        public bool Equals(PlutoAdaptiveAdjust other) => MomentumPts == other.MomentumPts && DevilPts == other.DevilPts && Generation == other.Generation && LeadPts == other.LeadPts;
        public override bool Equals(object? obj) => obj is PlutoAdaptiveAdjust o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(MomentumPts, DevilPts, Generation, LeadPts);
        public static bool operator ==(PlutoAdaptiveAdjust a, PlutoAdaptiveAdjust b) => a.Equals(b);
        public static bool operator !=(PlutoAdaptiveAdjust a, PlutoAdaptiveAdjust b) => !a.Equals(b);
    }

    public static class PlutoRecommendationEngine
    {
        /// <summary>팩터 점수 0(중립) 의 0~25점 환산값(PlutoDiscoveryScorer.Points(0)).</summary>
        public const int NeutralFactorPts = 12;
        public const int MaxScore = 100;

        /// <summary>[v12.18.86-DEV] 시장 → 텐서 자산군 열.</summary>
        public static PlutoAssetClass AssetOf(PlutoScreenMarket market) => market switch
        {
            PlutoScreenMarket.Us => PlutoAssetClass.Us,
            PlutoScreenMarket.Crypto => PlutoAssetClass.Upbit,
            _ => PlutoAssetClass.Krx,
        };

        /// <summary>[v12.18.86-DEV] 시장 전용 가중치(δ=0): 텐서 활성 국면 슬롯 → 자산 기준선 → 전역(텐서 null = 전역 그대로).</summary>
        public static PlutoCognitiveWeights AssetWeights(PlutoScreenMarket market, in PlutoCognitiveWeights global, PlutoCognitiveTensorLake? tensor)
        {
            var w = global;
            tensor?.ResolveActive(AssetOf(market), ref w);
            return w;
        }

        /// <summary>학습 가중치 → 종목 1건 조정(δ=0 · 순수 함수). 가중치 무결성 위반 = 미적용(중립).</summary>
        public static PlutoAdaptiveAdjust Project(in PlutoDiscoveryScore s, in PlutoCognitiveWeights w)
        {
            if (!w.IsWellFormed || w.IsNeutral) return new PlutoAdaptiveAdjust(0, 0, w.Generation);
            int momentum = (s.TechnicalPts - NeutralFactorPts) * w.MomentumTiltPct / 100;
            int devil = w.DevilStrictnessPts * s.RiskCount / 2;
            return new PlutoAdaptiveAdjust(momentum, devil, w.Generation);
        }

        /// <summary>적응 점수(0~100 포화).</summary>
        public static int AdaptiveTotal(in PlutoDiscoveryScore s, in PlutoAdaptiveAdjust a) => Math.Clamp(s.Total + a.Net, 0, MaxScore);

        /// <summary>
        /// 시장 1개 랭킹 → 상위 topN 추천(콜드 · 할당 허용). universe = 스크리너 스크래치 판독분(시장 불일치 · 무시세 = 유니버스 밖) ·
        /// capital = 추천 수량 기준 자본 · monitored = 감시 카탈로그 종목 수.
        /// </summary>
        public static PlutoRecommendation[] Rank(ReadOnlySpan<PlutoScreenInputs> universe, PlutoScreenMarket market, in PlutoScreenMacro macro,
            in PlutoCognitiveWeights weights, long capital, int monitored, int topN, out PlutoScreenTally tally)
            => Rank(universe, market, in macro, in weights, capital, monitored, topN, default, out tally);

        /// <summary>[v12.18.88-DEV] 위원회 결선 랭킹(council.Enabled false = 위 오버로드와 완전 동일).</summary>
        public static PlutoRecommendation[] Rank(ReadOnlySpan<PlutoScreenInputs> universe, PlutoScreenMarket market, in PlutoScreenMacro macro,
            in PlutoCognitiveWeights weights, long capital, int monitored, int topN, in PlutoCouncilInputs council, out PlutoScreenTally tally)
            => Rank(universe, market, in macro, in weights, capital, monitored, topN, in council, out tally, out _);

        /// <summary>
        /// [v12.18.90-DEV] 랭킹 + 비토 차단 목록(리스크 통제관 · 악마의 변호인 탈락 종목 · 차단 전 기본 점수 내림차순 → 종목 오름차순 · ≤ MaxVetoesPerMarket ·
        /// 사유 = 실측 수치 인용). 판독 전무(Insufficient) · 50점 미만 = 차단 목록 제외(비토가 아님).
        /// </summary>
        public static PlutoRecommendation[] Rank(ReadOnlySpan<PlutoScreenInputs> universe, PlutoScreenMarket market, in PlutoScreenMacro macro,
            in PlutoCognitiveWeights weights, long capital, int monitored, int topN, in PlutoCouncilInputs council, out PlutoScreenTally tally,
            out PlutoVetoedCandidate[] vetoes)
        {
            if (topN is < 1 or > PlutoRecommendationSnapshot.TopN) throw new ArgumentOutOfRangeException(nameof(topN));
            var picks = new List<Pick>(universe.Length);
            var blocked = new List<Blocked>();
            int scanned = 0, vetoed = 0, insufficient = 0, below = 0, councilVetoed = 0;
            int lead = council.Enabled && market == PlutoScreenMarket.Krx ? council.Macro.Spillover.LeadPts : 0;
            for (int i = 0; i < universe.Length; i++)
            {
                ref readonly var x = ref universe[i];
                if (x.Market != market || x.Symbol <= 0 || x.Price <= 0) continue;
                scanned++;
                var s = PlutoDiscoveryScorer.Score(in x, in macro);
                if (s.Insufficient) { insufficient++; continue; }     // 피드 판독 전무 = 점수 산출 0(가짜 추천 0)
                if (s.Vetoed) { vetoed++; blocked.Add(new Blocked(i, x.Symbol, s.Total, PlutoVetoSource.DevilsAdvocate, PlutoVetoReason.None, s.Risks)); continue; }
                PlutoCouncilVerdict verdict = default;
                if (council.Enabled)
                {
                    verdict = PlutoCognitiveCouncil.Convene(in x, in s, in council.Macro, council.RegimeId, in council.Learned);
                    if (verdict.Vetoed)   // 리스크 통제관 거부권 = 진입 강제 차단(점수 무관)
                    {
                        councilVetoed++;
                        blocked.Add(new Blocked(i, x.Symbol, s.Total, PlutoVetoSource.Council, verdict.Veto, s.Risks));
                        continue;
                    }
                }
                var a = Project(in s, in weights);
                if (lead != 0) a = a.WithLead(lead);
                int total = AdaptiveTotal(in s, in a);
                if (total < PlutoDiscoveryScorer.MinRecommendScore) { below++; continue; }
                picks.Add(new Pick(i, x.Symbol, s, a, total, verdict));
            }
            picks.Sort((p, q) =>
            {
                int c = q.Total.CompareTo(p.Total);
                if (c != 0) return c;
                c = p.Score.RiskCount.CompareTo(q.Score.RiskCount);
                return c != 0 ? c : p.Symbol.CompareTo(q.Symbol);
            });
            int take = Math.Min(picks.Count, topN);
            var result = new PlutoRecommendation[take];
            for (int r = 0; r < take; r++)
            {
                var p = picks[r];
                ref readonly var x = ref universe[p.Index];
                var kelly = council.Enabled ? council.KellyOf(x.Symbol) : default;
                long qty = kelly.IsSized
                    ? PlutoMarketScreener.RecommendedQtyBp(market, capital, kelly.WeightBp, x.Price)
                    : PlutoMarketScreener.RecommendedQty(market, capital, p.Total, x.Price);
                result[r] = new PlutoRecommendation(market, r + 1, x.Symbol, PlutoMarketScreener.CodeOf(market, x.Symbol), x.Price, p.Score,
                    Array.AsReadOnly(PlutoDiscoveryScorer.Reasons(in x, in macro, p.Score)), x.HasValuation, x.MarginOfSafetyBps, x.FairPrice, x.SeedProfile,
                    qty, PlutoDiscoveryScorer.RiskText(p.Score.Risks, market), p.Adjust)
                {
                    Council = p.Verdict,
                    Kelly = kelly,
                    CouncilWired = council.Enabled,
                };
            }
            tally = new PlutoScreenTally(scanned, vetoed, insufficient, below, Math.Max(monitored, scanned), picks.Count) { CouncilVetoed = councilVetoed };
            blocked.Sort(static (p, q) => p.BaseScore != q.BaseScore ? q.BaseScore.CompareTo(p.BaseScore) : p.Symbol.CompareTo(q.Symbol));
            vetoes = new PlutoVetoedCandidate[Math.Min(blocked.Count, PlutoRecommendationSnapshot.MaxVetoesPerMarket)];
            for (int v = 0; v < vetoes.Length; v++)
            {
                var b = blocked[v];
                ref readonly var x = ref universe[b.Index];
                string reason = b.Source == PlutoVetoSource.Council
                    ? PlutoCognitiveCouncil.VetoDetail(b.Veto, in council.Macro)
                    : "반대 논리 " + PlutoDiscoveryScorer.CountOf(b.Risks).ToString(System.Globalization.CultureInfo.InvariantCulture) + "건 중첩 — "
                      + PlutoDiscoveryScorer.RiskList(b.Risks, market) + " → 진입 차단";
                vetoes[v] = new PlutoVetoedCandidate(market, x.Symbol, PlutoMarketScreener.CodeOf(market, x.Symbol), x.Price, b.BaseScore, b.Source, b.Veto, reason);
            }
            return result;
        }

        /// <summary>학습 투영 근거 문구(콜드). 미적용 = "🧠 학습 가중치 투영: 중립(학습 전 · 기본 점수 그대로)".</summary>
        public static string RationaleText(in PlutoAdaptiveAdjust a)
        {
            if (a.IsZero) return "🧠 학습 가중치 투영: 중립(조정 0점 · 기본 점수 그대로)";
            return "🧠 학습 가중치 투영(세대 #" + a.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "): 모멘텀 "
                + Signed(a.MomentumPts) + "점 · 악마의 변호인 " + Signed(-a.DevilPts) + "점 → 순 " + Signed(a.Net) + "점";
        }

        private static string Signed(int v) => (v >= 0 ? "+" : "") + v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private readonly record struct Blocked(int Index, long Symbol, int BaseScore, PlutoVetoSource Source, PlutoVetoReason Veto, PlutoScreenRisk Risks);

        private readonly record struct Pick(int Index, long Symbol, PlutoDiscoveryScore Score, PlutoAdaptiveAdjust Adjust, int Total, PlutoCouncilVerdict Verdict);
    }
}

// ============================================================================
// StrategyEvolutionPipeline — v4.86.0 과제1/3: 자율 전략 진화 파이프라인 + 락-프리 FSM
//
//  알파 공장이 발굴한 MA 유전자(CartStrategy)를 전 자동 체력 검정에 투입:
//   [Gate1] WalkForward.Analyze — OOS(사후) 워크포워드 강건성(IsRobust) 검증.
//   [Gate2] StrategyTournament.Run — 기존 챔피언군과 1:N 리그 데스매치, 후보가 Rank #1(우월) 이어야.
//   [Gate3] AlphaEngine.Compute — 전략 에쿼티 vs 벤치마크 OLS β/공분산 분해, 진성 알파(TrueAlphaPct>0).
//   3중 게이트 통과 시 → AutonomousCartridgeSigner 서명 → PromotionGateway.Submit(PendingApproval).
//
//  ★liveArmed:false 불변(적대검증 핵심): 파이프라인은 gateway.Submit(=PendingApproval)까지만 수행한다.
//   Approve(인간 서명)·Activate(슬롯 스왑)를 '절대' 자율 호출하지 않는다. 실주문 도달은 여전히 인간 서명 +
//   실거래 이중키(ValveWired ∧ LiveArmingWired) + 점화 FSM 이 통제한다(StrategyActivationGate 로 명시 증명).
//
//  ── FSM(락-프리) ────────────────────────────────────────────────────────────
//   TournamentPipelineMode: Idle→Testing→Signing→Deployed→Idle. 동결/밸브 SUSPENDED → Paused 동기화.
//   Interlocked(long) 모드 + Volatile 문자열(유전자ID·챔피언명) — UI 락-프리 독출. 결정론(ReferenceSeries).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace StockManager.Common
{
    public enum TournamentPipelineMode : long { Idle = 0, Testing = 1, Signing = 2, Deployed = 3, Paused = 4 }

    public enum EvolutionOutcome
    {
        Promoted, RejectedRobust, RejectedTournament, RejectedAlpha, SigningTrashed, Paused, Error
    }

    /// <summary>
    /// ★전략 활성화 게이트(적대검증 명시) — 자율 승격 전략이 실 주문 엔진에 배선되려면 (1) 승격 상태 Active,
    /// (2) 실거래 무장(BrokerHub.IsLive&amp;IsArmed), (3) 무장 술어(LiveArmingGate/DispatchArming)가 동시 참이어야.
    /// 기본은 셋 다 false → 자율 승격만으로는 실주문 도달 확률이 수학적 0(liveArmed:false 증명).
    /// </summary>
    public static class StrategyActivationGate
    {
        public static bool CanRouteToLive(PromotionState state, bool brokerLiveArmed, Func<bool> armedPredicate)
        {
            if (state != PromotionState.Active) return false;   // 인간 서명 승인·활성화 전에는 불가
            if (!brokerLiveArmed)               return false;   // 실거래 이중키 미충족
            if (armedPredicate is null)         return false;
            try { return armedPredicate(); }                    // 무장 술어(예외 → 미무장)
            catch { return false; }
        }
    }

    public sealed class StrategyEvolutionPipeline
    {
        private readonly OrderAttestationService  _att;
        private readonly AutonomousAlphaFactory   _factory;    // 밸브(SUSPENDED) 동기화 소스
        private readonly AutonomousCartridgeSigner _signer;
        private readonly PromotionGateway         _gateway;
        private readonly Market                   _market;
        private readonly int                      _refSeed;
        private readonly int                      _refBars;

        private long   _mode = (long)TournamentPipelineMode.Idle;
        private string _currentGenomeId = "";
        private string _lastChampion    = "";
        private long   _tested, _promoted, _rejRobust, _rejTournament, _rejAlpha, _signTrashed;

        public StrategyEvolutionPipeline(
            OrderAttestationService att, AutonomousAlphaFactory factory,
            AutonomousCartridgeSigner signer, PromotionGateway gateway,
            Market market = Market.KRX_KOSPI, int refSeed = 42, int refBars = 300)
        {
            _att     = att     ?? throw new ArgumentNullException(nameof(att));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _signer  = signer  ?? throw new ArgumentNullException(nameof(signer));
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _market  = market;
            _refSeed = refSeed;
            _refBars = refBars < 120 ? 120 : refBars;
        }

        // ── 락-프리 관제 독출(UI) ──────────────────────────────────────────────
        public TournamentPipelineMode Mode        => (TournamentPipelineMode)Interlocked.Read(ref _mode);
        public string CurrentGenomeId             => Volatile.Read(ref _currentGenomeId);
        public string LastChampion                => Volatile.Read(ref _lastChampion);
        public long   Tested                      => Interlocked.Read(ref _tested);
        public long   Promoted                    => Interlocked.Read(ref _promoted);
        public long   RejectedRobust              => Interlocked.Read(ref _rejRobust);
        public long   RejectedTournament          => Interlocked.Read(ref _rejTournament);
        public long   RejectedAlpha               => Interlocked.Read(ref _rejAlpha);
        public long   SignTrashed                 => Interlocked.Read(ref _signTrashed);
        public double SigningSuccessRate          => _signer.SuccessRate;
        /// <summary>밸브 SUSPENDED 또는 EmergencyFreeze 로 파이프라인이 Paused 동기화 상태인지.</summary>
        public bool   IsPausedSync                => _att.Mode == GatingMode.EmergencyFreeze || _factory.IsSuspended;

        private void SetMode(TournamentPipelineMode m) => Interlocked.Exchange(ref _mode, (long)m);

        /// <summary>
        /// 후보 MA 유전자 1건을 3중 게이트 검정→서명→승격(Submit) 파이프에 태운다. 결정론(ReferenceSeries).
        /// champions: 기존 챔피언군(리그 대조 상대). ★Submit 은 PendingApproval 까지만 — 자율 활성화 없음.
        /// </summary>
        public EvolutionOutcome EvaluateAndPromote(CartStrategy candidate, IReadOnlyList<CartStrategy> champions)
        {
            if (candidate is null) throw new ArgumentNullException(nameof(candidate));

            // ★Paused 동기화 — 동결/밸브 SUSPENDED 면 검정 자체를 수행하지 않음(fail-closed·자원 절약).
            if (IsPausedSync) { SetMode(TournamentPipelineMode.Paused); return EvolutionOutcome.Paused; }

            Interlocked.Increment(ref _tested);
            Volatile.Write(ref _currentGenomeId, candidate.Name ?? "");
            SetMode(TournamentPipelineMode.Testing);
            try
            {
                var refSeries = CartridgeEngine.ReferenceSeries(_refSeed, _refBars);

                // [Gate1] 워크포워드 OOS 강건성.
                var wf = WalkForward.Analyze("EVO", candidate.Name ?? "cand", refSeries, _market);
                if (!wf.IsRobust) { Interlocked.Increment(ref _rejRobust); SetMode(TournamentPipelineMode.Idle); return EvolutionOutcome.RejectedRobust; }

                // [Gate2] 토너먼트 리그 — 후보가 챔피언군 대비 Rank #1 이어야.
                var entrants = new List<Entrant>(1 + (champions?.Count ?? 0))
                {
                    new(candidate.Name ?? "cand", "evolution", EntryKind.MaCross, candidate.Short, candidate.Long, -1, null)
                };
                if (champions is not null)
                    foreach (var ch in champions)
                        entrants.Add(new Entrant(ch.Name ?? "champ", "champion", EntryKind.MaCross, ch.Short, ch.Long, -1, null));
                var league = StrategyTournament.Run(entrants, refSeries, _market);
                var candRow = league.Rows.FirstOrDefault(r => r.Source == "evolution" && r.Name == (candidate.Name ?? "cand"));
                if (candRow is null || !candRow.Valid || candRow.Rank != 1)
                { Interlocked.Increment(ref _rejTournament); SetMode(TournamentPipelineMode.Idle); return EvolutionOutcome.RejectedTournament; }

                // [Gate3] 진성 알파 — 전략 에쿼티 vs 벤치마크 OLS β/공분산 분해(AlphaEngine).
                var bt = BacktestEngine.RunMaCross(refSeries, _market, candidate.Short, candidate.Long);
                var portfolio = BuildEquityCandles(refSeries, bt.EquityCurve);
                var alpha = AlphaEngine.Compute("EVO", portfolio, refSeries, "REF");
                if (!alpha.IsValid || alpha.TrueAlphaPct <= 0.0)
                { Interlocked.Increment(ref _rejAlpha); SetMode(TournamentPipelineMode.Idle); return EvolutionOutcome.RejectedAlpha; }

                // 서명(자율 ECDsa·금고·zero-alloc). 실패 시 폐기.
                SetMode(TournamentPipelineMode.Signing);
                var so = _signer.BuildAndSign($"EVO-{candidate.Name}", "자율 진화 승격 후보", new List<CartStrategy> { candidate });
                if (!so.IsSigned)
                { Interlocked.Increment(ref _signTrashed); SetMode(TournamentPipelineMode.Idle); return EvolutionOutcome.SigningTrashed; }

                // 승격 제출 — ★PendingApproval 까지만(인간 서명 전까지 실주문 도달 불가). 자율 Approve/Activate 없음.
                SetMode(TournamentPipelineMode.Deployed);
                var spec = StrategyCompiler.FromMaCross(candidate.Short, candidate.Long);
                _gateway.Submit(spec, candRow.Score);   // fitness = 위험조정 리그 점수(mean−λ·std)
                Volatile.Write(ref _lastChampion, candidate.Name ?? "");
                Interlocked.Increment(ref _promoted);
                SetMode(TournamentPipelineMode.Idle);
                return EvolutionOutcome.Promoted;
            }
            catch
            {
                SetMode(TournamentPipelineMode.Idle);
                return EvolutionOutcome.Error;   // 결함은 조용히 Idle 복귀(다음 후보 계속) — 폭주·부분상태 방지
            }
        }

        // 전략 에쿼티 곡선(List&lt;decimal&gt;)을 벤치마크 날짜에 정렬한 Candle 로 래핑(AlphaEngine 입력용).
        // ★날짜 정합(적대검증 교정): RunMaCross 에쿼티는 '워밍업(longP+1) 이후 봉'부터 시작하므로, 벤치마크의
        //   '꼬리'에 정렬한다(equity[last] ↔ bench[last]). 머리(bench[0])에 정렬하면 포트폴리오 수익률이
        //   ~longP+1 봉 이전 시장 수익률과 회귀되어 β/공분산이 위상차로 오염된다. 오프셋을 길이차로 도출(하드코딩 X).
        private static List<Candle> BuildEquityCandles(List<Candle> bench, List<decimal> equity)
        {
            int eqCount = equity?.Count ?? 0;
            if (eqCount == 0 || bench.Count == 0) return new List<Candle>(0);
            int offset = bench.Count - eqCount;
            if (offset < 0) offset = 0;                                  // 방어(비정상 길이)
            int n = Math.Min(eqCount, bench.Count - offset);
            var list = new List<Candle>(n);
            for (int i = 0; i < n; i++)
            {
                decimal e = equity![i] <= 0m ? 0.01m : equity[i];       // 비양수 방어(로그수익 안전)
                var d = bench[offset + i].Date;                         // 에쿼티 곡선을 실제 물리 봉 날짜에 정렬
                list.Add(new Candle { Date = d, Open = e, High = e, Low = e, Close = e, Volume = 1 });
            }
            return list;
        }
    }
}

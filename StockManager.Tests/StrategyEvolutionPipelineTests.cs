// ============================================================================
// StrategyEvolutionPipelineTests — v4.86.0 과제1/3: 진화 파이프라인 FSM + liveArmed 활성화 게이트
//
//  §1 활성화 게이트(5) — liveArmed:false 증명(기본 false·전 조건 충족 시에만 true).
//  §2 파이프라인(5)     — Paused 동기화(동결/밸브)·자율 미활성 불변·모드 정착·카운터·텔레메트리.
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class StrategyEvolutionPipelineTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;

        public StrategyEvolutionPipelineTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"evo_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
        }
        public void Dispose() { _db.Dispose(); try { File.Delete(_dbPath); } catch { } }

        private (StrategyEvolutionPipeline pipe, AutonomousAlphaFactory f, PromotionGateway gw)
            Build(OrderAttestationService att)
        {
            var f = new AutonomousAlphaFactory(_db, att);        // 미시작(스레드 없음)
            var vault = new SecurityAssetVault(_db, att, () => false);
            var signer = new AutonomousCartridgeSigner(_db, vault, att);
            var gw = new PromotionGateway(_db, att);
            var pipe = new StrategyEvolutionPipeline(att, f, signer, gw);
            return (pipe, f, gw);
        }

        private static CartStrategy Candidate() => CartridgeEngine.Attest("EVO-5-20", 5, 20, 5020);

        // ── §1 전략 활성화 게이트(liveArmed:false 증명) ───────────────────────

        [Fact]
        public void ActivationGate_DefaultFalse_MathematicalZero()
        {
            // 기본: 승격 안 됨(Idle) + 미무장 + 술어 false → 실주문 배선 불가.
            Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.Idle, false, () => false));
            Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.Active, false, () => false)); // 이중키 미충족
            Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.Active, true, () => false));  // 술어 false
            Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.PendingApproval, true, () => true)); // 미승인
        }

        [Fact]
        public void ActivationGate_AllTrue_Routes()
            => Assert.True(StrategyActivationGate.CanRouteToLive(PromotionState.Active, true, () => true));

        [Fact]
        public void ActivationGate_PredicateThrows_FailClosed()
            => Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.Active, true, () => throw new InvalidOperationException()));

        [Fact]
        public void ActivationGate_NullPredicate_FailClosed()
            => Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.Active, true, null!));

        // ── §2 파이프라인 ────────────────────────────────────────────────────

        [Fact]
        public void Frozen_PausesSync_NoWork()
        {
            var att = new OrderAttestationService();
            var (pipe, _, gw) = Build(att);
            att.ForceFreeze("동결");
            var outcome = pipe.EvaluateAndPromote(Candidate(), Array.Empty<CartStrategy>());
            Assert.Equal(EvolutionOutcome.Paused, outcome);
            Assert.Equal(TournamentPipelineMode.Paused, pipe.Mode);
            Assert.True(pipe.IsPausedSync);
            Assert.Equal(0L, pipe.Tested);   // 검정 자체 미수행
            Assert.NotEqual(PromotionState.Active, gw.State);
        }

        [Fact]
        public void ValveSuspended_PausesSync()
        {
            var att = new OrderAttestationService();
            var (pipe, f, _) = Build(att);
            f.Suspend();   // 밸브 트립
            var outcome = pipe.EvaluateAndPromote(Candidate(), Array.Empty<CartStrategy>());
            Assert.Equal(EvolutionOutcome.Paused, outcome);
            Assert.Equal(TournamentPipelineMode.Paused, pipe.Mode);
        }

        [Fact]
        public void NonPaused_NeverAutoActivates_AndSettles()
        {
            var att = new OrderAttestationService();
            var (pipe, _, gw) = Build(att);
            var outcome = pipe.EvaluateAndPromote(Candidate(), Array.Empty<CartStrategy>());

            // ★liveArmed:false 핵심 불변: 결과와 무관하게 자율 Activate 없음(PendingApproval 이하).
            Assert.NotEqual(PromotionState.Active, gw.State);
            Assert.NotEqual(PromotionState.Approved, gw.State);
            Assert.Equal(1L, pipe.Tested);
            Assert.Equal(TournamentPipelineMode.Idle, pipe.Mode);   // 비-Paused 경로는 Idle 로 정착
            // Promoted 면 챔피언 기록 + Submit(PendingApproval)까지.
            if (outcome == EvolutionOutcome.Promoted)
            {
                Assert.Equal(1L, pipe.Promoted);
                Assert.Equal("EVO-5-20", pipe.LastChampion);
                Assert.Equal(PromotionState.PendingApproval, gw.State);
            }
        }

        [Fact]
        public void NullCandidate_Throws()
        {
            var att = new OrderAttestationService();
            var (pipe, _, _) = Build(att);
            Assert.Throws<ArgumentNullException>(() => pipe.EvaluateAndPromote(null!, Array.Empty<CartStrategy>()));
        }

        [Fact]
        public void RingTelemetry_PipelineFields_And_PausedName()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0,
                alphaPipelineMode: (long)TournamentPipelineMode.Paused, alphaPromoted: 3, alphaSigningSuccessRate: 0.75);
            Assert.True(t.AlphaPipelinePaused);
            Assert.Equal("Paused", t.AlphaPipelineModeName);
            Assert.Equal(3L, t.AlphaPromoted);
            Assert.Equal(0.75, t.AlphaSigningSuccessRate, precision: 6);
        }

        [Fact]
        public void Publisher_ReflectsPipelineMode()
        {
            var att = new OrderAttestationService();
            var (pipe, f, _) = Build(att);
            f.Suspend();
            pipe.EvaluateAndPromote(Candidate(), Array.Empty<CartStrategy>());   // → Paused
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L, null, null, pipe);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.Equal((long)TournamentPipelineMode.Paused, snap.AlphaPipelineMode);
            Assert.True(snap.AlphaPipelinePaused);
        }
    }
}

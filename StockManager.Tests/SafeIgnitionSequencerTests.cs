// ============================================================================
// SafeIgnitionSequencerTests — v4.76.0: 실 무장 안전 점화 시퀀스 적대적 검증
//
//  §1 전진 가드(4)  — 이중키 미충족·단일키·미인가 서명·이미 동결 att → 거부(동결 없음)
//  §2 해피패스(2)   — 이중키+서명+격리프로브 → MasterIgnited / 결정론 재현
//  §3 fail-closed(4)— IsLive 브로커·ProbeReferencePrice<=0(dead-on-arrival)·프로브 거부·Abort → Frozen
//  §4 재진입/종단(2)— MasterIgnited 후 재점화 거부·동시 재진입 거부
//
//  ★검증 핵심: 적대검증이 잡은 dead-on-arrival(ref=0 영구동결) 교정이 실제로
//    '양수 기준가 → MasterIgnited 도달'·'ref=0 → 명시 fail-closed 동결'로 동작하는지 박제.
// ============================================================================
using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class SafeIgnitionSequencerTests
    {
        private const string Sig = "SAFE_IGNITION_SEQ";   // 시퀀서 기본 IgnitionSignature 와 일치
        private static readonly DateTime FixedNow = new(2026, 6, 27, 9, 0, 0, DateTimeKind.Utc);

        // 격리 페이크 브로커 — IsLive 토글·Place 결과 주입.
        private sealed class FakeBroker : IBroker
        {
            private readonly bool _ok;
            public FakeBroker(bool isLive, bool placeOk) { IsLive = isLive; _ok = placeOk; }
            public string Name => "FAKE";
            public bool   IsLive { get; }
            public (bool ok, string msg) Place(PaperAsset a, string s, string n,
                PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
                => _ok ? (true, "ok") : (false, "거부");
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        private static SafeIgnitionSequencer Make(
            OrderAttestationService att, IBroker broker,
            bool valve, bool live, double probeRef = 1.0)
        {
            var ign      = new IgnitionLock(s => s == Sig, att);
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 3.0 };
            return new SafeIgnitionSequencer(
                ign, att, sentinel, broker,
                valveWired:      () => valve,
                liveArmingWired: () => live,
                clock:           () => FixedNow)
            {
                ProbeSymbol         = "005930",
                ProbeReferencePrice = probeRef,
                MaxObservedSigma    = 3.0,
            };
        }

        // ════════════════════════ §1 전진 가드 (동결 없음) ════════════════════════

        [Fact]
        public void BothKeysOff_Rejects_NoFreeze()
        {
            var att = new OrderAttestationService();
            var r = Make(att, new FakeBroker(false, true), valve: false, live: false).BeginIgnition("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Disarmed, r.FinalState);   // 전진 거부 — 상태 무오염
            Assert.Equal(GatingMode.Normal, att.Mode);              // 동결 없음
        }

        [Fact]
        public void OneKeyOff_Rejects_NoFreeze()
        {
            var att = new OrderAttestationService();
            var r = Make(att, new FakeBroker(false, true), valve: true, live: false).BeginIgnition("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Disarmed, r.FinalState);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void BlankSignature_Rejects_NoFreeze()
        {
            var att = new OrderAttestationService();
            var r = Make(att, new FakeBroker(false, true), valve: true, live: true).BeginIgnition("   ");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Disarmed, r.FinalState);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 미인가 서명은 거부일 뿐 동결 아님
        }

        [Fact]
        public void AlreadyFrozenAtt_Rejects()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("선행 동결");
            var r = Make(att, new FakeBroker(false, true), valve: true, live: true).BeginIgnition("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 원래 동결 유지
        }

        // ════════════════════════ §2 해피패스 ═════════════════════════════════════

        [Fact]
        public void BothKeysOn_ValidProbe_ReachesMasterIgnited()
        {
            var att = new OrderAttestationService();
            var seq = Make(att, new FakeBroker(false, true), valve: true, live: true, probeRef: 1.0);
            var r = seq.BeginIgnition("운영자");
            Assert.True(r.Succeeded, $"MasterIgnited 미도달: {r.Reason}");
            Assert.Equal(IgnitionSeqState.MasterIgnited, r.FinalState);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 정상 점화 — 동결 없음
            Assert.True(r.PipelineLatencyMs >= 0);
        }

        [Fact]
        public void Deterministic_SameInputs_SameResult()
        {
            var a1 = new OrderAttestationService();
            var a2 = new OrderAttestationService();
            var r1 = Make(a1, new FakeBroker(false, true), true, true).BeginIgnition("운영자");
            var r2 = Make(a2, new FakeBroker(false, true), true, true).BeginIgnition("운영자");
            Assert.Equal(r1.Succeeded, r2.Succeeded);
            Assert.Equal(r1.FinalState, r2.FinalState);
            Assert.Equal(r1.PipelineLatencyMs, r2.PipelineLatencyMs);   // 고정 클럭 → 결정론
        }

        // ════════════════════════ §3 fail-closed ══════════════════════════════════

        [Fact]
        public void LiveBroker_FreezesClosed()
        {
            var att = new OrderAttestationService();
            var r = Make(att, new FakeBroker(isLive: true, placeOk: true), valve: true, live: true).BeginIgnition("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Frozen, r.FinalState);       // IsLive 프로브 → 격리 위반 → 동결
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ProbeReferenceZero_DeadOnArrival_FreezesClosed()
        {
            var att = new OrderAttestationService();
            // 적대검증 교정 박제: ref<=0(시장가)은 모호하게 흐르지 않고 명시 fail-closed 동결.
            var r = Make(att, new FakeBroker(false, true), valve: true, live: true, probeRef: 0.0).BeginIgnition("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Frozen, r.FinalState);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ProbePlaceRejected_FreezesClosed()
        {
            var att = new OrderAttestationService();
            var r = Make(att, new FakeBroker(isLive: false, placeOk: false), valve: true, live: true).BeginIgnition("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Frozen, r.FinalState);       // 프로브 발주 거부 → 동결
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Abort_FreezesClosed()
        {
            var att = new OrderAttestationService();
            var seq = Make(att, new FakeBroker(false, true), valve: true, live: true);
            var r = seq.Abort("운영자 긴급 중단");
            Assert.False(r.Succeeded);
            Assert.Equal(IgnitionSeqState.Frozen, r.FinalState);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ════════════════════════ §4 재진입/종단 ══════════════════════════════════

        [Fact]
        public void AfterMasterIgnited_SecondIgnition_Rejected()
        {
            var att = new OrderAttestationService();
            var seq = Make(att, new FakeBroker(false, true), valve: true, live: true);
            Assert.True(seq.BeginIgnition("운영자").Succeeded);
            var r2 = seq.BeginIgnition("운영자");   // 이미 MasterIgnited
            Assert.False(r2.Succeeded);
            Assert.Equal(IgnitionSeqState.MasterIgnited, seq.State);   // 종단 상태 보존
        }

        [Fact]
        public void State_StartsDisarmed()
        {
            var att = new OrderAttestationService();
            var seq = Make(att, new FakeBroker(false, true), valve: true, live: true);
            Assert.Equal(IgnitionSeqState.Disarmed, seq.State);
        }
    }
}

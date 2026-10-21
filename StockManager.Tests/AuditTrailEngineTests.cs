// ============================================================================
// AuditTrailEngineTests — v12.2.0 Dynamic Risk Interlock & Cryptographic Forensic Audit Trail 적대검증
//   Section 2: 무장(Arm)/해제(Disarm)/킬스위치 격발/리스크 위반 이벤트를 SHA-256 해시 체인(Append-Only)으로
//     기록 → 위변조 시도 감지 시 VerifyIntegrity()=false(100% Veto) → 상위 부팅/무장 파이프라인 Fail-Closed 중단.
//   Think Beyond: ZeroAllocRingBufferedForensicAuditStream(슬롯별 PrevHash 보존 → 랩된 링도 지역 연쇄 재검증).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class AuditTrailEngineTests
    {
        private readonly ITestOutputHelper _out;
        public AuditTrailEngineTests(ITestOutputHelper o) => _out = o;

        // ── §1 단일 Append — 체인 구성 및 무결성 통과 ───────────────────────────
        [Fact]
        public void Append_Single_VerifiesIntegrity()
        {
            var s = new ZeroAllocForensicAuditStream(16);
            long seq = s.Append(AuditEventKind.Arm, payloadTag: 42, timestampTicks: 1000);
            Assert.Equal(1, seq);
            Assert.Equal(1, s.WriteHead);
            Assert.True(s.VerifyIntegrity());
        }

        // ── §2 연쇄 다중 Append — 순서·필드 정합 ────────────────────────────────
        [Fact]
        public void Append_Multiple_ChainVerifies_And_FieldsReadBack()
        {
            var s = new ZeroAllocForensicAuditStream(32);
            s.Append(AuditEventKind.Arm, 1, 100);
            s.Append(AuditEventKind.Disarm, 2, 200);
            s.Append(AuditEventKind.RiskViolation, 3, 300);
            s.Append(AuditEventKind.KillSwitch, 4, 400);

            Assert.True(s.VerifyIntegrity());

            var link2 = s.ReadLink(2);
            Assert.True(link2.Live);
            Assert.Equal(AuditEventKind.Disarm, link2.Kind);
            Assert.Equal(2, link2.PayloadTag);
            Assert.Equal(200, link2.TimestampTicks);
        }

        // ── §3 결정론 — 동일 입력 시퀀스는 두 독립 스트림에서 동일 체인을 산출 ────
        [Fact]
        public void Append_IsDeterministic_AcrossIndependentInstances()
        {
            var a = new ZeroAllocForensicAuditStream(16);
            var b = new ZeroAllocForensicAuditStream(16);
            (AuditEventKind kind, long tag, long ts)[] events =
            {
                (AuditEventKind.Arm, 1, 10), (AuditEventKind.RiskViolation, 99, 20),
                (AuditEventKind.Disarm, 0, 30),
            };
            foreach (var e in events) { a.Append(e.kind, e.tag, e.ts); b.Append(e.kind, e.tag, e.ts); }

            for (long seq = 1; seq <= 3; seq++)
            {
                var la = a.ReadLink(seq); var lb = b.ReadLink(seq);
                Assert.Equal(la.Kind, lb.Kind);
                Assert.Equal(la.PayloadTag, lb.PayloadTag);
                Assert.Equal(la.TimestampTicks, lb.TimestampTicks);
            }
        }

        // ── §4 위변조 감지 — 저장된 해시 1비트 변조 → VerifyIntegrity=false(100% Veto) ──
        [Fact]
        public void Tamper_SingleByteFlip_DetectedByVerifyIntegrity()
        {
            var s = new ZeroAllocForensicAuditStream(16);
            s.Append(AuditEventKind.Arm, 1, 100);
            s.Append(AuditEventKind.Disarm, 2, 200);
            s.Append(AuditEventKind.RiskViolation, 3, 300);
            Assert.True(s.VerifyIntegrity());   // 위변조 전 — 정상

            s.__AdversarialCorruptForTest(sequence: 2, byteOffset: 5);   // 중간 레코드 해시 1비트 변조

            Assert.False(s.VerifyIntegrity());   // ★위변조 감지 — Veto
            // 부팅/무장 파이프라인은 이 시점에 Fail-Closed 로 중단해야 한다(호출부 계약 — 본 테스트는 감지 자체를 실증).
        }

        // ── §5 위변조 후 재-Append 는 여전히 탐지된 위변조를 남김(sticky 아님 — 매 호출 재계산) ──
        [Fact]
        public void Tamper_PersistsAcrossSubsequentAppends()
        {
            var s = new ZeroAllocForensicAuditStream(16);
            s.Append(AuditEventKind.Arm, 1, 100);
            s.__AdversarialCorruptForTest(sequence: 1, byteOffset: 0);
            Assert.False(s.VerifyIntegrity());

            s.Append(AuditEventKind.Disarm, 2, 200);   // 위변조 이후에도 신규 이벤트는 계속 기록 가능(append-only)
            Assert.False(s.VerifyIntegrity());         // 그러나 과거 위변조는 여전히 탐지됨
        }

        // ── §6 링 랩(wrap) — 용량 초과 후에도 살아있는 구간 지역 연쇄 재검증 성공 ───
        [Fact]
        public void RingWrap_BeyondCapacity_LiveWindowStillVerifies()
        {
            var s = new ZeroAllocForensicAuditStream(8);
            for (int i = 0; i < 50; i++) s.Append(AuditEventKind.RiskViolation, i, i * 10);   // 50 >> 용량 8 → 다중 랩

            Assert.Equal(50, s.WriteHead);
            Assert.True(s.VerifyIntegrity());

            // 이미 랩되어 사라진 시퀀스는 Live=false.
            var evicted = s.ReadLink(10);
            Assert.False(evicted.Live);
            var live = s.ReadLink(50);
            Assert.True(live.Live);
            Assert.Equal(49, live.PayloadTag);   // i=49 → payload=49(0-based 루프)
        }

        // ── §7 링 랩 후 위변조도 정확히 탐지(랩 이후 슬롯 좌표계 재사용 확인) ──────
        [Fact]
        public void RingWrap_TamperOnLiveSlot_StillDetected()
        {
            var s = new ZeroAllocForensicAuditStream(8);
            for (int i = 0; i < 20; i++) s.Append(AuditEventKind.Arm, i, i);
            Assert.True(s.VerifyIntegrity());

            s.__AdversarialCorruptForTest(sequence: 18, byteOffset: 3);   // 살아있는 구간(13..20) 내부 변조
            Assert.False(s.VerifyIntegrity());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §8 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class AuditTrailEngineConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public AuditTrailEngineConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly ZeroAllocForensicAuditStream _hotStream = new(1024);
        private static long _hotSeq;
        private static void AppendHot()
        {
            long s = Interlocked.Increment(ref _hotSeq);
            _hotStream.Append(AuditEventKind.RiskViolation, s, s);
        }

        [Fact]
        public void Append_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) AppendHot();
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 20_000; i++) AppendHot();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            _out.WriteLine($"[AuditStream] Append alloc={alloc}B over 20k (SHA256.HashData 정적 Span 오버로드)");
            Assert.Equal(0L, alloc);   // ★SHA-256 해시 체인 갱신 힙 0(stackalloc + Pinned 배열 직접 기록)
        }

        [Fact]
        public void Append_HighVolume_ChainStaysVerifiable()
        {
            var s = new ZeroAllocForensicAuditStream(256);
            for (int i = 0; i < 5000; i++) s.Append((AuditEventKind)(i % 4), i, i);
            Assert.True(s.VerifyIntegrity());
        }
    }
}

// ============================================================================
// Phase7PreflightAuditTests — v7.0.0 적대적 검증
//   RioHardwareProbe 진단 일관성 · CausalAuditVault 결정론 해시체인 · δ=0 기록 · 변조 감지(CORRUPTED)
//   · 트립 포렌식 봉인 비가역(자동복구 불가) · Append-Only 무-wrap.
// ============================================================================
using System;
using System.Reflection;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase7PreflightAuditTests
    {
        // ── RioHardwareProbe: 진단 리포트 내적 일관성(RIO 지원 여부는 환경 의존이라 단언 회피) ──
        [Fact]
        public void HardwareProbe_ReturnsConsistentReport()
        {
            var probe = new RioHardwareProbe();
            var r = probe.Probe();   // 예외 없이 완주
            Assert.True(r.QpcFrequency > 0, "QPC 주파수 양수");
            Assert.True(r.QpcResolutionNanos > 0, "QPC 분해능 양수");
            Assert.True(r.AdapterCount >= 0);
            Assert.True(r.UpAdapterCount >= 0 && r.UpAdapterCount <= r.AdapterCount);
            Assert.Equal(!r.RioSupported, r.RecommendSafeFallback);   // ★안심강등 권고 = RIO 미지원과 동치
            Assert.InRange(r.ProbeCode, 0, 3);
            if (r.RioSupported) { Assert.Equal(0, r.ProbeCode); Assert.True(r.MaxReceiveBufferBytes >= 0); }
        }

        [Fact]
        public void HardwareProbe_Idempotent()
        {
            var probe = new RioHardwareProbe();
            var a = probe.Probe();
            var b = probe.Probe();
            Assert.Equal(a.RioSupported, b.RioSupported);              // 반복 프로브 일관
            Assert.Equal(a.RecommendSafeFallback, b.RecommendSafeFallback);
        }

        // ── CausalAuditVault: SHA-256 해시체인 결정론 + INTACT 검증 ──
        [Fact]
        public void Vault_AppendChain_VerifiesIntact_Deterministic()
        {
            var v1 = new CausalAuditVault(1024);
            var v2 = new CausalAuditVault(1024);
            for (int i = 0; i < 100; i++)
            {
                Assert.True(v1.TryAppend(1000 + i, i, i * 3L));
                Assert.True(v2.TryAppend(1000 + i, i, i * 3L));
            }
            Assert.Equal(100L, v1.Count);
            Assert.Equal(LedgerIntegrity.Intact, v1.Verify());
            Assert.Equal(v1.HeadHash8, v2.HeadHash8);                 // 동일 입력 → 동일 사슬(결정론)
            Assert.Equal(v1.CurrentHashHex(), v2.CurrentHashHex());
        }

        // ── ★기록 루프 δ=0(초당 수만 레코드에도 GC 가비지 0바이트) ──
        [Fact]
        public void Vault_Append_ZeroAllocation_HotPath()
        {
            var v = new CausalAuditVault(400_000);
            for (int i = 0; i < 20_000; i++) v.TryAppend(i, i, i);    // 워밍업
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++) v.TryAppend(i, i, i);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, delta);   // SHA256.HashData 무할당 정적 + 사전할당 스팬 재사용
        }

        // ── ★변조 감지: 중간 레코드 1비트 위조 → 해시체인 불일치(CORRUPTED) ──
        [Fact]
        public void Vault_Tamper_DetectedCorrupted()
        {
            var v = new CausalAuditVault(64);
            for (int i = 0; i < 20; i++) v.TryAppend(1000 + i, i, i);
            Assert.Equal(LedgerIntegrity.Intact, v.Verify());

            // 저장된 레코드값을 리플렉션으로 변조(적대적 위조 시뮬레이션).
            var qpcField = typeof(CausalAuditVault).GetField("_qpc", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var qpc = (long[])qpcField.GetValue(v)!;
            qpc[5] ^= 0x1L;   // 1비트 변조

            Assert.Equal(LedgerIntegrity.Corrupted, v.Verify());              // ★사슬 불일치 감지
            Assert.Equal((long)LedgerIntegrity.Corrupted, v.IntegrityCode);
        }

        // ── ★트립 포렌식 봉인: 비가역 차단(자동복구 불가) + 봉인 레코드 기입 + 1회 래치 ──
        [Fact]
        public void Vault_SealTrip_Irreversible_BlocksAllWrites()
        {
            var v = new CausalAuditVault(64);
            for (int i = 0; i < 5; i++) v.TryAppend(1000 + i, i, i);
            long before = v.Count;

            Assert.True(v.SealTrip(qpcDelta: -5000, seqOffset: 42, bufferState: 777));
            Assert.True(v.Sealed);
            Assert.Equal(before + 1, v.Count);            // 포렌식 봉인 레코드 1건 기입
            Assert.Equal(-5000L, v.SealQpcDelta);
            Assert.Equal(42L, v.SealSeqOffset);
            Assert.Equal(777L, v.SealBufferState);
            Assert.Equal(before, v.SealAtRecord);

            // ★비가역: 이후 모든 쓰기 영구 거부(재시작 전 자동복구 없음).
            Assert.False(v.TryAppend(9999, 9, 9));
            Assert.False(v.TryAppend(8888, 8, 8));
            Assert.Equal(before + 1, v.Count);            // 봉인 후 카운트 불변
            Assert.False(v.SealTrip(1, 2, 3));            // 봉인은 1회만(중복 거부)
            Assert.Equal(LedgerIntegrity.Intact, v.Verify());   // 봉인 사슬 자체는 무결
        }

        // ── Append-Only 무-wrap: 용량 소진 시 조용한 덮어쓰기 없이 거부(유실 없음) ──
        [Fact]
        public void Vault_FullCapacity_AppendOnly_NoWrap()
        {
            var v = new CausalAuditVault(8);
            for (int i = 0; i < 8; i++) Assert.True(v.TryAppend(i, i, i));
            Assert.True(v.Full);
            Assert.False(v.TryAppend(99, 99, 99));        // 용량 소진 → 무-wrap
            Assert.Equal(8L, v.Count);
            Assert.Equal(LedgerIntegrity.Intact, v.Verify());
        }
    }
}

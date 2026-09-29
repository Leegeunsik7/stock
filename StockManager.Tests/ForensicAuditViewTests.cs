// ============================================================================
// ForensicAuditViewTests — v12.12.0 [Step 6/7] 포렌식 감사 & 비행 기록 장치 전수 오디팅 적대검증
//   Section 1: ForensicAuditViewAdapter — MmfFlightRecorder(이미 실배선이나 UI는 최근 100건 고정 헥스덤프만
//     노출)를 대용량 Virtualize 뷰어에 최초 결속. ZeroAllocForensicAuditStream(DI 등록 자체가 없었음)을 최초 결속.
//   ★ForensicAuditStreamViewer.razor(Virtualize 렌더링)는 bUnit 부재로 xUnit 직접 검증 불가 — 사중 빌드 0W/0E
//   로 구조 정합만 확인(Step 1~5 와 동일 한계, 정직 명기).
// ============================================================================
using System;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Shell;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class ForensicAuditViewTests
    {
        private readonly ITestOutputHelper _out;
        public ForensicAuditViewTests(ITestOutputHelper o) => _out = o;

        private static MmfFlightRecorder NewRecorder(int cap = 16) => new(cap, () => 1000L);

        [Fact]
        public void Adapter_ExposesRecorderMetadata()
        {
            var rec = NewRecorder(32);
            var adapter = new ForensicAuditViewAdapter(rec);
            Assert.Equal(32, adapter.RingCapacity);
            Assert.Equal(0, adapter.TotalRecorded);
            Assert.False(adapter.RingSealed);
        }

        [Fact]
        public void Adapter_SnapshotRecent_ReturnsRecordedRowsWithFields()
        {
            var rec = NewRecorder(16);
            rec.Record(FlightEventKind.RiskAdmit, symbolCode: 5930, arg0: 111, arg1: 222);
            rec.Record(FlightEventKind.Freeze, symbolCode: 0, arg0: 1, arg1: 2);
            var adapter = new ForensicAuditViewAdapter(rec);

            var rows = adapter.SnapshotRecent(10);

            Assert.Equal(2, rows.Count);
            Assert.Equal(2, adapter.TotalRecorded);
            // SnapshotRecent(기존 엔진)는 최신순 — 가장 최근(Freeze)이 0번째.
            Assert.Equal("Freeze", rows[0].EventKind);
            Assert.Equal(1, rows[0].Detail0);
            Assert.Equal("RiskAdmit", rows[1].EventKind);
            Assert.Equal(111, rows[1].Detail0);
            Assert.Equal(222, rows[1].Detail1);
        }

        [Fact]
        public void Adapter_SnapshotRecent_ZeroMaxRows_ReturnsEmpty()
        {
            var rec = NewRecorder(16);
            rec.Record(FlightEventKind.RiskAdmit, 1, 1, 1);
            var adapter = new ForensicAuditViewAdapter(rec);
            Assert.Empty(adapter.SnapshotRecent(0));
        }

        [Fact]
        public void Adapter_SnapshotRecent_ClampsToCapacity()
        {
            var rec = NewRecorder(4);
            for (int i = 0; i < 4; i++) rec.Record(FlightEventKind.RiskAdmit, i, i, i);
            var adapter = new ForensicAuditViewAdapter(rec);
            // 요청 maxRows(1000) > 용량(4) → 용량으로 clamp, 예외 없음.
            var rows = adapter.SnapshotRecent(1000);
            Assert.Equal(4, rows.Count);
        }

        [Fact]
        public void Adapter_NoChain_ChainAvailableFalse_VerifyReturnsTrue_Neutral()
        {
            var adapter = new ForensicAuditViewAdapter(NewRecorder());
            Assert.False(adapter.ChainAvailable);
            Assert.Equal(0, adapter.ChainWriteHead);
            Assert.True(adapter.VerifyChainIntegrity());   // 미결속 = 중립(위변조 주장 안 함)
        }

        [Fact]
        public void Adapter_WithChain_ValidChain_VerifiesTrue()
        {
            var chain = new ZeroAllocForensicAuditStream(16);
            chain.Append(AuditEventKind.Arm, 1, 100);
            chain.Append(AuditEventKind.RiskViolation, 2, 200);
            var adapter = new ForensicAuditViewAdapter(NewRecorder(), chain);

            Assert.True(adapter.ChainAvailable);
            Assert.Equal(2, adapter.ChainWriteHead);
            Assert.True(adapter.VerifyChainIntegrity());
        }

        [Fact]
        public void Adapter_WithChain_TamperedChain_VerifiesFalse()
        {
            var chain = new ZeroAllocForensicAuditStream(16);
            chain.Append(AuditEventKind.Arm, 1, 100);
            chain.__AdversarialCorruptForTest(sequence: 1, byteOffset: 0);
            var adapter = new ForensicAuditViewAdapter(NewRecorder(), chain);

            Assert.False(adapter.VerifyChainIntegrity());
        }
    }
}

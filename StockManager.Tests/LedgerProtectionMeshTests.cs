// ============================================================================
// LedgerProtectionMeshTests — v4.88.0: 원장 비동기 분리 + 연쇄 순차 + fail-closed
//
//  §1 페이로드(1)       — blittable 크기(unmanaged·Pack=1).
//  §2 영속화 연쇄(2)    — Enqueue→Drain→AddTransaction + RecomputeAverage 순차 보장(장부/평단).
//  §3 fail-closed(3)    — 큐 90% 포화 동결·큐 포화 write 거부·영속화 예외 동결.
//  §4 텔레메트리(2)     — 원장 필드/헬퍼·퍼블리셔 반영.
// ============================================================================
using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace StockManager.Tests
{
    public sealed class LedgerProtectionMeshTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;

        public LedgerProtectionMeshTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"ledger_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
        }
        public void Dispose() { _db.Dispose(); try { File.Delete(_dbPath); } catch { } }

        private static readonly long TxTicks = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc).Ticks;
        private static DbTransactionPayload Buy(long code, decimal qty, decimal price)
            => new(code, (byte)TransactionType.Buy, QQty.Of(qty).Raw, QPrice.Of(price).Raw, 0, 0, TxTicks);

        // ── §1 페이로드 ───────────────────────────────────────────────────────

        [Fact]
        public void Payload_IsBlittable_57Bytes()
            => Assert.Equal(57, Unsafe.SizeOf<DbTransactionPayload>());   // 7×long(56) + byte(1), Pack=1

        // ── §2 영속화 연쇄(HANDOVER §12) ──────────────────────────────────────

        [Fact]
        public void Drain_Persists_AndChainsRecomputeAverage()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att);   // Start 안 함 — DrainAvailable 직접 호출(결정론)
            Assert.True(mesh.Enqueue(Buy(5930, 10m, 70000m)));
            Assert.Equal(1, mesh.DrainAvailable());

            // ① 원장 기록 확인.
            var txs = _db.Query("SELECT * FROM Transactions WHERE Symbol=@s", new() { ["@s"] = "005930" });
            Assert.Equal(1, txs.Rows.Count);
            // ② ★연쇄 RecomputeAverage 확인 — Holdings 평단/수량 재계산됨(장부/평단 디싱크 없음).
            var hold = _db.Query("SELECT Quantity, AvgPrice FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "005930" });
            Assert.Equal(1, hold.Rows.Count);
            Assert.Equal(70000.0, Convert.ToDouble(hold.Rows[0]["AvgPrice"]), precision: 2);
            Assert.Equal(10.0, Convert.ToDouble(hold.Rows[0]["Quantity"]), precision: 6);

            Assert.Equal(1L, mesh.Persisted);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 정상 — 무동결
        }

        [Fact]
        public void Drain_MultipleBuys_SequentialAverage()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att);
            mesh.Enqueue(Buy(5930, 10m, 70000m));
            mesh.Enqueue(Buy(5930, 10m, 80000m));   // 평단 = (70000+80000)/2 = 75000
            mesh.DrainAvailable();
            var hold = _db.Query("SELECT Quantity, AvgPrice FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "005930" });
            Assert.Equal(1, hold.Rows.Count);
            Assert.Equal(20.0, Convert.ToDouble(hold.Rows[0]["Quantity"]), precision: 6);
            Assert.Equal(75000.0, Convert.ToDouble(hold.Rows[0]["AvgPrice"]), precision: 2);
        }

        // ── §3 fail-closed ────────────────────────────────────────────────────

        [Fact]
        public void Enqueue_Past90Percent_ForceFreezes()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att, capacity: 16);   // highWater = 14
            for (int i = 0; i < 16 && att.Mode == GatingMode.Normal; i++)
                mesh.Enqueue(Buy(5930, 1m, 70000m));   // 드레인 없이 적재 → 90% 초과 시 동결
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 큐 포화 → 영구 락업
        }

        [Fact]
        public void Enqueue_QueueFull_WriteRejected_Freezes()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att, capacity: 16);
            for (int i = 0; i < 20; i++) mesh.Enqueue(Buy(5930, 1m, 70000m));   // 용량 초과까지 밀어붙임
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(mesh.WriteFailures >= 1L);   // 포화 write 거부 계상
        }

        [Fact]
        public void Persist_InvalidTransaction_ForceFreezes()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att);
            // 음수 수량 → AddTransaction 검증 실패(예외) → 드레인이 삼키지 않고 ForceFreeze.
            mesh.Enqueue(new DbTransactionPayload(5930, (byte)TransactionType.Buy,
                QQty.Of(-5m).Raw, QPrice.Of(70000m).Raw, 0, 0, TxTicks));
            mesh.DrainAvailable();
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(mesh.WriteFailures >= 1L);
        }

        [Fact]
        public void Drain_PersistentFailure_FreezesOnce_NoStorm_PreservesQueue()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att, capacity: 64);
            // 3건 적재(모두 음수 수량 → 영속화 예외). 동결 전이라 모두 수용.
            for (int i = 0; i < 3; i++)
                Assert.True(mesh.Enqueue(new DbTransactionPayload(5930, (byte)TransactionType.Buy,
                    QQty.Of(-5m).Raw, QPrice.Of(70000m).Raw, 0, 0, TxTicks)));

            // 첫 드레인: 1건 시도→실패→ForceFreeze→중단. 성공 0건이므로 0 반환. 잔여는 큐 보존.
            Assert.Equal(0, mesh.DrainAvailable());
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(1L, mesh.WriteFailures);   // ★단일 실패만 계상 — 폭주 없음
            Assert.Equal(0L, mesh.Persisted);
            Assert.True(mesh.QueueDepth >= 2, $"잔여 보존 위반(drop): depth={mesh.QueueDepth}");

            // 반복 드레인: 동결 게이트로 즉시 0 반환 — 재시도/추가 실패/ForceFreeze 재격발 없음.
            for (int i = 0; i < 8; i++) Assert.Equal(0, mesh.DrainAvailable());
            Assert.Equal(1L, mesh.WriteFailures);   // ★여전히 1 — busy-spin·감사 로그 범람 없음
            Assert.True(mesh.QueueDepth >= 2);
        }

        [Fact]
        public void Frozen_Enqueue_Rejected_FailClosed()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att);
            att.ForceFreeze("사전 동결(타 원인)");
            Assert.False(mesh.Enqueue(Buy(5930, 1m, 70000m)));   // ★동결 후 신규 수용 거부(fail-closed)
            Assert.Equal(0, mesh.QueueDepth);
            Assert.True(mesh.WriteFailures >= 1L);
        }

        // ── §4 텔레메트리 ─────────────────────────────────────────────────────

        [Fact]
        public void RingTelemetry_LedgerFields_And_Helpers()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0, 0, 0, 0.0, 0, 0, 0, 0,
                ledgerQueueDepth: 95, ledgerCapacity: 100, ledgerAvgPersistMicros: 250.5, ledgerWriteFailures: 0);
            Assert.Equal(95.0, t.LedgerSaturationPct, precision: 6);
            Assert.True(t.LedgerOverflowAlert);   // 95% ≥ 90% → 경보
            Assert.Equal(250.5, t.LedgerAvgPersistMicros, precision: 6);
        }

        [Fact]
        public void Publisher_ReflectsLedgerMesh()
        {
            var att = new OrderAttestationService();
            using var mesh = new LedgerProtectionMesh(_db, att, capacity: 128);
            mesh.Enqueue(Buy(5930, 1m, 70000m));   // 깊이 1
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L, null, null, null, null, null, mesh);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.Equal(128L, snap.LedgerCapacity);
            Assert.True(snap.LedgerQueueDepth >= 1L);
        }
    }
}

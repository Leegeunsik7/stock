using System;
using System.IO;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S25-b — 복합 저널 메인 척추 승격.
    //   §1 원자 세션(genome+forensic 단일 seqlock·split-brain 봉인·다중 라이터)
    //   §2 복합 persist/recover·v1 마이그레이션·오염 동결  §3 펌프 위임 모드  §4 부팅 오케스트레이터
    public sealed class S25bCompositeSpineTests
    {
        private static string TempPath()
            => Path.Combine(Path.GetTempPath(), "ms2e_s25b_" + Guid.NewGuid().ToString("N") + ".dat");
        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        private static JournalRecord[] JRecs(int n)
        {
            var a = new JournalRecord[n];
            for (int i = 0; i < n; i++)
                a[i] = new JournalRecord(i + 1, RecoveryKind.Rejection, 2, 0, i * 1000, i * 1000 + 877, 500, i * 1.5, 77);
            return a;
        }

        // ═══════════════════ §1 원자 세션 ═══════════════════

        [Fact]
        public void SnapshotSession_ReturnsPublishedGenome_AndForensics()
        {
            var a = new RejectionForensicAuditor(16);
            a.PublishGenome(new GenomeSnapshot(42, 777, 2.5, 1000, 3));
            a.Record(RejectionStage.Activate, 100, 977, 50, 1.0, "g");
            var buf = new RejectionRecord[16];
            int n = a.SnapshotSession(buf, out var g);
            Assert.Equal(42, g.Seq);
            Assert.Equal(2.5, g.Fitness, 6);
            Assert.Equal(1, n);
            Assert.Equal(RejectionStage.Activate, buf[0].Stage);
        }

        [Fact]
        public void SnapshotSession_AtomicGenome_NoTornRead_UnderConcurrentPublish()
        {
            // 단면 시간 정합성 봉인 — genome.GenomeHash==Seq*7 불변식을 동시 발행 중 독출이 깨면 torn.
            var a = new RejectionForensicAuditor(16);
            int failures = 0; bool stop = false;
            void Reader()
            {
                var buf = new RejectionRecord[16];
                while (!Volatile.Read(ref stop))
                    if (a.SnapshotSession(buf, out var g) >= 0 && g.HasValue && g.GenomeHash != g.Seq * 7)
                        Interlocked.Increment(ref failures);
            }
            var r1 = new Thread(Reader) { IsBackground = true };
            var r2 = new Thread(Reader) { IsBackground = true };
            r1.Start(); r2.Start();
            for (long s = 1; s <= 60000; s++) a.PublishGenome(new GenomeSnapshot(s, s * 7, s * 0.5, s * 1000, 3));
            Volatile.Write(ref stop, true); r1.Join(3000); r2.Join(3000);
            Assert.Equal(0, failures);
        }

        [Fact]
        public void Auditor_MultiWriter_GenomeAndRejection_NoCorruption()
        {
            // genome 발행(스레드1)과 rejection 기록(스레드2) 동시 — write-gate 가 seqlock 무결성 보장.
            var a = new RejectionForensicAuditor(16);
            var t1 = new Thread(() => { for (long s = 1; s <= 30000; s++) a.PublishGenome(new GenomeSnapshot(s, s * 7, 1, 1000, 3)); }) { IsBackground = true };
            var t2 = new Thread(() => { for (int i = 0; i < 30000; i++) a.Record(RejectionStage.Approve, 1, 2, 1, 0, null); }) { IsBackground = true };
            t1.Start(); t2.Start(); t1.Join(5000); t2.Join(5000);

            var buf = new RejectionRecord[16];
            a.SnapshotSession(buf, out var g);
            Assert.True(g.HasValue && g.GenomeHash == g.Seq * 7);   // genome 무결(torn 아님)
            Assert.Equal(30000, a.Count);                            // 거부 카운트 무손실
        }

        // ═══════════════════ §2 복합 persist/recover ═══════════════════

        [Fact]
        public void Journaler_PersistRecoverComposite_RoundTrip()
        {
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, 16);
                Assert.True(j.PersistComposite(new GenomeSnapshot(42, 777, 2.5, 1000, 3), JRecs(3)));
                var dest = new JournalRecord[16];
                var r = j.RecoverComposite(dest, out int n, out var g);
                Assert.Equal(JournalStatus.Ok, r.File);
                Assert.False(r.AnyCorrupt);
                Assert.Equal(42, g.Seq);
                Assert.Equal(2.5, g.Fitness, 6);
                Assert.Equal(3, n);
                Assert.Equal(3, dest[2].Seq);
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Journaler_V1FlatFile_RecoverComposite_EmptyMigration_NoFreeze()
        {
            // 미션2 — 구 플랫 포맷(MS2C) 유입 시 동결 아닌 Empty(마이그레이션) 흡수.
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, 16);
                j.Persist(JRecs(3));   // 구 플랫 포맷 기록
                var att = new OrderAttestationService();
                var r = j.RecoverOrFreezeComposite(att, new JournalRecord[16], out _, out _);
                Assert.Equal(JournalStatus.Empty, r.File);             // 플랫 매직 → Empty
                Assert.False(r.AnyCorrupt);
                Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode); // 무동결
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Journaler_CorruptComposite_FailsClosed()
        {
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, 16);
                j.PersistComposite(new GenomeSnapshot(1, 7, 1, 1000, 3), JRecs(3));
                var bytes = File.ReadAllBytes(path);
                int forensicData = 8 + 8 + CompositeJournalCodec.GenomeSize + 12;   // 포렌식 데이터 오프셋
                bytes[forensicData + 2] ^= 0xFF;
                File.WriteAllBytes(path, bytes);

                var att = new OrderAttestationService();
                var r = j.RecoverOrFreezeComposite(att, new JournalRecord[16], out _, out _);
                Assert.True(r.AnyCorrupt);
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 내용 오염 → fail-closed
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Journaler_NoFileComposite_Empty_NoFreeze()
        {
            var j = new CrashRecoveryJournaler(TempPath(), 16);
            var att = new OrderAttestationService();
            var r = j.RecoverOrFreezeComposite(att, new JournalRecord[16], out _, out _);
            Assert.Equal(JournalStatus.Empty, r.File);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §3 펌프 위임 모드 ═══════════════════

        [Fact]
        public void Pump_DelegateMode_FlushIfDirty_NoLostUpdate()
        {
            int flushes = 0;
            var pump = new JournalFlushPump(() => { flushes++; return true; });
            Assert.False(pump.FlushIfDirty());   // dirty 아님
            pump.RequestFlush();
            Assert.True(pump.FlushIfDirty());    // 위임 호출
            Assert.False(pump.FlushIfDirty());
            pump.RequestFlush();
            Assert.True(pump.FlushIfDirty());
            Assert.Equal(2, flushes);
            Assert.Equal(2, pump.Flushes);
        }

        [Fact]
        public void Pump_DelegateMode_FailReturnsFalse_NoAdvance()
        {
            var pump = new JournalFlushPump(() => false);   // 영속화 실패 위임
            pump.RequestFlush();
            Assert.False(pump.FlushIfDirty());   // 실패 → 미전진(flushedGen 불변)
            Assert.True(pump.FlushErrors >= 1);
            Assert.False(pump.FlushIfDirty());   // 여전히 dirty라 재시도하나 위임이 또 실패 → false(데이터 미손실)
        }

        [Fact]
        public void Pump_DelegateMode_NullDelegate_Throws()
            => Assert.Throws<ArgumentNullException>(() => new JournalFlushPump((Func<bool>)null!));

        // ═══════════════════ §4 부팅 오케스트레이터 ═══════════════════

        [Fact]
        public void BootOrchestrator_Composite_FlatFile_Migrates_NoFreeze()
        {
            string jpath = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(jpath, 16);
                j.Persist(JRecs(3));   // 구 플랫 포맷
                var hyd  = new StateHydrationEngine(TempPath());
                var att  = new OrderAttestationService();
                var slot = new HydrationSlot<int>((int)GatingMode.Normal);
                var rep  = BootOrchestrator.RunSafeBoot(hyd, att, slot, j, new JournalRecord[16], composite: true);
                Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 마이그레이션 무동결
                Assert.NotEqual(BootSafety.Frozen, rep.Safety);
            }
            finally { TryDelete(jpath); }
        }
    }
}

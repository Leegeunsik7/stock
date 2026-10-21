using System;
using System.IO;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    // S24-b — 가역적 부팅 시퀀스 + 비동기 플러시 검증.
    //   §1 RejectionForensicAuditor.SnapshotInto 배치 드레인
    //   §2 JournalFlushPump 핫패스 무할당·lost-update 0·수명
    //   §3 ForensicJournalProvider 변환
    //   §4 BootOrchestrator 합성(후방호환·Corrupt 동결·Inaccessible 무동결[자가당착 차단])
    public sealed class S24bBootSequenceTests
    {
        private static string TempPath()
            => Path.Combine(Path.GetTempPath(), "ms2e_s24b_" + Guid.NewGuid().ToString("N") + ".dat");
        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        private static JournalRecord[] JRecs(int n)
        {
            var a = new JournalRecord[n];
            for (int i = 0; i < n; i++)
                a[i] = new JournalRecord(i + 1, RecoveryKind.Rejection, 2, 0, i * 1000, i * 1000 + 877, 500, i * 1.5, 77);
            return a;
        }
        private static bool WaitFor(Func<bool> c, int ms = 2000)
        {
            var dl = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < dl) { if (c()) return true; Thread.Sleep(10); }
            return c();
        }

        private sealed class FakeProvider : IJournalSnapshotProvider
        {
            public int N = 2, Captures;
            public int CaptureInto(Span<JournalRecord> dest)
            {
                Captures++;
                int k = Math.Min(N, dest.Length);
                for (int i = 0; i < k; i++)
                    dest[i] = new JournalRecord(i + 1, RecoveryKind.Rejection, 2, 0, 100, 977, 50, 1.0, 9);
                return k;
            }
        }

        // ═══════════════════ §1 SnapshotInto ═══════════════════

        [Fact]
        public void SnapshotInto_Empty_Zero()
            => Assert.Equal(0, new RejectionForensicAuditor().SnapshotInto(new RejectionRecord[4]));

        [Fact]
        public void SnapshotInto_DrainsTimeOrdered()
        {
            var a = new RejectionForensicAuditor(capacity: 8);
            for (int i = 1; i <= 3; i++) a.Record(RejectionStage.Activate, i * 10, i * 10 + 5, 1, i, null);
            var dest = new RejectionRecord[4];
            int n = a.SnapshotInto(dest);
            Assert.Equal(3, n);
            Assert.Equal(1, dest[0].Seq);   // 오래된→
            Assert.Equal(3, dest[2].Seq);   // →최신
        }

        [Fact]
        public void SnapshotInto_RingWrap_KeepsNewest()
        {
            var a = new RejectionForensicAuditor(capacity: 4);
            for (int i = 1; i <= 6; i++) a.Record(RejectionStage.Approve, i, i + 1, 1, i, null);
            var dest = new RejectionRecord[8];
            int n = a.SnapshotInto(dest);
            Assert.Equal(4, n);             // 용량 제한
            Assert.Equal(3, dest[0].Seq);   // 가장 오래된 유효
            Assert.Equal(6, dest[3].Seq);   // 최신
        }

        // ═══════════════════ §2 JournalFlushPump ═══════════════════

        [Fact]
        public void Pump_RequestFlush_ZeroAllocation()
        {
            string path = TempPath();
            try
            {
                using var pump = new JournalFlushPump(new CrashRecoveryJournaler(path, 16), new FakeProvider(), 16);
                pump.RequestFlush();   // 워밍업
                Assert.Equal(0L, ZeroAllocProbe.MinAllocated(() =>
                {
                    for (int k = 0; k < 500; k++) pump.RequestFlush();
                })); // 핫패스 할당 0
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Pump_FlushIfDirty_NoLostUpdate()
        {
            string path = TempPath();
            try
            {
                var pump = new JournalFlushPump(new CrashRecoveryJournaler(path, 16), new FakeProvider(), 16);
                Assert.False(pump.FlushIfDirty());   // dirty 아님
                pump.RequestFlush();
                Assert.True(pump.FlushIfDirty());    // dirty → 플러시
                Assert.False(pump.FlushIfDirty());   // 다시 안 dirty
                pump.RequestFlush();
                Assert.True(pump.FlushIfDirty());    // 재요청 → 재플러시(lost-update 0)
                Assert.Equal(2, pump.Flushes);
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Pump_StartStop_Lifecycle_FlushesAndJoins()
        {
            string path = TempPath();
            try
            {
                var pump = new JournalFlushPump(new CrashRecoveryJournaler(path, 16), new FakeProvider(), 16, intervalMs: 25);
                pump.Start();
                Assert.True(pump.Running);
                pump.RequestFlush();
                // 부하 하 백그라운드 펌프 스레드 첫 스케줄 지연 대비 넉넉한 데드락 안전망(조건 충족 즉시 반환 → 비용 0).
                Assert.True(WaitFor(() => pump.Flushes > 0, 8000), "백그라운드 플러시 발생");
                pump.Stop();
                Assert.False(pump.Running);   // 합류 완료
            }
            finally { TryDelete(path); }
        }

        // ═══════════════════ §3 ForensicJournalProvider ═══════════════════

        [Fact]
        public void ForensicProvider_ConvertsRejections()
        {
            var a = new RejectionForensicAuditor();
            a.Record(RejectionStage.Activate, 100, 977, 50, 2.5, "g-1");
            var prov = new ForensicJournalProvider(a);
            var dest = new JournalRecord[8];
            int n = prov.CaptureInto(dest);
            Assert.Equal(1, n);
            Assert.Equal((byte)RecoveryKind.Rejection, dest[0].Kind);
            Assert.Equal(JournalRecord.HashId("g-1"), dest[0].GenomeHash);
            Assert.Equal(2.5, dest[0].Fitness, 6);
        }

        // ═══════════════════ §4 BootOrchestrator ═══════════════════

        private static (StateHydrationEngine, OrderAttestationService, HydrationSlot<int>) CleanBoot()
            => (new StateHydrationEngine(TempPath()),   // 파일 없음 → NoSnapshot
                new OrderAttestationService(),
                new HydrationSlot<int>((int)GatingMode.Normal));

        [Fact]
        public void BootOrchestrator_NullJournaler_Backcompat()
        {
            var (hyd, att, slot) = CleanBoot();
            var rep = BootOrchestrator.RunSafeBoot(hyd, att, slot);   // journaler null
            Assert.Equal(HydrationStatus.NoSnapshot, rep.Integrity.Status);
            Assert.Equal(BootSafety.Clean, rep.Safety);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(JournalStatus.Empty, rep.Journal);
        }

        [Fact]
        public void BootOrchestrator_CorruptJournal_Frozen()
        {
            string jpath = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(jpath, 16);
                j.Persist(JRecs(3));
                var bytes = File.ReadAllBytes(jpath);
                bytes[JournalCodec.HeaderSize + 2] ^= 0xFF;   // 내용 오염
                File.WriteAllBytes(jpath, bytes);

                var (hyd, att, slot) = CleanBoot();
                var rep = BootOrchestrator.RunSafeBoot(hyd, att, slot, j, new JournalRecord[16]);
                Assert.True(rep.Journal.IsCorrupt());
                Assert.Equal(BootSafety.Frozen, rep.Safety);
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 불결한 크래시 → fail-closed
            }
            finally { TryDelete(jpath); }
        }

        [Fact]
        public void BootOrchestrator_InaccessibleJournal_NotFrozen()
        {
            // 미션3 부팅 레벨 — 저널 파일 락(백신/공유위반)으로 거래를 엉뚱하게 동결하지 않는다.
            string jpath = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(jpath, 16);
                j.Persist(JRecs(3));
                using (var locker = new FileStream(jpath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var (hyd, att, slot) = CleanBoot();
                    var rep = BootOrchestrator.RunSafeBoot(hyd, att, slot, j, new JournalRecord[16]);
                    Assert.Equal(JournalStatus.Inaccessible, rep.Journal);
                    Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 자가당착 차단 — 무동결
                    Assert.Equal(BootSafety.Clean, rep.Safety);             // 하이드레이션이 권위 있게 판정
                }
            }
            finally { TryDelete(jpath); }
        }

        [Fact]
        public void Greenhouse_OnChampionPromoted_FiresOnSubmit()
        {
            // S24-b — 챔피언 제출 시 핫패스 dirty 훅 발화(펌프 RequestFlush 결속점). 미설정 시 무동작.
            int fired = 0;
            var gh = new GreenhouseService(new PromotionGateway()) { OnChampionPromoted = () => fired++ };
            gh.Prime(GreenhouseActivator.DefaultSeeds(), GreenhouseActivator.BootstrapCandles(), Market.KRX_KOSPI);
            gh.RunCycleOnce();
            Assert.True(fired >= 1);
        }

        [Fact]
        public void BootOrchestrator_NullArgs_Throw()
        {
            var (hyd, att, slot) = CleanBoot();
            Assert.Throws<ArgumentNullException>(() => BootOrchestrator.RunSafeBoot(null!, att, slot));
            Assert.Throws<ArgumentNullException>(() => BootOrchestrator.RunSafeBoot(hyd, null!, slot));
            Assert.Throws<ArgumentNullException>(() => BootOrchestrator.RunSafeBoot(hyd, att, null!));
        }
    }
}

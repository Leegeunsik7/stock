// ============================================================================
// FlightRecorderTests — v12.5.0 Black-Box Flight Recorder 적대검증
//   Think Beyond Idea A: MmfCircularBlackBoxWriter(진짜 System.IO.MemoryMappedFiles 순환 블랙박스).
//   Section 1 §2: BlackBoxDumpTriggerBridge — Fatal Exception/Hardware Kill-Switch/OOM 임박 시에만
//     기존 CrashResilientDumper 가 버퍼를 원자적으로 디스크 덤프함을 실증(신규 병렬 덤퍼 없음 — 재사용 결속).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.IO;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class FlightRecorderTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _tempDir;
        public FlightRecorderTests(ITestOutputHelper o)
        {
            _out = o;
            _tempDir = Path.Combine(Path.GetTempPath(), "ms2mmfbb_" + Guid.NewGuid().ToString("N"));
        }
        public void Dispose() { try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { } }

        private static FlightRecordEvent Ev(long seq, byte kind) => new(seq, 1000 + seq, seq * 5, seq * 10, seq * 20, 0, 7, 1, kind, 0);

        // ── §1 생성 + Append + ReadAll 왕복 ──────────────────────────────────────
        [Fact]
        public void MmfWriter_Append_ReadAll_RoundTrips()
        {
            string path = Path.Combine(_tempDir, "bb1.mmf");
            using var w = new MmfCircularBlackBoxWriter(path, capacity: 8);
            for (long i = 1; i <= 5; i++) w.Append(Ev(i, (byte)FlightEventKind.RiskAdmit));

            Assert.Equal(5, w.WriteSeq);
            var dest = new FlightRecordEvent[8];
            int n = w.ReadAll(dest);
            Assert.Equal(5, n);
            Assert.Equal(1L, dest[0].Sequence);
            Assert.Equal(5L, dest[4].Sequence);
        }

        // ── §2 용량 초과 — 순환 덮어쓰기(드롭 대신 최신 보존) ────────────────────
        [Fact]
        public void MmfWriter_ExceedsCapacity_OverwritesOldest_KeepsLatest()
        {
            string path = Path.Combine(_tempDir, "bb2.mmf");
            using var w = new MmfCircularBlackBoxWriter(path, capacity: 4);
            for (long i = 1; i <= 10; i++) w.Append(Ev(i, (byte)FlightEventKind.Freeze));

            var dest = new FlightRecordEvent[4];
            int n = w.ReadAll(dest);
            Assert.Equal(4, n);
            Assert.Equal(7L, dest[0].Sequence);   // 최신 4개(7,8,9,10) 만 생존
            Assert.Equal(10L, dest[3].Sequence);
        }

        // ── §3 실제 파일에 진짜 MemoryMappedFile 로 영속 — 재오픈 시 시퀀스 이어받기 ──
        [Fact]
        public void MmfWriter_Reopen_ResumesWriteSeq_SurvivesAcrossInstances()
        {
            string path = Path.Combine(_tempDir, "bb3.mmf");
            using (var w1 = new MmfCircularBlackBoxWriter(path, capacity: 8))
            {
                for (long i = 1; i <= 3; i++) w1.Append(Ev(i, (byte)FlightEventKind.KillSwitchTrip));
            }
            Assert.True(File.Exists(path));   // ★진짜 디스크 파일 — 커널 페이지 캐시 매핑 대상 실존

            using var w2 = new MmfCircularBlackBoxWriter(path, capacity: 8);
            Assert.Equal(3, w2.WriteSeq);   // 이전 인스턴스의 시퀀스 이어받기(크래시 후 재기동 시나리오)
            w2.Append(Ev(4, (byte)FlightEventKind.KillSwitchTrip));
            var dest = new FlightRecordEvent[8];
            int n = w2.ReadAll(dest);
            Assert.Equal(4, n);
            Assert.Equal(4L, dest[3].Sequence);
        }

        [Fact]
        public void MmfWriter_AfterDispose_ThrowsOnAppend()
        {
            string path = Path.Combine(_tempDir, "bb4.mmf");
            var w = new MmfCircularBlackBoxWriter(path, capacity: 4);
            w.Dispose();
            Assert.Throws<ObjectDisposedException>(() => w.Append(Ev(1, 0)));
        }

        // ── §4 BlackBoxDumpTriggerBridge — Fatal Exception 트리거 → 기존 덤퍼 결속 ──
        [Fact]
        public void Bridge_OnFatalException_TriggersDump_ViaExistingDumper()
        {
            string dir = Path.Combine(_tempDir, "fatal");
            var rec = new MmfFlightRecorder(64, () => 1000L);
            rec.Record(FlightEventKind.RiskAdmit, 1, 2, 3);
            var att = new OrderAttestationService();
            using var dumper = new CrashResilientDumper(rec, att, dumpDir: dir, nowTicks: () => 777L);
            var bridge = new BlackBoxDumpTriggerBridge(dumper);

            Assert.Equal(BlackboxDumpState.Idle, dumper.State);
            bridge.OnFatalException(new InvalidOperationException("simulated fatal"));

            Assert.Equal(BlackboxDumpState.Completed, dumper.State);
            Assert.True(File.Exists(dumper.DumpPath));
        }

        // ── §5 OOM 임박 트리거 ────────────────────────────────────────────────
        [Fact]
        public void Bridge_OnOomImminent_TriggersDump()
        {
            string dir = Path.Combine(_tempDir, "oom");
            var rec = new MmfFlightRecorder(64, () => 1000L);
            var att = new OrderAttestationService();
            using var dumper = new CrashResilientDumper(rec, att, dumpDir: dir, nowTicks: () => 1L);
            var bridge = new BlackBoxDumpTriggerBridge(dumper);

            bridge.OnOomImminent();
            Assert.Equal(BlackboxDumpState.Completed, dumper.State);
        }

        // ── §6 하드웨어 킬스위치 격발 → 덤프 결속 ─────────────────────────────
        [Fact]
        public void Bridge_WireKillSwitch_KilledEvent_TriggersDump()
        {
            string dir = Path.Combine(_tempDir, "kill");
            var rec = new MmfFlightRecorder(64, () => 1000L);
            var att = new OrderAttestationService();
            using var dumper = new CrashResilientDumper(rec, att, dumpDir: dir, nowTicks: () => 1L);
            var bridge = new BlackBoxDumpTriggerBridge(dumper);
            var killSwitch = new HardwareKillSwitchBridge();
            bridge.WireKillSwitch(killSwitch);

            killSwitch.Signal(KillSwitchReason.HardwareButton);

            Assert.Equal(BlackboxDumpState.Completed, dumper.State);
        }

        // ── §7 LiveSoak 브리치 트리거(Section 1↔2 결속 지점) ──────────────────
        [Fact]
        public void Bridge_OnLiveSoakBreach_TriggersDump()
        {
            string dir = Path.Combine(_tempDir, "livesoak");
            var rec = new MmfFlightRecorder(64, () => 1000L);
            var att = new OrderAttestationService();
            using var dumper = new CrashResilientDumper(rec, att, dumpDir: dir, nowTicks: () => 1L);
            var bridge = new BlackBoxDumpTriggerBridge(dumper);

            bridge.OnLiveSoakBreach(LiveSoakBreachSource.BaselineDrift);
            Assert.Equal(BlackboxDumpState.Completed, dumper.State);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §8 적대검증 — δ=0 핫패스(정상 경로) + 다중스레드
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class FlightRecorderConcurrencyTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ms2mmfbb_cc_" + Guid.NewGuid().ToString("N"));
        public FlightRecorderConcurrencyTests(ITestOutputHelper o) => _out = o;
        public void Dispose() { try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { } }

        [Fact]
        public void MmfWriter_Append_ZeroAllocation_SteadyState()
        {
            string path = Path.Combine(_tempDir, "hot.mmf");
            using var w = new MmfCircularBlackBoxWriter(path, capacity: 4096);
            var ev = new FlightRecordEvent(0, 0, 0, 0, 0, 0, 0, 0, (byte)FlightEventKind.RiskAdmit, 0);

            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) w.Append(ev);
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 10_000; i++) w.Append(ev);
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            _out.WriteLine($"[MmfBlackBox] Append alloc={alloc}B over 10k(steady-state, post file/view 오픈)");
            Assert.Equal(0L, alloc);   // ★MemoryMappedViewAccessor.Write<T> 제네릭 unmanaged 오버로드 힙 0
        }
    }
}

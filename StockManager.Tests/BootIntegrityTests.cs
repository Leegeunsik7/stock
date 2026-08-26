// ============================================================================
// BootIntegrityTests — S18 #1: 콜드 하든 검증 (13개)
//
//  §1 BootIntegrityChecker (7) — 부팅 시점 GatingMode 정합성 + fail-closed
//      클린부팅 · 정상복원 · 동결복원(패리티) · 손상→안전동결 · 풀 충돌 라운드트립
//      · 신선도(Stale) 라벨 · 무단 해제 금지(never-unfreeze)
//  §2 MemoryLeakGuard (6)      — 고아 핸들러 추적
//      리스 발급/반납 · 고속루프 해제 후 잔여 0 · 누수 탐지 · 멱등 디스포즈
//      · Probe 메트릭 · 카테고리 카운트
//
//  순수 결정론: 임시 MMF 파일은 인스턴스별 격리 후 Dispose 에서 일괄 삭제.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class BootIntegrityTests : IDisposable
    {
        private readonly List<string> _tempFiles = new();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // ── 헬퍼 ─────────────────────────────────────────────────────────────────

        private string NewPath()
        {
            var p = Path.Combine(Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + "-state.hydr");
            _tempFiles.Add(p);
            return p;
        }

        // 지정 경로에 gatingMode 슬롯이 등록된 엔진을 생성
        private static (StateHydrationEngine engine, HydrationSlot<int> slot) NewEngine(string path)
        {
            var engine = new StateHydrationEngine(path);
            var slot   = new HydrationSlot<int>((int)GatingMode.Normal);
            engine.RegisterSlot("gatingMode", slot);
            return (engine, slot);
        }

        // 한 세션이 주어진 모드로 스냅샷을 남기고 종료한 상황을 시뮬레이션
        private void WriteSnapshotWithMode(string path, GatingMode mode)
        {
            var (engine, slot) = NewEngine(path);
            slot.Value = (int)mode;     // RegisterSlot getter 가 slot.Value 를 직렬화
            engine.Snapshot();
            engine.Dispose();
        }

        // ── §1 BootIntegrityChecker ───────────────────────────────────────────────

        [Fact]
        public void NoSnapshot_CleanBoot_StaysNormal()
        {
            var (engine, slot) = NewEngine(NewPath());  // 스냅샷 미생성
            var att = new OrderAttestationService();

            var report = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(HydrationStatus.NoSnapshot, report.Status);
            Assert.Equal(GatingMode.Normal, att.Mode);
            Assert.False(report.FrozeOnBoot);
            Assert.Null(report.SnapshotUtc);
        }

        [Fact]
        public void RecoveredNormalMode_StaysNormal()
        {
            var path = NewPath();
            WriteSnapshotWithMode(path, GatingMode.Normal);

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            var report = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(HydrationStatus.Recovered, report.Status);
            Assert.Equal(GatingMode.Normal, att.Mode);
            Assert.False(report.FrozeOnBoot);
        }

        [Fact]
        public void CrashedWhileFrozen_RestoresFreezeOnBoot()
        {
            // 핵심 패리티: 동결 상태에서 충돌 후 재부팅 → 동결 완벽 복원
            var path = NewPath();
            WriteSnapshotWithMode(path, GatingMode.EmergencyFreeze);

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();   // 인메모리는 Normal 로 초기화됨
            Assert.Equal(GatingMode.Normal, att.Mode);

            var report = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 동결 복원!
            Assert.True(report.FrozeOnBoot);
            Assert.Equal(HydrationStatus.Recovered, report.Status);
        }

        [Fact]
        public void CorruptedSnapshot_FailsClosedAndClears()
        {
            // 안전 기본값(Safe Default): 변조된 스냅샷 = 상태 미상 ⇒ 보수적 동결
            var path = NewPath();
            WriteSnapshotWithMode(path, GatingMode.Normal);

            // 슬롯 데이터 영역 1바이트 변조 → CRC32 불일치 유발 (헤더 32B 이후)
            var bytes = File.ReadAllBytes(path);
            bytes[40] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            var report = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(HydrationStatus.Corrupted, report.Status);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // fail-closed
            Assert.True(report.FrozeOnBoot);
            Assert.False(engine.HasValidSnapshot);                // 손상 스냅샷 폐기됨
        }

        [Fact]
        public void FullCrashRoundTrip_SeparateInstances_RestoresFreeze()
        {
            var path = NewPath();

            // 세션 1: 운영 중 미스매치로 동결 → 스냅샷 기록 후 비정상 종료
            var (engine1, slot1) = NewEngine(path);
            var att1 = new OrderAttestationService();
            att1.ForceFreeze("정합 미스매치 — 비상 동결");
            slot1.Value = (int)att1.Mode;
            engine1.Snapshot();
            engine1.Dispose();

            // 세션 2: 콜드 부팅 — 완전히 새로운 인스턴스, 같은 경로
            var (engine2, slot2) = NewEngine(path);
            var att2 = new OrderAttestationService();
            var report = BootIntegrityChecker.CheckAndHydrate(engine2, att2, slot2);

            Assert.True(report.FrozeOnBoot);
            Assert.Equal(GatingMode.EmergencyFreeze, att2.Mode);
            // 복원된 동결은 수동 서명으로만 해제 가능
            att2.ManualRelease("operator-signature", "브로커 정합 확인 완료");
            Assert.Equal(GatingMode.Normal, att2.Mode);
        }

        [Fact]
        public void StaleSnapshot_LabeledStale_ButHonorsRecoveredMode()
        {
            var path = NewPath();
            WriteSnapshotWithMode(path, GatingMode.EmergencyFreeze);
            Thread.Sleep(30);   // UtcNow 분해능(~15ms) 초과 보장 — 결정론적 staleness

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            // freshnessWindow=Zero → 경과 시간이 0보다 크면 Stale
            var report = BootIntegrityChecker.CheckAndHydrate(engine, att, slot, TimeSpan.Zero);

            Assert.Equal(HydrationStatus.Stale, report.Status);
            Assert.NotNull(report.SnapshotUtc);
            // 오래되었어도 동결은 fail-closed 로 보존
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(report.FrozeOnBoot);
        }

        [Fact]
        public void NeverUnfreezes_PreExistingFreezePreserved()
        {
            // 검사기는 ForceFreeze 만 하고 절대 해제하지 않는다 (수동 서명 전용)
            var path = NewPath();
            WriteSnapshotWithMode(path, GatingMode.Normal);   // 스냅샷은 Normal

            var (engine, slot) = NewEngine(path);
            var att = new OrderAttestationService();
            att.ForceFreeze("부팅 전 외부 동결");              // 이미 동결 상태

            var report = BootIntegrityChecker.CheckAndHydrate(engine, att, slot);

            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode); // 여전히 동결 — 무단 해제 금지
            Assert.False(report.FrozeOnBoot);                   // 검사기가 새로 동결한 건 아님
        }

        // ── §2 MemoryLeakGuard ─────────────────────────────────────────────────────

        [Fact]
        public void Acquire_Then_Dispose_LiveCountReturnsToZero()
        {
            var guard = new MemoryLeakGuard();
            Assert.Equal(0, guard.LiveCount);

            var lease = guard.Acquire("sentinel-sub");
            Assert.Equal(1, guard.LiveCount);
            Assert.Equal(1, guard.TotalAcquired);

            lease.Dispose();
            Assert.Equal(0, guard.LiveCount);
            Assert.Equal(1, guard.TotalReleased);
            Assert.True(lease.IsReleased);
        }

        [Fact]
        public void FakeEventLoop_DisposeReleasesAllHandlers_ZeroResidual()
        {
            // 가상 고속 이벤트 루프(핸들러 16 + 렌더타이머 1) 해제 후 잔여 핸들러 0바이트 검증
            var guard = new MemoryLeakGuard();
            var loop  = new FakeEventLoop(guard, handlers: 16);

            Assert.Equal(17, guard.LiveCount);

            loop.Dispose();

            Assert.Equal(0, guard.LiveCount);            // 잔여 핸들러 0
            Assert.Equal(17, guard.TotalReleased);
            Assert.False(guard.Probe().LeakSuspected);
        }

        [Fact]
        public void LeakyLoop_OrphanedHandlers_AreDetected()
        {
            // Dispose 에서 반납을 누락한 누수 컴포넌트 → LiveCount 가 0으로 안 떨어짐
            var guard = new MemoryLeakGuard();
            var leaky = new LeakyEventLoop(guard, handlers: 8);

            leaky.Dispose();   // BUG: 리스 반납 누락

            Assert.Equal(8, guard.LiveCount);            // 누수 가시화
            Assert.Equal(8, guard.LiveCountFor("evt-handler"));
            Assert.Equal(0, guard.TotalReleased);
        }

        [Fact]
        public void DoubleDispose_IsIdempotent()
        {
            var guard = new MemoryLeakGuard();
            var lease = guard.Acquire();

            lease.Dispose();
            lease.Dispose();   // 두 번째는 무해해야 함
            lease.Dispose();

            Assert.Equal(0, guard.LiveCount);
            Assert.Equal(1, guard.TotalReleased);        // 단 1회만 반납
        }

        [Fact]
        public void Probe_ReportsWorkingSet_AndLeakFlagLogic()
        {
            var clean = new MemoryLeakGuard();
            var r0 = clean.Probe();
            Assert.True(r0.WorkingSetBytes > 0);
            Assert.True(r0.ManagedHeapBytes > 0);
            Assert.False(r0.LeakSuspected);              // 리스 0개

            var tight = new MemoryLeakGuard { LeaseLeakThreshold = 1 };
            tight.Acquire("a");
            tight.Acquire("b");                          // LiveLeases=2 > 1
            Assert.True(tight.Probe().LeakSuspected);
        }

        [Fact]
        public void CategoryCounts_TrackedIndependently()
        {
            var guard = new MemoryLeakGuard();
            var t1 = guard.Acquire("render-timer");
            guard.Acquire("sentinel-sub");
            guard.Acquire("sentinel-sub");

            Assert.Equal(1, guard.LiveCountFor("render-timer"));
            Assert.Equal(2, guard.LiveCountFor("sentinel-sub"));
            Assert.Equal(0, guard.LiveCountFor("nonexistent"));

            t1.Dispose();
            Assert.Equal(0, guard.LiveCountFor("render-timer"));
            Assert.Equal(2, guard.LiveCountFor("sentinel-sub"));
        }

        // ── 테스트용 가상 컴포넌트 ──────────────────────────────────────────────────

        // 올바르게 구현된 고속 이벤트 루프: Dispose 시 전 리스 반납
        private sealed class FakeEventLoop : IDisposable
        {
            private readonly List<ResourceLease> _leases = new();
            public FakeEventLoop(MemoryLeakGuard guard, int handlers)
            {
                for (int i = 0; i < handlers; i++)
                    _leases.Add(guard.Acquire("evt-handler"));
                _leases.Add(guard.Acquire("render-timer"));
            }
            public void Dispose()
            {
                foreach (var l in _leases) l.Dispose();
                _leases.Clear();
            }
        }

        // 결함 컴포넌트: 리스를 획득하나 Dispose 에서 반납을 누락 (고아 핸들러)
        private sealed class LeakyEventLoop : IDisposable
        {
            private readonly List<ResourceLease> _leases = new();
            public LeakyEventLoop(MemoryLeakGuard guard, int handlers)
            {
                for (int i = 0; i < handlers; i++)
                    _leases.Add(guard.Acquire("evt-handler"));
            }
            public void Dispose() { /* BUG: _leases 반납 누락 — 누수 시뮬레이션 */ }
        }
    }
}

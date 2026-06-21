using System;
using System.Collections.Generic;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S25-c — Horizon A: 복구 요새 통합 검증 (헤르메틱 가상 MMF 카오스 + Lock-Free Catch-Up).
    //   §1 메모리 배리어 catch-up(미션1)  §2 무디스크 카오스 인젝션→Truncated/freeze 단언(미션2)
    //   §3 하드게이트 후방호환·store 등가성·파일부재 무동결 자가당착 차단(미션3)
    //
    //  디스크 부작용 0 — 모든 카오스는 InMemoryJournalStore(byte[] 백킹)에서 결정론적으로 재생되며,
    //  실제 파일시스템을 깨지 않으므로 후속 1,271 테스트 스위트를 flaky 하게 만들 수 없다.
    public sealed class S25cChaosHarnessTests
    {
        private const int Cap = 16;

        private sealed class TestClock
        {
            public DateTime Now = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            public DateTime Get() => Now;
        }

        private static JournalRecord[] JRecs(int n)
        {
            var a = new JournalRecord[n];
            for (int i = 0; i < n; i++)
                a[i] = new JournalRecord(i + 1, RecoveryKind.Rejection, 2, 0, i * 1000, i * 1000 + 877, 500, i * 1.5, 77);
            return a;
        }

        // 복합 레이아웃 결정론 오프셋(CompositeForensicLedger 와 동일): FileHdr8 + GenomeHdr8 + GenomeSize + ForensicHdr12
        private static int ForensicDataOffset() => 8 + 8 + CompositeJournalCodec.GenomeSize + 12;

        // ═══════════════════ §1 메모리 배리어 catch-up (미션1) ═══════════════════

        [Fact]
        public void RunCycleOnce_CatchUp_UsesLatestPrimedWindow_NotStale()
        {
            // 사이클 진입점의 단일 Volatile.Read(acquire) 가 '가장 신선한' Prime 윈도우를 잡는다(catch-up).
            //  짧은 윈도우(<60) → 조기 반환(무진화). 완전 윈도우 재prime → catch-up 이 최신을 잡아 진화.
            var seeds = GreenhouseActivator.DefaultSeeds();
            var gh = new GreenhouseService(new PromotionGateway());

            var shortWin = new List<Candle>();
            foreach (var c in GreenhouseActivator.BootstrapCandles()) { shortWin.Add(c); if (shortWin.Count >= 30) break; }
            gh.Prime(seeds, shortWin, Market.KRX_KOSPI);
            gh.RunCycleOnce();
            Assert.Equal(0, gh.Probe().Mutations);   // snap.Length<60 → 조기 반환(스냅샷이 짧음)

            gh.Prime(seeds, new List<Candle>(GreenhouseActivator.BootstrapCandles()), Market.KRX_KOSPI);
            gh.RunCycleOnce();
            Assert.True(gh.Probe().Mutations > 0);    // catch-up 이 최신 완전 윈도우를 잡아 진화
        }

        [Fact]
        public void RunCycleOnce_StampsCycleStartFreshness_AsOf()
        {
            // ★적대적 리뷰(major) 회귀 — 챔피언의 As-Of 가 cycle-start 에서 snap 과 역순으로 가둔 snapAsOf
            //   (=Prime 신선도)로 박제되는지 확인. 사이클 끝 _lastFreshTicks 재독출이었다면 도중 Prime 의
            //   과대신선이 새어들어 신선도 게이트가 fail-open 되었다. 플러밍이 snapAsOf 를 Submit 에 전달함을 보증.
            var clk = new TestClock();
            var gw  = new PromotionGateway();
            var gh  = new GreenhouseService(gw) { Clock = clk.Get };
            gh.Prime(GreenhouseActivator.DefaultSeeds(), new List<Candle>(GreenhouseActivator.BootstrapCandles()), Market.KRX_KOSPI);
            gh.RunCycleOnce();
            Assert.NotNull(gw.Current);
            Assert.Equal(clk.Now.Ticks, gw.Current!.AsOfTicks);   // cycle-start 신선도(Prime 시각) 박제 — 과대신선 아님
        }

        // ═══════════════════ §2 무디스크 카오스 인젝션 (미션2) ═══════════════════

        [Fact]
        public void Chaos_TornWrite_AnyTruncation_Truncated_FailsClosed()
        {
            // ★적대적 리뷰(minor) 수정 — 코덱의 길이 가드(src.Length<total)는 '이진'(전부 아니면 무)이라
            //   레코드/섹터 경계를 인식하는 분기가 없다. 따라서 [8, total) 의 '어떤' 절단 길이든(레코드 경계든,
            //   레코드 중간이든, 최소 헤더든, 후미 1바이트 부족이든) 동일하게 Truncated→freeze 임을 전수 증명한다.
            //   (이전엔 '레코드 중간 절단' 단언이 코드 경로와 무관해 vacuous 했다 — 가드가 경계를 안 보기 때문.)
            int total = CompositeJournalCodec.RequiredBytes(Cap);
            int fData = ForensicDataOffset();
            int rsz   = CompositeJournalCodec.RecordSize;
            int[] keeps = { 8, fData - 1, fData, fData + rsz, fData + 2 * rsz + 25, total - 1 };   // 헤더직후·데이터직전·경계·경계+1·중간·후미부족
            foreach (int keep in keeps)
            {
                Assert.InRange(keep, 8, total - 1);
                var store = new InMemoryJournalStore();
                new CrashRecoveryJournaler(store, Cap).PersistComposite(new GenomeSnapshot(7, 49, 2.0, 1000, 3), JRecs(5));
                store.SimulateTornWrite(keep);

                var att = new OrderAttestationService();
                var jReboot = new CrashRecoveryJournaler(store, Cap);   // '재부팅' — 같은 backing 위 새 journaler
                var r = jReboot.RecoverOrFreezeComposite(att, new JournalRecord[Cap], out int n, out _);
                Assert.Equal(JournalStatus.Truncated, r.File);
                Assert.True(r.AnyCorrupt);
                Assert.Equal(0, n);
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 어떤 절단이든 엄격 동결
            }
        }

        [Fact]
        public void Chaos_TornWrite_DirectCodec_NoThrow_Truncated()
        {
            // 코덱 자체 — 구조체 중간 절단 span 을 받아도 길이 가드(src.Length<total)가 선발동하므로
            // 부분 레코드 OOB 파싱·예외 없이 클린 Truncated 를 반환한다(미션2 코덱 정밀도 직증).
            int total = CompositeJournalCodec.RequiredBytes(Cap);
            var buf = new byte[total];
            CompositeJournalCodec.Encode(new GenomeSnapshot(1, 7, 1, 1000, 3), JRecs(5), Cap, buf);
            int keep = ForensicDataOffset() + 2 * CompositeJournalCodec.RecordSize + 25;

            var dest = new JournalRecord[Cap];
            var ex = Record.Exception(() =>
            {
                var r = CompositeJournalCodec.Decode(buf.AsSpan(0, keep), Cap, dest, out int n);
                Assert.Equal(JournalStatus.Truncated, r.File);
                Assert.Equal(0, n);
            });
            Assert.Null(ex);   // 예외 전무 — 클린 길이 가드 경로
        }

        [Fact]
        public void Chaos_PowerLoss_ZeroedTail_Truncated_FailsClosed()
        {
            // MMF 현실 모드 — 파일은 capacity 선할당(풀사이즈)이나 후미 매직이 미플러시(0)로 남은 torn.
            var store = new InMemoryJournalStore();
            new CrashRecoveryJournaler(store, Cap).PersistComposite(new GenomeSnapshot(9, 63, 3.0, 2000, 3), JRecs(4));
            int total = CompositeJournalCodec.RequiredBytes(Cap);
            store.ZeroFrom(total - 4);   // FileTail 소실(풀사이즈 유지)

            var att = new OrderAttestationService();
            var r = new CrashRecoveryJournaler(store, Cap).RecoverOrFreezeComposite(att, new JournalRecord[Cap], out _, out var g);
            Assert.Equal(JournalStatus.Truncated, r.File);   // 후미 매직 0 → Truncated
            Assert.True(r.AnyCorrupt);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(9, g.Seq);                          // 게놈 섹터(앞쪽·온전)는 여전히 복구됨(섹터 독립)
        }

        [Fact]
        public void Chaos_ForensicBitFlip_GenomeSurvives_ButFailsClosed()
        {
            // 포렌식 데이터 1비트 오염 → 포렌식 BadCrc(fail-closed). 그러나 독립 CRC 의 게놈 섹터는 무손상 복구.
            var store = new InMemoryJournalStore();
            new CrashRecoveryJournaler(store, Cap).PersistComposite(new GenomeSnapshot(123, 861, 4.5, 3000, 2), JRecs(3));
            store.Corrupt(ForensicDataOffset() + 2);   // 포렌식 섹터 내부 비트 플립

            var att = new OrderAttestationService();
            var r = new CrashRecoveryJournaler(store, Cap).RecoverOrFreezeComposite(att, new JournalRecord[Cap], out _, out var g);
            Assert.Equal(JournalStatus.BadCrc, r.Forensic);
            Assert.Equal(JournalStatus.Ok,     r.Genome);     // 섹터 독립 — 게놈 생존
            Assert.True(r.AnyCorrupt);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(123, g.Seq);                          // 게놈 비트 정확 복구
            Assert.Equal(4.5, g.Fitness, 6);
        }

        [Fact]
        public void Chaos_GenomeBitFlip_ForensicSurvives_ButFailsClosed()
        {
            // ★적대적 리뷰(minor) 보강 — 역방향 섹터 독립(정방향과 대칭). 게놈 데이터 1비트 오염 → 게놈 BadCrc
            //   (fail-closed). 그러나 독립 CRC 의 포렌식 섹터는 무손상 복구된다. store 위임·재부팅 경로로 직증.
            var store = new InMemoryJournalStore();
            new CrashRecoveryJournaler(store, Cap).PersistComposite(new GenomeSnapshot(321, 99, 1.5, 5000, 1), JRecs(3));
            store.Corrupt(8 + 8 + 2);   // 게놈 데이터 영역(FileHdr8 + GenomeHdr8 = 16, +2). 매직/CRC 비적중.

            var att = new OrderAttestationService();
            var dest = new JournalRecord[Cap];
            var r = new CrashRecoveryJournaler(store, Cap).RecoverOrFreezeComposite(att, dest, out int n, out _);
            Assert.Equal(JournalStatus.BadCrc, r.Genome);
            Assert.Equal(JournalStatus.Ok,     r.Forensic);   // 섹터 독립 — 포렌식 생존
            Assert.Equal(3, n);                                // 포렌식 레코드 정상 복구
            Assert.Equal(3, dest[2].Seq);
            Assert.True(r.AnyCorrupt);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Chaos_PowerLossBeforeWrite_NoFile_Empty_NoFreeze()
        {
            // 미션3 자가당착 차단 — 쓰기 시작 전 전원차단 = 파일 부재. 정상 신규 부팅(Empty), 절대 동결 금지.
            var store = new InMemoryJournalStore();   // 한 번도 쓰지 않음(부재)
            var att = new OrderAttestationService();
            var r = new CrashRecoveryJournaler(store, Cap).RecoverOrFreezeComposite(att, new JournalRecord[Cap], out _, out _);
            Assert.Equal(JournalStatus.Empty, r.File);
            Assert.False(r.AnyCorrupt);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 멀쩡한 환경 무동결
        }

        [Fact]
        public void Chaos_Inaccessible_IoLock_NoFreeze()
        {
            // I/O 락·권한(증거 無)은 오염 아님 — 비동결(자가당착 차단). 포렌식 복구만 생략.
            var store = new InMemoryJournalStore();
            new CrashRecoveryJournaler(store, Cap).PersistComposite(new GenomeSnapshot(1, 7, 1, 1000, 3), JRecs(2));
            store.SetInaccessible(true);

            var att = new OrderAttestationService();
            var r = new CrashRecoveryJournaler(store, Cap).RecoverOrFreezeComposite(att, new JournalRecord[Cap], out _, out _);
            Assert.Equal(JournalStatus.Inaccessible, r.File);
            Assert.False(r.AnyCorrupt);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Chaos_Reboot_Replay_BitExact()
        {
            // 정상 종료→재부팅 — 새 journaler 가 같은 backing 을 읽어 게놈+포렌식을 비트 정확 재생.
            var store = new InMemoryJournalStore();
            var recs  = JRecs(5);
            Assert.True(new CrashRecoveryJournaler(store, Cap).PersistComposite(new GenomeSnapshot(55, 385, 7.25, 9000, 1), recs));

            var dest = new JournalRecord[Cap];
            var r = new CrashRecoveryJournaler(store, Cap).RecoverComposite(dest, out int n, out var g);
            Assert.Equal(JournalStatus.Ok, r.File);
            Assert.False(r.AnyCorrupt);
            Assert.Equal(55, g.Seq);
            Assert.Equal(7.25, g.Fitness, 6);
            Assert.Equal(5, n);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(recs[i].Seq, dest[i].Seq);
                Assert.Equal(recs[i].Fitness, dest[i].Fitness, 6);
                Assert.Equal(recs[i].GenomeHash, dest[i].GenomeHash);
            }
        }

        // ═══════════════════ §3 하드게이트·후방호환·등가성 (미션3) ═══════════════════

        [Fact]
        public void Store_NullArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new MmfJournalStore(null!));
            Assert.Throws<ArgumentNullException>(() => new CrashRecoveryJournaler((IJournalStore)null!, Cap));
            Assert.Throws<ArgumentNullException>(() => new CrashRecoveryJournaler((string)null!, Cap));
        }

        [Fact]
        public void Store_Equivalence_MmfVsInMemory_ByteIdentical()
        {
            // 가상 MMF 가 실제 MMF 의 비트 트윈임을 증명 — 카오스 결과가 프로덕션으로 그대로 전이된다.
            string path = Path.Combine(Path.GetTempPath(), "ms2e_s25c_" + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                var g    = new GenomeSnapshot(42, 777, 2.5, 1000, 3);
                var recs = JRecs(4);

                new CrashRecoveryJournaler(path, Cap).PersistComposite(g, recs);   // 실제 MMF 경로
                byte[] mmfImage = File.ReadAllBytes(path);

                var store = new InMemoryJournalStore();
                new CrashRecoveryJournaler(store, Cap).PersistComposite(g, recs);   // 가상 backing
                byte[]? memImage = store.Image;

                Assert.NotNull(memImage);
                Assert.Equal(mmfImage.Length, memImage!.Length);
                Assert.Equal(mmfImage, memImage);   // 바이트 완전 동일
            }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        [Fact]
        public void Store_BackwardCompat_FlatPersistRecover_StillWorks()
        {
            // 경로 생성자(MmfJournalStore)로 기존 플랫 포맷 round-trip — S24 호출부 무손상 후방호환.
            string path = Path.Combine(Path.GetTempPath(), "ms2e_s25c_flat_" + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                var j = new CrashRecoveryJournaler(path, Cap);
                Assert.True(j.Persist(JRecs(3)));
                var dest = new JournalRecord[Cap];
                Assert.Equal(JournalStatus.Ok, j.Recover(dest, out int n));
                Assert.Equal(3, n);
                Assert.Equal(3, dest[2].Seq);
            }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }
    }
}

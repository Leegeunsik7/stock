// ============================================================================
// MmfForensicAuditTests — [MMF 하드닝] 블랙박스 파일 회복 · 링 랩 경합 · 시퀀스 포화 · 콜드스타트 0x00 정돈 · 포렌식 체인 격리/시각 사상 ·
//                         체인 변조 Disarm 인터락
//
//   수정 전 실측(Red):
//     · 헤더는 정상인데 파일이 잘린 블랙박스 — 그대로 이어받아 꼬리 슬롯이 0 으로 조용히 확장(보존 이관 없음)
//     · 헤더 시퀀스가 long 상한 근처 — 몇 건 기록 후 seq % cap 음수 → 음수 오프셋 예외(블랙박스 영구 사망)
//     · 전원 손실(헤더만 전진·레코드 페이지 미반영) — 재기동 후 ReadAll 이 0x00 유령 레코드 반환
//     · 다중 라이터 — _writeSeq++ 경합으로 같은 슬롯 이중 기록(레코드 유실)
//     · 포렌식 체인 검증 결과를 무장 술어가 소비하지 않음 · 기록 시각 부재 · 결함 링크 특정/격리 불가
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MmfForensicAuditTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mmf_forensic_" + Guid.NewGuid().ToString("N"));
        public MmfForensicAuditTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* 무관 */ } }

        private string File_(string name = "bb.mmf") => Path.Combine(_dir, name);
        private const int Header = 64, Ev = 64;

        private static FlightRecordEvent Rec(long seq) => new(seq, seq * 10, 5930, seq * 7, ~seq, seq ^ 0x5A5A, 1, 2, 3, 0);

        private static bool Consistent(in FlightRecordEvent r)
            => r.Sequence > 0 && r.TimestampTicks == r.Sequence * 10 && r.Arg0 == r.Sequence * 7 && r.Arg1 == ~r.Sequence && r.Arg2 == (r.Sequence ^ 0x5A5A);

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        // ═══ §1 블랙박스 헤더 검증 · 복구 ═══

        [Fact]
        public void Truncated_File_With_Valid_Header_Is_Preserved_Aside_And_Restarted()
        {
            string path = File_();
            using (var w = new MmfCircularBlackBoxWriter(path, 16)) for (int i = 1; i <= 10; i++) w.Append(Rec(i));
            using (var fs = new FileStream(path, FileMode.Open)) fs.SetLength(Header + 5 * Ev);   // 부분 복사/동기화로 잘린 파일
            using var w2 = new MmfCircularBlackBoxWriter(path, 16);
            Assert.True(w2.PreservedIncompatibleFile);                                            // 수정 전: false(그대로 이어받음)
            Assert.Equal(0, w2.WriteSeq);
            Assert.Single(Directory.GetFiles(_dir, "bb.mmf.incompatible.*.bak"));                 // 포렌식 증거 보존(삭제 0)
        }

        [Fact]
        public void Near_Overflow_Header_Sequence_Rebases_Instead_Of_Crashing()
        {
            string path = File_();
            using (var w = new MmfCircularBlackBoxWriter(path, 8)) w.Append(Rec(1));
            using (var fs = new FileStream(path, FileMode.Open))
            {
                fs.Position = 16;
                fs.Write(BitConverter.GetBytes(long.MaxValue - 3));                                // 손상/초장기 가동 시퀀스
            }
            using var w2 = new MmfCircularBlackBoxWriter(path, 8);
            for (int i = 2; i <= 20; i++) w2.Append(Rec(i));                                      // 수정 전: 음수 오프셋 예외
            Assert.InRange(w2.WriteSeq, 8, 2L * 8 + 20);
            var dest = new FlightRecordEvent[8];
            int n = w2.ReadAll(dest);
            Assert.Equal(8, n);
            Assert.Equal(Enumerable.Range(13, 8).Select(x => (long)x), dest.Select(d => d.Sequence));   // 최신 8건 순서 보존
        }

        [Fact]
        public void Cold_Start_Trims_Zero_Tail_From_Power_Loss_And_Skips_Zero_Holes()
        {
            string path = File_();
            using (var w = new MmfCircularBlackBoxWriter(path, 32)) for (int i = 1; i <= 10; i++) w.Append(Rec(i));
            using (var fs = new FileStream(path, FileMode.Open))
            {
                fs.Position = 16; fs.Write(BitConverter.GetBytes(13L));                              // 헤더만 3건 전진(레코드 페이지 미반영)
                fs.Position = Header + 4L * Ev; fs.Write(new byte[Ev]);                             // 중간 슬롯(seq 5) 0x00 구멍
            }
            using var w2 = new MmfCircularBlackBoxWriter(path, 32);
            Assert.Equal(3, w2.ColdStartTrimmedZeroSlots);
            Assert.Equal(1, w2.ColdStartZeroHoles);
            Assert.Equal(10, w2.WriteSeq);                                                         // 시퀀스 후퇴 — 다음 기록이 그 자리부터
            var dest = new FlightRecordEvent[32];
            int n = w2.ReadAll(dest);
            Assert.Equal(9, n);                                                                    // 수정 전: 13건(0x00 유령 4건 포함)
            Assert.All(dest.Take(n), r => Assert.True(Consistent(r)));
            w2.Append(Rec(11));
            Assert.Equal(11, w2.WriteSeq);
        }

        [Fact]
        public void Negative_Sequence_Or_Capacity_Mismatch_Still_Preserved_Aside()
        {
            string path = File_();
            using (var w = new MmfCircularBlackBoxWriter(path, 8)) w.Append(Rec(1));
            using (var fs = new FileStream(path, FileMode.Open)) { fs.Position = 16; fs.Write(BitConverter.GetBytes(-5L)); }
            using (var w2 = new MmfCircularBlackBoxWriter(path, 8)) { Assert.True(w2.PreservedIncompatibleFile); w2.Append(Rec(1)); }
            using var w3 = new MmfCircularBlackBoxWriter(path, 16);                                // 용량 변경
            Assert.True(w3.PreservedIncompatibleFile);
        }

        // ═══ §1 링 랩 경합 · 다중 라이터 ═══

        [Fact]
        public void Concurrent_Writers_Lose_No_Record()
        {
            string path = File_();
            using var w = new MmfCircularBlackBoxWriter(path, 1 << 15);
            const int threads = 4, per = 5000;
            using var go = new ManualResetEventSlim(false);
            var ts = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                go.Wait();
                for (int i = 0; i < per; i++) w.Append(Rec(t * per + i + 1));
            })).ToList();
            ts.ForEach(t => t.Start()); go.Set(); ts.ForEach(t => t.Join());
            Assert.Equal(threads * per, w.WriteSeq);                                                // 수정 전: seq++ 경합으로 < 20000
            var dest = new FlightRecordEvent[threads * per];
            Assert.Equal(threads * per, w.ReadAll(dest));
            Assert.Equal(threads * per, dest.Select(d => d.Sequence).Distinct().Count());            // 슬롯 이중 기록 0
            Assert.All(dest, r => Assert.True(Consistent(r)));
        }

        [Fact]
        public void ReadAll_During_Wrap_Never_Returns_Torn_Records()
        {
            string path = File_();
            using var w = new MmfCircularBlackBoxWriter(path, 64);
            long written = 0;
            using var stop = new ManualResetEventSlim(false);
            var writer = new Thread(() => { long s = 0; while (!stop.IsSet) { w.Append(Rec(++s)); Interlocked.Exchange(ref written, s); } });
            writer.Start();
            var dest = new FlightRecordEvent[64];
            int reads = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < deadline)
            {
                int n = w.ReadAll(dest);
                for (int i = 0; i < n; i++)
                {
                    Assert.True(Consistent(dest[i]), $"찢긴 레코드 seq={dest[i].Sequence}");
                    if (i > 0) Assert.Equal(dest[i - 1].Sequence + 1, dest[i].Sequence);           // 랩 중에도 연속 창
                }
                reads++;
            }
            stop.Set(); writer.Join();
            Assert.True(reads > 10 && Interlocked.Read(ref written) > 64 * 4, $"reads={reads} written={written}");
        }

        [Fact]
        public void Append_Steady_State_Is_Zero_Alloc()
        {
            using var w = new MmfCircularBlackBoxWriter(File_(), 1024);
            var ev = Rec(1);
            for (int i = 0; i < 2000; i++) w.Append(ev);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) w.Append(ev); }));
        }

        // ═══ §2 포렌식 체인 — 상세 판정 · 격리 · 시각 사상 ═══

        private sealed class StepClock { public long Now = 1_000; public long Tick() => Now += 10; }

        [Fact]
        public void Recorded_Time_Is_Atomically_Bound_And_Readable()
        {
            var clock = new StepClock();
            var s = new ZeroAllocForensicAuditStream(16, clock.Tick);
            s.Append(AuditEventKind.Arm, 7, timestampTicks: 500);
            var link = s.ReadLink(1);
            Assert.True(link.Live);
            Assert.Equal(500, link.TimestampTicks);   // 발주/이벤트 시각
            Assert.Equal(1_010, link.RecordedTicks);  // 체인 커밋 시각
            Assert.True(s.VerifyDetailed().Intact);
        }

        [Fact]
        public void Default_Record_Clock_Shares_Producer_Domain_For_Microsecond_Mapping()
        {
            // 생산자(DynamicRiskAdapter) 기본 이벤트 시계 = DateTime.UtcNow.Ticks. 수정 전 기록 기본값(Stopwatch.GetTimestamp)은 도메인이 달라
            // 지연이 수십만 년 단위 음수로 나왔다(사상 불가).
            var s = new ZeroAllocForensicAuditStream(16);
            long eventTicks = DateTime.UtcNow.Ticks;
            s.Append(AuditEventKind.Arm, 1, eventTicks);
            var link = s.ReadLink(1);
            Assert.InRange(link.CommitLatencyMicros, 0, 5_000_000);   // 발주 시점 → 커밋 시점 µs(같은 도메인)
            Assert.InRange(link.RecordedTicks, eventTicks, DateTime.UtcNow.Ticks);
            Assert.Equal(0, s.ForwardDatedEvents);
            Assert.True(s.VerifyDetailed().Intact);
        }

        [Fact]
        public void Forward_Dated_Event_Is_Counted_Not_Treated_As_Tamper()
        {
            var s = new ZeroAllocForensicAuditStream(16, () => 1_000);
            s.Append(AuditEventKind.RiskViolation, 1, timestampTicks: 5_000);   // 이벤트가 커밋보다 미래(시계 스텝)
            Assert.Equal(1, s.ForwardDatedEvents);
            Assert.True(s.ReadLink(1).CommitLatencyMicros < 0);
            Assert.True(s.VerifyDetailed().Intact);                             // 오격발 Disarm 방지 — 계측만
        }

        [Fact]
        public void Record_Clock_Regression_Is_Clamped_And_Counted_Chain_Stays_Intact()
        {
            long[] times = { 100, 200, 150, 300 };
            int k = 0;
            var s = new ZeroAllocForensicAuditStream(8, () => times[k++]);
            for (int i = 0; i < 4; i++) s.Append(AuditEventKind.RiskViolation, i, i);
            Assert.Equal(1, s.ClockRegressions);
            Assert.Equal(200, s.ReadLink(3).RecordedTicks);   // 역행분은 직전값으로 클램프(단조)
            Assert.True(s.VerifyDetailed().Intact);
        }

        [Fact]
        public void Tampered_Link_Is_Located_Reasoned_And_Quarantined()
        {
            var s = new ZeroAllocForensicAuditStream(16, new StepClock().Tick);
            for (int i = 0; i < 10; i++) s.Append(AuditEventKind.Arm, i, i);
            s.__AdversarialCorruptForTest(6, 3);
            var v = s.VerifyDetailed();
            Assert.False(v.Intact);
            Assert.Equal(6, v.FirstBrokenSequence);
            Assert.Equal(ForensicBreakKind.HashMismatch, v.Reason);
            Assert.Equal(5, v.VerifiedLinks);
            Assert.True(s.Compromised);
            var q = s.ReadLink(6);
            Assert.True(q.Quarantined);
            Assert.False(q.Live);
            Assert.True(s.ReadLink(5).Live);
        }

        [Fact]
        public void Rewound_Recorded_Time_Is_Detected()
        {
            var s = new ZeroAllocForensicAuditStream(16, new StepClock().Tick);
            for (int i = 0; i < 6; i++) s.Append(AuditEventKind.Disarm, i, i);
            s.__AdversarialRewindRecordedForTest(4, 1_000_000);   // 기록 흔적 지우기(해시 미재계산)
            var v = s.VerifyDetailed();
            Assert.False(v.Intact);
            Assert.Equal(4, v.FirstBrokenSequence);
            Assert.True(v.Reason is ForensicBreakKind.RecordClockRegressed or ForensicBreakKind.HashMismatch);
        }

        [Fact]
        public void Genesis_Forgery_Still_Detected_With_New_Layout()
        {
            var s = new ZeroAllocForensicAuditStream(16, new StepClock().Tick);
            for (int i = 0; i < 3; i++) s.Append(AuditEventKind.Arm, i, i);
            s.__AdversarialForgeFirstLinkForTest();
            Assert.Equal(ForensicBreakKind.GenesisAnchor, s.VerifyDetailed().Reason);
        }

        [Fact]
        public void VerifyDetailed_And_Append_Are_Zero_Alloc()
        {
            var s = new ZeroAllocForensicAuditStream(1024, new StepClock().Tick);
            for (int i = 0; i < 1024; i++) s.Append(AuditEventKind.RiskViolation, i, i);
            for (int i = 0; i < 3; i++) s.VerifyDetailed();
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 20; i++) s.VerifyDetailed(); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2000; i++) s.Append(AuditEventKind.Arm, i, i); }));
        }

        // ═══ §3 방향 B — 체인 변조 Disarm 인터락 ═══

        [Fact]
        public void Intact_Chain_Does_Not_Trip()
        {
            var s = new ZeroAllocForensicAuditStream(16, new StepClock().Tick);
            for (int i = 0; i < 5; i++) s.Append(AuditEventKind.Arm, i, i);
            using var g = new ForensicChainDisarmInterlock(s);
            Assert.True(g.Check().Intact);
            Assert.False(g.ArmingBlocked);
        }

        [Fact]
        public void Chain_Tamper_Trips_Zeroizes_Tokens_And_Blocks_Arming_Sticky()
        {
            var s = new ZeroAllocForensicAuditStream(16, new StepClock().Tick);
            for (int i = 0; i < 5; i++) s.Append(AuditEventKind.Arm, i, i);
            var purger = new VolatileMemoryPurger();
            purger.Register(new byte[] { 1, 2, 3 });
            var token = new PoisonedSecretBuffer(new byte[] { 0x7F });
            int kills = 0;
            using var g = new ForensicChainDisarmInterlock(s, purger, _ => kills++, token);
            s.__AdversarialCorruptForTest(3, 0);
            var v = g.Check();
            Assert.False(v.Intact);
            Assert.True(g.ArmingBlocked);
            Assert.True(purger.AllZero);
            Assert.True(token.IsPoisoned);
            Assert.Equal(1, kills);
            Assert.Equal(3, g.TripVerdict.FirstBrokenSequence);
            g.Check();
            Assert.Equal(1, kills);                                                                 // sticky — 재격발 0
        }

        [Fact]
        public void Background_Monitor_Detects_Tamper()
        {
            var s = new ZeroAllocForensicAuditStream(64, new StepClock().Tick);
            for (int i = 0; i < 20; i++) s.Append(AuditEventKind.Arm, i, i);
            using var tripped = new ManualResetEventSlim(false);
            using var g = new ForensicChainDisarmInterlock(s, onTrip: _ => tripped.Set());
            g.StartMonitor(TimeSpan.FromMilliseconds(10));
            s.__AdversarialCorruptForTest(11, 5);
            Assert.True(tripped.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(g.ArmingBlocked);
        }

        [Fact]
        public void Check_Is_Zero_Alloc()
        {
            var s = new ZeroAllocForensicAuditStream(256, new StepClock().Tick);
            for (int i = 0; i < 256; i++) s.Append(AuditEventKind.Arm, i, i);
            using var g = new ForensicChainDisarmInterlock(s);
            for (int i = 0; i < 3; i++) g.Check();
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 50; i++) g.Check(); }));
        }

        [Fact]
        public void Host_Arming_Predicate_Reads_Forensic_Chain_Interlock()
        {
            var getter = typeof(ForensicChainDisarmInterlock).GetProperty(nameof(ForensicChainDisarmInterlock.ArmingBlocked))!.GetMethod!;
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool found = false;
            foreach (var t in typeof(StockManager.Blazor.BlazorHomeForm).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                foreach (var m in t.GetMethods(All))
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int k = 0; k + 4 < il.Length && !found; k++)
                        if (il[k] is 0x28 or 0x6F)
                            try { found = m.Module.ResolveMethod(BitConverter.ToInt32(il, k + 1)) == getter; } catch (ArgumentException) { }
                }
            Assert.True(found, "BlazorHomeForm 무장 술어가 ForensicChainDisarmInterlock.ArmingBlocked 를 판독하지 않음");
        }
    }
}

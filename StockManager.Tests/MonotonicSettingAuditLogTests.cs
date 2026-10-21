// ============================================================================
// MonotonicSettingAuditLogTests — [v12.18.2-DEV · 추천 A] 강화 전용 설정 감사 원장(MMF 해시 체인) 검증
//
//  §1 체인 형성 · 레이아웃: 레코드 = 128B · 순번 · 직전 해시 = 앞 레코드 해시(첫 = 헤더 해시) · 재계산 해시 일치 · 파일 재열기 = 같은 머리.
//  §2 δ=0: Append(재검증 + 직렬화 + 해시) · Verify · PollIntegrity 힙 0(MinAllocated 3창).
//  §3 ★1비트 변조 전수: 헤더 · 각 레코드 128B 의 모든 바이트 위치(대표 레코드) · 꼬리 빈 슬롯 — 정적 검증 전부 거부 ·
//     실행 중 원장 = 다음 기입(AuditFrozen) · 주기 판정(PollIntegrity) 거부 + 비트 27 · 이후 강화 기입 0.
//  §4 일관 재해시 위조(체인 자체는 맞음) · 꼬리 절단 = 실행 중 메모리 앵커가 거부 · CLI --expect-head/--expect-count 외부 앵커.
//  §5 기입기 결선: 강화 = 선기록 후 기입 · 거부도 기록 · 저장 실패 = 강화 + StoreFault 2건 · 미결속(null) = AuditFrozen · 가득 참 = 동결 + 비트 27 ·
//     거부 예산(분당 60 · 3/4 이후 중단 · 생략 수 누계) · 시계 역행 = 직전 시각 유지.
//  §6 기동 검증: 손상 파일 · 크기 이탈 · 다른 프로세스가 쓰기 잠금 = 동결 + 비트 27 · 실 호스트 비트마스크 결속.
//  §7 출하 IL 가드 ⑪(선기록 · ldnull) 1바이트 패치 · CLI --verify-audit-chain 종료 코드 · 1ms 검증 · 회귀(오중 빌드 · 래칫 · 인터락 30).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Cryptography;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Engines;
using StockManager.Security;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MonotonicSettingAuditLogTests
    {
        private readonly ITestOutputHelper _out;
        public MonotonicSettingAuditLogTests(ITestOutputHelper o) => _out = o;

        private static string TempFile() => Path.Combine(Path.GetTempPath(), "ms2e_audit_" + Guid.NewGuid().ToString("N"), "tighten-audit.mmf");
        private static void Cleanup(string f) { try { Directory.Delete(Path.GetDirectoryName(f)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        /// <summary>결정론 시계(1초씩 증가).</summary>
        private sealed class Clock { public long T = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc).Ticks; public long Next() => T += TimeSpan.TicksPerSecond; }

        private sealed class MemStore : ITightenSettingStore
        {
            public readonly Dictionary<string, string> Db = new();
            public bool FailWrite;
            public int Reads;
            public string? ReadEnvironment(string key) { Reads++; return null; }
            public string? ReadStored(string key) { Reads++; return Db.TryGetValue(key, out var v) ? v : null; }
            public void WriteStored(string key, string value) { if (FailWrite) throw new IOException("db"); Db[key] = value; }
        }

        private const string MaxBp = "Strategy.AdaptiveMaxBp", Conf = "Strategy.ConfidenceThresholdBp", Gate = "Strategy.SignalGateEnabled";

        private static byte[] Snapshot(string file) { using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); var b = new byte[fs.Length]; fs.ReadExactly(b); return b; }

        // ════════════ §1 체인 형성 ════════════

        [Fact]
        public void Chain_Formed_LayoutHashesLinked_ReopenSameHead()
        {
            string f = TempFile();
            try
            {
                var clock = new Clock();
                var anomaly = new StrategyAnomalyInterlock();
                byte[] head = new byte[32];
                using (var log = MonotonicSettingAuditLog.Open(f, anomaly, 64, clock.Next))
                {
                    var st = new MemStore();
                    Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9700", st, log));
                    Assert.Equal(TightenVerdict.Loosening, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9600", st, log));
                    Assert.Equal(TightenVerdict.UnknownKey, TightenOnlyUiSettingWriter.TryWrite("Dispatch.ValveWired", "true", st, log));
                    Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(Gate, "true", st, log));
                    Assert.Equal(4, log.Count);
                    log.CopyHead(head);
                }
                byte[] img = Snapshot(f);
                Assert.Equal(MonotonicSettingAuditLog.RegionBytes(64), img.Length);
                Assert.Equal("MS2EAUDT"u8.ToArray(), img[..8]);
                var prev = img.AsSpan(96, 32).ToArray();                                       // 헤더 해시 = 제네시스
                Assert.Equal(SHA256.HashData(img.AsSpan(0, 96)), prev);
                var expect = new (short Key, TightenVerdict V, long Cur, long Prop)[]
                {
                    (3, TightenVerdict.Tightened, 9_500, 9_700), (3, TightenVerdict.Loosening, 9_700, 9_600), (-1, TightenVerdict.UnknownKey, 0, 0), (0, TightenVerdict.Tightened, 0, 1),
                };
                long lastTicks = 0;
                for (int i = 0; i < 4; i++)
                {
                    var r = img.AsSpan(128 + i * 128, 128);
                    Assert.Equal((ulong)(i + 1), BinaryPrimitives.ReadUInt64LittleEndian(r));
                    long t = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
                    Assert.True(t > lastTicks); lastTicks = t;
                    Assert.Equal(expect[i], (BinaryPrimitives.ReadInt16LittleEndian(r[16..]), (TightenVerdict)r[18], BinaryPrimitives.ReadInt64LittleEndian(r[32..]), BinaryPrimitives.ReadInt64LittleEndian(r[40..])));
                    Assert.Equal(prev, r.Slice(64, 32).ToArray());
                    Assert.Equal(SHA256.HashData(r[..96]), r.Slice(96, 32).ToArray());
                    prev = r.Slice(96, 32).ToArray();
                }
                Assert.Equal(MonotonicSettingAuditLog.KeyHash("Dispatch.ValveWired"), BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(128 + 2 * 128 + 24)));   // 미등재 키 = 원문 대신 해시
                Assert.Equal(2, img[128 + 0 * 128 + 19]);                                       // AdaptiveMaxBp 규칙 = NonDecreasing
                Assert.Equal(prev, head);
                Assert.True(img.AsSpan(128 + 4 * 128).IndexOfAnyExcept((byte)0) < 0);

                // 재열기 = 전체 검증 Ok · 같은 머리 · 이어 쓰기
                using var again = MonotonicSettingAuditLog.Open(f, anomaly, 64, clock.Next);
                Assert.False(again.Frozen);
                Assert.Equal(4, again.Count);
                byte[] h2 = new byte[32]; again.CopyHead(h2);
                Assert.Equal(head, h2);
                Assert.True(again.Append(1, "Strategy.AdaptiveThreshold", TightenVerdict.Unchanged, 1, 1));
                Assert.Equal(5, again.Count);
                Assert.False(anomaly.Tripped);
            }
            finally { Cleanup(f); }
        }

        // ════════════ §2 δ=0 ════════════

        [Fact]
        public void Append_Verify_Poll_ZeroAlloc()
        {
            var anomaly = new StrategyAnomalyInterlock();
            var clock = new Clock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 4096, clock.Next);
            for (int i = 0; i < 8; i++) log.Append(3, MaxBp, TightenVerdict.Tightened, 9_500 + i, 9_501 + i);   // JIT 예열
            long append = MinAllocated(() => { for (int i = 0; i < 200; i++) log.Append(3, MaxBp, TightenVerdict.Tightened, i, i + 1); });
            long poll = MinAllocated(() => { for (int i = 0; i < 20; i++) log.PollIntegrity(); });
            var st = new MemStore();
            long write = MinAllocated(() => { for (int i = 0; i < 50; i++) TightenOnlyUiSettingWriter.TryWrite(Conf, "1", st, log); });   // 거부(완화) 경로 · 예산 내 기록
            _out.WriteLine($"[δ] Append 200 = {append}B · PollIntegrity 20 = {poll}B · 기입기 거부 경로 50 = {write}B · 레코드 {log.Count}");
            Assert.Equal(0, append);
            Assert.Equal(0, poll);
            Assert.False(log.Frozen);
            Assert.False(anomaly.Tripped);
        }

        // ════════════ §3 1비트 변조 ════════════

        private static (string File, byte[] Image) BuildLedger(int records, int capacity = 32)
        {
            string f = TempFile();
            var clock = new Clock();
            using (var log = MonotonicSettingAuditLog.Open(f, new StrategyAnomalyInterlock(), capacity, clock.Next))
                for (int i = 0; i < records; i++) Assert.True(log.Append(i % 6, "k" + i, (TightenVerdict)(i % 2 == 0 ? 0 : 6), 100 + i, 101 + i));
            return (f, Snapshot(f));
        }

        [Fact]
        public void OneBitTamper_EveryByte_Header_Record_Tail_StaticVerifyRejects()
        {
            var (f, img) = BuildLedger(5);
            try
            {
                Span<byte> head = stackalloc byte[32];
                Assert.Equal(AuditChainVerdict.Ok, MonotonicSettingAuditLog.Verify(img, out int n, head, out _));
                Assert.Equal(5, n);
                int flips = 0;
                var seen = new Dictionary<AuditChainVerdict, int>();
                // 헤더 128B 전부 · 레코드 #2(가운데) 128B 전부 · 마지막 레코드 128B 전부 · 꼬리 빈 슬롯 샘플 — 각 바이트 최하위 · 최상위 비트
                var offsets = Enumerable.Range(0, 128).Concat(Enumerable.Range(128 + 2 * 128, 128)).Concat(Enumerable.Range(128 + 4 * 128, 128))
                                        .Concat(new[] { 128 + 5 * 128, 128 + 5 * 128 + 77, img.Length - 1 });
                foreach (int off in offsets)
                    foreach (byte bit in new byte[] { 0x01, 0x80 })
                    {
                        img[off] ^= bit;
                        var v = MonotonicSettingAuditLog.Verify(img, out _, head, out _);
                        img[off] ^= bit;
                        flips++;
                        Assert.True(v != AuditChainVerdict.Ok, $"오프셋 {off} 비트 0x{bit:X2} 변조 미감지");
                        seen[v] = seen.GetValueOrDefault(v) + 1;
                    }
                _out.WriteLine($"[1비트 변조] {flips}건 전부 거부 · " + string.Join(" · ", seen.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
                Assert.Equal(AuditChainVerdict.Ok, MonotonicSettingAuditLog.Verify(img, out _, head, out _));   // 원상 복구 = Ok
                // 타임스탬프만 조작(+1틱) = 레코드 해시 불일치 · 순서 교환 = 순번/체인 불일치
                long t = BinaryPrimitives.ReadInt64LittleEndian(img.AsSpan(128 + 3 * 128 + 8));
                BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(128 + 3 * 128 + 8), t + 1);
                Assert.Equal(AuditChainVerdict.RecordHashMismatch, MonotonicSettingAuditLog.Verify(img, out _, head, out int at));
                Assert.Equal(3, at);
                BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(128 + 3 * 128 + 8), t);
                var swapped = (byte[])img.Clone();
                img.AsSpan(128 + 1 * 128, 128).CopyTo(swapped.AsSpan(128 + 2 * 128));
                img.AsSpan(128 + 2 * 128, 128).CopyTo(swapped.AsSpan(128 + 1 * 128));
                Assert.Equal(AuditChainVerdict.SequenceGap, MonotonicSettingAuditLog.Verify(swapped, out _, head, out _));
            }
            finally { Cleanup(f); }
        }

        /// <summary>실행 중 원장 파일을 외부에서 1비트 변조(공유 쓰기 — 다른 프로세스 흉내: 별도 MMF 매핑으로 같은 페이지 수정).</summary>
        private static void FlipInPlace(MonotonicSettingAuditLog log, long offset, byte bit)
        {
            // 원장은 FileShare.Read 로 열려 있다 → 일반 파일 쓰기는 거부된다(정상 운영 방어). 테스트는 이름 없는 익명 원장 대신
            //  파일 원장을 쓰므로, 같은 프로세스에서 같은 파일 매핑에 접근할 수 있는 유일한 길 = 원장 영역 포인터. 반사로 기반 포인터를 꺼낸다.
            var f = typeof(MonotonicSettingAuditLog).GetField("_base", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            unsafe
            {
                byte* p = (byte*)System.Reflection.Pointer.Unbox(f.GetValue(log)!);
                p[offset] ^= bit;
            }
        }

        [Fact]
        public void LiveTamper_NextWriteFrozen_PollTrips_Bit27_AllLaterWritesBlocked()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            var anomaly = new StrategyAnomalyInterlock();
            anomaly.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            Assert.False(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            var clock = new Clock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 64, clock.Next);
            var st = new MemStore();
            Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9600", st, log));
            Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9700", st, log));

            FlipInPlace(log, 128 + 0 * 128 + 40, 0x01);                                      // 첫 레코드 '제안값' 최하위 비트(9600 → 9601)
            int readsBefore = st.Reads;
            var sw = Stopwatch.StartNew();
            var v = TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9800", st, log);
            sw.Stop();
            _out.WriteLine($"[실행 중 변조] 다음 기입 {v} · 감지까지 {sw.Elapsed.TotalMicroseconds:F0}µs · 원장 {log.LastVerdict} · 동결 {log.FreezeCause} · 비트27 {mask.IsRaised(ArmingInterlockSource.StrategyAnomaly)}");
            Assert.Equal(TightenVerdict.AuditFrozen, v);
            Assert.Equal("9700", st.Db[MaxBp]);                                               // ★기입 0
            Assert.Equal(AuditChainVerdict.RecordHashMismatch, log.LastVerdict);
            Assert.True(anomaly.Tripped);
            Assert.Equal(StrategyAnomalyKind.AuditChainBroken, anomaly.FirstCause);
            Assert.True(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));                   // ★비트 27 원자 Disarm
            // 이후 모든 시도 = 판독조차 없이 AuditFrozen(강화 · 거부 대상 · 미등재 전부)
            foreach (var (k, val) in new[] { (MaxBp, "9900"), (Gate, "true"), (Conf, "9000"), ("Dispatch.ValveWired", "true") })
                Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(k, val, st, log));
            Assert.Equal(readsBefore + 2, st.Reads);                                          // 감지 직전 1회 판독(env · stored)뿐
            Assert.Equal("9700", st.Db[MaxBp]);
            Assert.False(st.Db.ContainsKey(Gate));
            FlipInPlace(log, 128 + 0 * 128 + 40, 0x01);                                       // 원상 복구해도 동결 유지(sticky)
            Assert.False(log.PollIntegrity());
            Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(Gate, "true", st, log));
            // 물리 이중키 재무장 = 비트 27 해제 가능 · 원장 동결은 프로세스 수명 동안 유지(설정 변경 계속 차단)
            Assert.True(anomaly.TryRearm(true, true));
            Assert.False(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(Gate, "true", st, log));
        }

        [Fact]
        public void LiveTamper_NoWrites_PollIntegrity_Detects_AndTrips()
        {
            var anomaly = new StrategyAnomalyInterlock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 64, new Clock().Next);
            Assert.True(log.Append(0, Gate, TightenVerdict.Tightened, 0, 1));
            Assert.True(log.PollIntegrity());
            FlipInPlace(log, 128 + 64 + 5, 0x80);                                              // 직전 해시 필드
            Assert.False(log.PollIntegrity());
            Assert.Equal(AuditChainVerdict.PrevHashMismatch, log.LastVerdict);
            Assert.True(anomaly.Tripped);
            Assert.Equal(1L, anomaly.AuditChainBreaks);
        }

        // ════════════ §4 일관 재해시 위조 · 절단 · 외부 앵커 ════════════

        [Fact]
        public void ConsistentRehashForgery_And_Truncation_RejectedByMemoryAnchor()
        {
            var anomaly = new StrategyAnomalyInterlock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 64, new Clock().Next);
            for (int i = 0; i < 3; i++) Assert.True(log.Append(3, MaxBp, TightenVerdict.Tightened, 9_500 + i, 9_501 + i));
            // 공격: 레코드 #1 제안값 위조 후 #1 · #2 해시를 전부 다시 계산 — 정적 검증은 통과하는 '일관된' 체인
            var p = typeof(MonotonicSettingAuditLog).GetField("_base", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            unsafe
            {
                byte* b = (byte*)System.Reflection.Pointer.Unbox(p.GetValue(log)!);
                var region = new Span<byte>(b, (int)MonotonicSettingAuditLog.RegionBytes(64));
                BinaryPrimitives.WriteInt64LittleEndian(region[(128 + 128 + 40)..], 5_000);
                for (int i = 1; i < 3; i++)
                {
                    var r = region.Slice(128 + i * 128, 128);
                    region.Slice(128 + (i - 1) * 128 + 96, 32).CopyTo(r[64..]);
                    SHA256.HashData(r[..96], r[96..]);
                }
                Span<byte> h = stackalloc byte[32];
                Assert.Equal(AuditChainVerdict.Ok, MonotonicSettingAuditLog.Verify(region, out _, h, out _));   // 정적 = 구분 불가(정직한 한계)
            }
            Assert.False(log.Append(3, MaxBp, TightenVerdict.Tightened, 9_502, 9_503));
            Assert.Equal(AuditChainVerdict.AnchorMismatch, log.LastVerdict);                  // ★메모리 앵커(머리 해시) 불일치
            Assert.True(anomaly.Tripped);

            // 꼬리 절단(마지막 레코드 0 으로) — 정적 = 짧은 정상 체인 · 메모리 앵커 = 개수 불일치
            var a2 = new StrategyAnomalyInterlock();
            using var log2 = MonotonicSettingAuditLog.CreateVolatile(a2, 64, new Clock().Next);
            for (int i = 0; i < 3; i++) Assert.True(log2.Append(0, Gate, TightenVerdict.Unchanged, 1, 1));
            unsafe
            {
                byte* b = (byte*)System.Reflection.Pointer.Unbox(p.GetValue(log2)!);
                new Span<byte>(b + 128 + 2 * 128, 128).Clear();
            }
            Assert.False(log2.PollIntegrity());
            Assert.Equal(AuditChainVerdict.AnchorMismatch, log2.LastVerdict);
            Assert.True(a2.Tripped);
        }

        // ════════════ §5 기입기 결선 ════════════

        [Fact]
        public void Writer_WriteAhead_StoreFault_Unbound_Full_Budget_ClockRegression()
        {
            var clock = new Clock();
            var anomaly = new StrategyAnomalyInterlock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 8, clock.Next);
            var st = new MemStore();
            // 미결속(null 원장) = 판독 0 · 기입 0
            Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9900", st, null));
            Assert.Equal(0, st.Reads);
            Assert.Empty(st.Db);
            // 저장 실패 = 강화 레코드(선기록) + StoreFault 레코드
            st.FailWrite = true;
            Assert.Equal(TightenVerdict.StoreFault, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9600", st, log));
            st.FailWrite = false;
            Assert.Equal(2, log.Count);
            // 시계 역행(NTP) = 직전 시각 유지(체인 비감소 · 동결 아님)
            clock.T -= TimeSpan.TicksPerHour;
            Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9600", st, log));
            Assert.False(log.Frozen);
            Assert.True(log.PollIntegrity());
            // 거부 예산: 용량 8 의 3/4 = 6 이후 거부 레코드 중단(생략 수 누계) — 강화는 계속 기록
            for (int i = 0; i < 5; i++) TightenOnlyUiSettingWriter.TryWrite(MaxBp, "5000", st, log);
            Assert.Equal(6, log.Count);                                                         // 3 + 거부 3(6 도달)
            Assert.Equal(2, log.SuppressedRejects);
            Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9700", st, log));
            Assert.Equal(TightenVerdict.Tightened, TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9800", st, log));
            Assert.Equal(8, log.Count);
            Assert.False(anomaly.Tripped);
            // 가득 참 = 다음 강화에서 동결 + 비트 27 · 기입 0
            var v = TightenOnlyUiSettingWriter.TryWrite(MaxBp, "9900", st, log);
            Assert.Equal(TightenVerdict.AuditFrozen, v);
            Assert.Equal("9800", st.Db[MaxBp]);
            Assert.Equal(AuditFreezeCause.Full, log.FreezeCause);
            Assert.Equal(StrategyAnomalyKind.AuditLedgerFull, anomaly.FirstCause);
            // 생략 수는 다음 레코드에 누계(#7 = 강화 9700 이 생략 2 를 실음)
            Span<byte> head = stackalloc byte[32];
            var p = typeof(MonotonicSettingAuditLog).GetField("_base", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            unsafe
            {
                byte* b = (byte*)System.Reflection.Pointer.Unbox(p.GetValue(log)!);
                var region = new ReadOnlySpan<byte>(b, (int)MonotonicSettingAuditLog.RegionBytes(8));
                Assert.Equal(AuditChainVerdict.Ok, MonotonicSettingAuditLog.Verify(region, out int n, head, out _));
                Assert.Equal(8, n);
                Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(region[(128 + 6 * 128 + 20)..]));
                Assert.Equal((byte)TightenVerdict.Tightened, region[128 + 0 * 128 + 18]);
                Assert.Equal((byte)TightenVerdict.StoreFault, region[128 + 1 * 128 + 18]);
                long t1 = BinaryPrimitives.ReadInt64LittleEndian(region[(128 + 1 * 128 + 8)..]), t2 = BinaryPrimitives.ReadInt64LittleEndian(region[(128 + 2 * 128 + 8)..]);
                Assert.Equal(t1, t2);                                                            // 역행 시각 = 직전 유지
            }

            // 분당 60 예산(큰 원장): 61번째 거부부터 생략 · 1분 뒤 재개
            var c2 = new Clock();
            using var big = MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock(), 1024, () => c2.T);
            for (int i = 0; i < 100; i++) TightenOnlyUiSettingWriter.TryWrite(Conf, "1", st, big);
            Assert.Equal(MonotonicSettingAuditLog.RejectBudgetPerMinute, big.Count);
            Assert.Equal(40, big.SuppressedRejects);
            c2.T += TimeSpan.TicksPerMinute;
            TightenOnlyUiSettingWriter.TryWrite(Conf, "1", st, big);
            Assert.Equal(61, big.Count);
        }

        // ════════════ §6 기동 검증 ════════════

        [Fact]
        public void Startup_CorruptOrResizedOrLocked_FreezesAndTrips_HostBitmaskBound()
        {
            var (f, img) = BuildLedger(4);
            try
            {
                // 정상 재열기
                using (var ok = MonotonicSettingAuditLog.Open(f, new StrategyAnomalyInterlock(), 32)) Assert.False(ok.Frozen);
                // 1비트 손상 후 기동 = 동결 + 비트 27
                img[128 + 2 * 128 + 33] ^= 0x04;
                File.WriteAllBytes(f, img);
                var a1 = new StrategyAnomalyInterlock();
                using (var bad = MonotonicSettingAuditLog.Open(f, a1, 32))
                {
                    Assert.True(bad.Frozen);
                    Assert.Equal(AuditChainVerdict.RecordHashMismatch, bad.LastVerdict);
                    Assert.Equal(StrategyAnomalyKind.AuditChainBroken, a1.FirstCause);
                    Assert.Equal(TightenVerdict.AuditFrozen, TightenOnlyUiSettingWriter.TryWrite(Gate, "true", new MemStore(), bad));
                }
                // 크기 이탈(용량 설정 변경 · 잘린 파일) = 동결
                var a2 = new StrategyAnomalyInterlock();
                using (var resized = MonotonicSettingAuditLog.Open(f, a2, 64)) Assert.True(resized.Frozen && a2.Tripped);
                // 다른 쓰기 핸들이 잡고 있음 = 열기 실패 = 동결(Unavailable)
                img[128 + 2 * 128 + 33] ^= 0x04;
                File.WriteAllBytes(f, img);
                using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var a3 = new StrategyAnomalyInterlock();
                    using var locked = MonotonicSettingAuditLog.Open(f, a3, 32);
                    Assert.Equal(AuditFreezeCause.Unavailable, locked.FreezeCause);
                    Assert.True(a3.Tripped);
                }
                // 꽉 찬 원장으로 기동 = 동결(Full) + 비트 27
                var (ff, _) = BuildLedger(4, capacity: 4);
                try
                {
                    var a4 = new StrategyAnomalyInterlock();
                    using var full = MonotonicSettingAuditLog.Open(ff, a4, 4);
                    Assert.Equal(AuditFreezeCause.Full, full.FreezeCause);
                    Assert.Equal(StrategyAnomalyKind.AuditLedgerFull, a4.FirstCause);
                }
                finally { Cleanup(ff); }
            }
            finally { Cleanup(f); }

            // 호스트 결선 소스: 원장 = 파일 · 싱글턴 ITightenAuditSink · 1초 무결성 폴링 · 어댑터 생성자 1개(원장 필수)
            string root = RepoRoot();
            string host = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("MonotonicSettingAuditLog.Open(", host);
            Assert.Contains("services.AddSingleton<MS2E.Infrastructure.ITightenAuditSink>(tightenAudit);", host);
            Assert.Contains("tightenAudit.PollIntegrity();", host);
            Assert.Single(typeof(AdminSettingWidgetAdapter).GetConstructors());
            Assert.Throws<ArgumentNullException>(() => new AdminSettingWidgetAdapter(new LocalDatabase(Path.Combine(Path.GetTempPath(), "ms2e_a_" + Guid.NewGuid().ToString("N") + ".db")), null!));
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        // ════════════ §7 출하 IL · CLI · 회귀 ════════════

        private static string HostDll() => typeof(ReleasePackager).Assembly.Location;
        private static ShippedBinaryLiveArmedGuard.Result ScanBytes(byte[] dll) { using var ms = new MemoryStream(dll); return ShippedBinaryLiveArmedGuard.Scan(ms); }

        [Fact]
        public void IlGuard_Rule11_WriteAheadAndNonNullAudit_OneBytePatches_Rejected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var ok = ScanBytes(dll);
            Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
            const string T = ShippedBinaryLiveArmedGuard.TightenWriterType;
            var cases = new List<(string Name, byte[] Bytes, string Expect)>();
            // ① TryWrite: Append 직후 조건 분기 → 무조건(br.s) — 기록 실패 무시 기입
            {
                var (off, _, ins) = TightenOnlySettingGuardTests.Body(dll, T, "TryWrite");
                int write = ins.FindIndex(i => i.Op == OpCodes.Callvirt && MethodName(dll, i.Operand) == "WriteStored");
                int k = ins.FindLastIndex(write, i => i.Op == OpCodes.Callvirt && MethodName(dll, i.Operand) == "Append");
                Assert.True(k > 0 && ins[k + 1].Op.FlowControl == FlowControl.Cond_Branch, "Append 직후 " + ins[k + 1].Op.Name);
                Assert.True(ins[k + 1].Op.Size == 1 && ins[k + 1].Op.OperandType == OperandType.ShortInlineBrTarget, "짧은 분기 예상 " + ins[k + 1].Op.Name);
                var b = (byte[])dll.Clone(); b[off + ins[k + 1].At] = 0x2B;                     // br.s
                cases.Add(("Append 분기 → br.s", b, "감사 Append 결과로 분기하지 않음"));
                // ② Append 토큰 → 다른 인터페이스 멤버(get_Frozen) — 선기록 소실
                var b2 = (byte[])dll.Clone();
                BitConverter.GetBytes(TokenOf(dll, "ITightenAuditSink", "get_Frozen")).CopyTo(b2, off + ins[k].At + 1);
                cases.Add(("Append → get_Frozen", b2, "감사 Append 호출 0건"));
            }
            // ③ 진입점: 감사 인자(ldfld _audit) → ldnull; nop ×4
            {
                var (off, _, ins) = TightenOnlySettingGuardTests.Body(dll, "AdminSettingWidgetAdapter", "TightenSetting");
                int k = ins.FindIndex(i => i.Op == OpCodes.Call && MethodName(dll, i.Operand) == "TryWrite");
                Assert.Equal(OpCodes.Ldfld, ins[k - 1].Op);
                var b = (byte[])dll.Clone();
                int at = off + ins[k - 1].At;
                b[at] = 0x00; b[at + 1] = 0x00; b[at + 2] = 0x00; b[at + 3] = 0x00; b[at + 4] = 0x14;   // nop ×4 + ldnull(ldfld 5바이트 자리 · call 직전 = ldnull)
                int ld0 = ins.FindLastIndex(k - 1, i => i.Op == OpCodes.Ldarg_0);              // 앞의 ldarg.0(this) 도 nop — 스택 균형
                b[off + ins[ld0].At] = 0x00;
                cases.Add(("감사 인자 ldnull", b, "감사 원장 인자 ldnull"));
            }
            foreach (var (name, bytes, expect) in cases)
            {
                var r = ScanBytes(bytes);
                _out.WriteLine($"[⑪ 패치] {name} → {(r.Ok ? "Ok" : "Rejected")} · {string.Join(" | ", r.Violations)}");
                Assert.False(r.Ok, name);
                Assert.Contains(r.Violations, v => v.Contains(expect, StringComparison.Ordinal));
            }
            Assert.True(ScanBytes(dll).Ok);
        }

        internal static string? MethodName(byte[] dll, int token)
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(dll));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var h = System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(token);
            return h.Kind switch
            {
                System.Reflection.Metadata.HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle)h).Name),
                System.Reflection.Metadata.HandleKind.MemberReference => md.GetString(md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)h).Name),
                _ => null,
            };
        }

        internal static int TokenOf(byte[] dll, string type, string method)
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(dll));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) == method && md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) == type)
                    return System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh);
            }
            throw new InvalidOperationException(type + "." + method);
        }

        [Fact]
        public void Cli_VerifyAuditChain_ExitCodes_ExternalAnchor_LiveShare_UnderOneMs()
        {
            string f = TempFile();
            try
            {
                var anomaly = new StrategyAnomalyInterlock();
                var clock = new Clock();
                using var log = MonotonicSettingAuditLog.Open(f, anomaly, MonotonicSettingAuditLog.DefaultCapacity, clock.Next);
                for (int i = 0; i < MonotonicSettingAuditLog.DefaultCapacity; i++) Assert.True(log.Append(3, MaxBp, TightenVerdict.Tightened, i, i + 1));   // 가득 찬 원장(최악 검증 비용)
                byte[] head = new byte[32]; log.CopyHead(head);
                string headHex = Convert.ToHexString(head);
                int Run(params string[] a) { var w = new StringWriter(); int c = ReleaseHandshakeCli.Run(a, w); _out.WriteLine(w.ToString().TrimEnd()); return c; }
                Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { "--verify-audit-chain" }));
                // 실행 중 원장(쓰기 핸들 보유 — FileShare.Read)과 공존 판독
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-audit-chain", f));
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-audit-chain", f, "--expect-head", headHex, "--expect-count", MonotonicSettingAuditLog.DefaultCapacity.ToString()));
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-audit-chain", f, "--expect-head", new string('0', 64)));
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-audit-chain", f, "--expect-count", "5"));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--verify-audit-chain", f, "--expect-head", "xyz"));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--verify-audit-chain", Path.Combine(Path.GetDirectoryName(f)!, "absent.mmf")));

                // 전체 영역(가득 찬 2,048 레코드 · 256KB) 검증 시간 — 웜 최솟값
                Span<byte> h = stackalloc byte[32];
                long best = long.MaxValue;
                for (int i = 0; i < 25; i++)                                                 // 웜 최솟값(예산 여유 ≈ 25% — 부하 잡음은 최솟값으로 제거)
                {
                    Assert.Equal(AuditChainVerdict.Ok, MonotonicSettingAuditLog.VerifyFile(f, out int n, h, out _, out long us));
                    Assert.Equal(MonotonicSettingAuditLog.DefaultCapacity, n);
                    best = Math.Min(best, us);
                }
                _out.WriteLine($"[검증 시간] 2,048 레코드 전체 최솟값 {best}µs");
                Assert.True(best < 1_000, "전체 검증 ≥ 1ms: " + best + "µs");

                // 1비트 변조 파일(사본) = CLI 거부
                string copy = Path.Combine(Path.GetDirectoryName(f)!, "copy.mmf");
                byte[] img = Snapshot(f);
                img[128 + 1000 * 128 + 50] ^= 0x10;
                File.WriteAllBytes(copy, img);
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-audit-chain", copy));
                Assert.Equal(AuditChainVerdict.RecordHashMismatch, MonotonicSettingAuditLog.VerifyFile(copy, out _, h, out int at, out _));   // 예약 바이트도 해시 범위
                Assert.Equal(1000, at);
            }
            finally { Cleanup(f); }
        }

        [Fact]
        public void Regression_FiveBuilds_Ratchet_Interlocks_VerdictCodes()
        {
            string root = RepoRoot();
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, proj)));
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);                   // 감사 원장은 기존 비트 27(StrategyAnomaly) 재사용 — 새 비트 0
            Assert.Equal(27, (int)ArmingInterlockSource.StrategyAnomaly);
            Assert.Equal(Enum.GetValues<TightenVerdict>().Length, MS2E.UI.Portable.Ports.TightenVerdictCode.Count);
            Assert.Equal(7, MS2E.UI.Portable.Ports.TightenVerdictCode.AuditFrozen);
        }
    }
}

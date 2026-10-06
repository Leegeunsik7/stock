// ============================================================================
// MmfIpcFabricTests — v4.94.0 과제3: MMF 제로레이턴시 IPC 패브릭 (unsafe·δ=0)
//
//  §1 라운드트립·빔·백프레셔    — 발행/소비 일치·빈 링·가득참(부패 아님).
//  §2 오염 방어 → ForceFreeze   — 라이터/상대 하트비트 20ms 단절·매직 파손 → 격리+동결.
//  §3 레이턴시(µs)·SPSC torn=0·δ=0 무할당·Dispose·liveArmed:false.
//
//  ★공유메모리/IPC/스핀 테스트는 부하·타이밍 민감 → ConcurrencyStress 컬렉션(직렬화)에 편입.
// ============================================================================
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MmfIpcFabricTests
    {
        private sealed class MutClock { public long T; }
        private static long TotalBytes(int capacity, int maxPayload) => 384L + (long)capacity * (24 + maxPayload);

        // ── §1 라운드트립 · 빔 · 백프레셔 ──────────────────────────────────────

        [Fact]
        public void PublishConsume_RoundTrip_ExactBytes()
        {
            const string name = "MS2E_TEST_MMF_RT";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64);
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            Assert.True(w.TryPublish(payload));
            var dst = new byte[64];
            Assert.True(r.TryConsume(dst, out int len));
            Assert.Equal(5, len);
            Assert.Equal(payload, dst[..len]);
            Assert.Equal(1L, w.Published);
            Assert.Equal(1L, r.Consumed);
            Assert.False(w.IsCorrupted);
        }

        [Fact]
        public void Consume_Empty_ReturnsFalse_NoCorruption()
        {
            const string name = "MS2E_TEST_MMF_EMPTY";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 32);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 32);
            var dst = new byte[32];
            Assert.False(r.TryConsume(dst, out int len));
            Assert.Equal(0, len);
            Assert.False(r.IsCorrupted);
        }

        [Fact]
        public void Full_Backpressure_NotCorruption()
        {
            const string name = "MS2E_TEST_MMF_FULL";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 4, 8);
            var f = new byte[] { 9 };
            for (int i = 0; i < 4; i++) Assert.True(w.TryPublish(f));   // 용량 4 채움
            Assert.False(w.TryPublish(f));                              // 가득참
            Assert.False(w.IsCorrupted);                               // ★백프레셔는 부패 아님
        }

        // ── §2 오염 방어 → ForceFreeze ────────────────────────────────────────

        [Fact]
        public void WriterHeartbeatStale_Consume_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_HBSTALE";
            var att = new OrderAttestationService();
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, att, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, att, () => rc.T, TimeSpan.TicksPerSecond, 20);
            Assert.True(w.TryPublish(new byte[] { 1 }));       // 라이터 하트비트 = 1_000_000
            rc.T = 1_000_000 + 250_000;                        // 리더 시계 +25ms(> 20ms 임계=200_000 tick)
            var dst = new byte[64];
            Assert.False(r.TryConsume(dst, out _));
            Assert.True(r.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Contains("Corrupted", r.LastReason);
        }

        [Fact]
        public void PeerHeartbeatStale_CheckPeerAlive_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_PEER";
            var att = new OrderAttestationService();
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 32, att, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 32, att, () => rc.T, TimeSpan.TicksPerSecond, 20);
            r.Beat();                             // 리더 하트비트 = 1_000_000
            Assert.True(w.CheckPeerAlive());      // 아직 신선
            wc.T = 1_000_000 + 300_000;           // 라이터 시계 +30ms → 리더 하트비트 단절로 관측
            Assert.False(w.CheckPeerAlive());
            Assert.True(w.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void MagicCorruption_Consume_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_MAGIC";
            var att = new OrderAttestationService();
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, att);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, att);
            Assert.True(w.TryPublish(new byte[] { 7 }));
            // 공유메모리 매직(offset 0) 을 외부에서 파손.
            using (var raw = MemoryMappedFile.CreateOrOpen(name, TotalBytes(16, 64)))
            using (var acc = raw.CreateViewAccessor(0, 16))
                acc.Write(0, unchecked((long)0xDEADBEEFDEADBEEF));
            var dst = new byte[64];
            Assert.False(r.TryConsume(dst, out _));
            Assert.True(r.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Contains("Corrupted", r.LastReason);
        }

        [Fact]
        public void HeadCorruption_BoundsViolation_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_BOUNDS";
            var att = new OrderAttestationService();
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, att);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, att);
            Assert.True(w.TryPublish(new byte[] { 3 }));
            // Head(offset 64) 를 불변식 위배 값(음수)으로 파손 → head>=tail 위반.
            using (var raw = MemoryMappedFile.CreateOrOpen(name, TotalBytes(16, 64)))
            using (var acc = raw.CreateViewAccessor(64, 8))
                acc.Write(0, -5L);
            var dst = new byte[64];
            Assert.False(r.TryConsume(dst, out _));
            Assert.True(r.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ── §3 레이턴시 · SPSC torn=0 · δ=0 · Dispose · liveArmed ──────────────

        [Fact]
        public void Latency_ComputedInMicros()
        {
            const string name = "MS2E_TEST_MMF_LAT";
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, null, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, null, () => rc.T, TimeSpan.TicksPerSecond, 20);
            Assert.True(w.TryPublish(new byte[] { 1, 2 }));                  // pubTs = 1_000_000
            rc.T = 1_000_000 + 10 * TimeSpan.TicksPerMillisecond;           // +10ms(< 20ms 하트비트 임계)
            var dst = new byte[64];
            Assert.True(r.TryConsume(dst, out _));
            Assert.Equal(10_000L, r.LastLatencyMicros);                     // 10ms = 10,000µs
        }

        // ── [MMF Triage] 플레이키 근본 원인 · 결정론 하네스 ──────────────────────────────────────────────
        //  원 테스트(Spsc_Concurrent_InOrder_NoTornNoLoss)는 기본 하트비트 20ms 로 '순서/찢김' 을 검증했는데, 판정 경로에
        //  스케줄러 지연이 섞여 있었다:
        //   (R) 리더가 선점돼 >20ms 멈추면 링이 가득 차고, 라이터는 TryPublish=false(백프레셔)만 반복하며 하트비트를 갱신하지 않았다 →
        //       리더가 돌아와 '라이터 단절' 로 Poison(★엔진 결함 — 살아 있는 라이터 오판 · 교정 ⑤).
        //   (W) 라이터가 선점돼 >20ms 멈추면 빈 링을 폴링하던 리더가 정당하게 '단절' 판정(설계상 생존 감지) — 순서 검증 테스트가
        //       생존 임계에 묶여 있던 하네스 결함.
        //  교정 하네스: 전용 스레드 + Barrier 동시 출발 + 순서 검증은 생존 임계와 분리(60초) + 고유 MMF 이름 + 진행 종료 조건.
        //  생존 감지는 별도 결정론 테스트(주입 시계 · 주입 정지)로 검증한다.

        private static string UniqueName(string tag) => "MS2E_TEST_MMF_" + tag + "_" + Guid.NewGuid().ToString("N");

        private sealed class SpscStats { public long Torn, Got, Rounds, WriterCorrupt, ReaderCorrupt; }

        /// <summary>
        /// 영속 스레드 결정론 SPSC 라운드 러너 — 라이터 1 · 리더 1 을 한 번 만들고 Barrier 로 라운드마다 동시 출발/합류.
        /// 프레임 = (i, ~i) 16B — 찢김(앞/뒤 8B 불일치) · 역순 · 유실 · 중복 전부 판정. 라운드마다 새 패브릭(고유 이름).
        /// </summary>
        private static SpscStats RunSpscRounds(int rounds, long perRound, int capacity, int heartbeatMs,
                                               Action<long>? writerHook = null, Action<long>? readerHook = null)
        {
            var st = new SpscStats();
            MmfIpcFabric? w = null, r = null;
            using var start = new Barrier(3);
            using var end = new Barrier(3);
            var wt = new Thread(() =>
            {
                var buf = new byte[16];
                for (int k = 0; k < rounds; k++)
                {
                    if (!start.SignalAndWait(60_000)) return;
                    var fw = Volatile.Read(ref w)!;
                    for (long i = 0; i < perRound; i++)
                    {
                        BitConverter.TryWriteBytes(buf.AsSpan(0, 8), i);
                        BitConverter.TryWriteBytes(buf.AsSpan(8, 8), ~i);
                        while (!fw.TryPublish(buf)) { if (fw.IsCorrupted) goto done; Thread.SpinWait(4); }
                        writerHook?.Invoke(i);
                    }
                done:
                    if (!end.SignalAndWait(60_000)) return;
                }
            }) { IsBackground = true, Name = "mmf-writer" };
            var rt = new Thread(() =>
            {
                var dst = new byte[32];
                for (int k = 0; k < rounds; k++)
                {
                    if (!start.SignalAndWait(60_000)) return;
                    var fr = Volatile.Read(ref r)!;
                    long got = 0;
                    while (got < perRound)
                    {
                        if (fr.TryConsume(dst, out int len))
                        {
                            long a = BitConverter.ToInt64(dst, 0), b = BitConverter.ToInt64(dst, 8);
                            if (len != 16 || a != got || b != ~got) Interlocked.Increment(ref st.Torn);
                            got++;
                            readerHook?.Invoke(got);
                        }
                        else if (fr.IsCorrupted) break;
                    }
                    Interlocked.Add(ref st.Got, got);
                    if (!end.SignalAndWait(60_000)) return;
                }
            }) { IsBackground = true, Name = "mmf-reader" };
            wt.Start(); rt.Start();
            for (int k = 0; k < rounds; k++)
            {
                string name = UniqueName("RND");
                Volatile.Write(ref w, new MmfIpcFabric(name, MmfFabricRole.Writer, capacity, 16, heartbeatTimeoutMs: heartbeatMs));
                Volatile.Write(ref r, new MmfIpcFabric(name, MmfFabricRole.Reader, capacity, 16, heartbeatTimeoutMs: heartbeatMs));
                Assert.True(start.SignalAndWait(60_000), $"라운드 {k} 출발 합류 타임아웃");
                Assert.True(end.SignalAndWait(120_000), $"라운드 {k} 종료 합류 타임아웃");
                if (w!.IsCorrupted) st.WriterCorrupt++;
                if (r!.IsCorrupted) st.ReaderCorrupt++;
                w.Dispose(); r.Dispose();
                st.Rounds++;
            }
            Assert.True(wt.Join(30_000)); Assert.True(rt.Join(30_000));
            return st;
        }

        [Fact]
        public void Spsc_Concurrent_InOrder_NoTornNoLoss()
        {
            // 교정 하네스판(원 테스트와 같은 규모 10만 프레임 · 용량 1024) — 순서/찢김 검증을 생존 임계(60초)와 분리.
            var st = RunSpscRounds(rounds: 1, perRound: 100_000, capacity: 1024, heartbeatMs: 60_000);
            Assert.Equal(0L, st.WriterCorrupt);
            Assert.Equal(0L, st.ReaderCorrupt);
            Assert.Equal(0L, st.Torn);
            Assert.Equal(100_000L, st.Got);
        }

        [Fact]
        public void Spsc_1000Rounds_Stress_ZeroTornZeroLoss()
        {
            // ★지시 불변식: 1,000 라운드 연속 · 용량 64(랩어라운드 + 백프레셔 상시) · 라운드당 4,096 프레임 — 찢김·유실·중복·오염 0.
            var st = RunSpscRounds(rounds: 1_000, perRound: 4_096, capacity: 64, heartbeatMs: 60_000,
                                   writerHook: i => { if ((i & 511) == 0) Thread.Yield(); });
            Assert.Equal(1_000L, st.Rounds);
            Assert.Equal(0L, st.WriterCorrupt);
            Assert.Equal(0L, st.ReaderCorrupt);
            Assert.Equal(0L, st.Torn);
            Assert.Equal(1_000L * 4_096, st.Got);
        }

        [Fact]
        public void Backpressure_ReaderStalled_WriterAlive_NotPoisoned_InjectedClock()
        {
            // [실결함 · 결정론 Red] 링 가득참 → 리더가 임계(20ms)보다 오래 정지 → 라이터는 백프레셔 재시도 중(살아 있음) →
            //   수정 전: 라이터 하트비트 미갱신 → 리더 복귀 시 '라이터 단절' Poison + ForceFreeze(허위).
            string name = UniqueName("BP");
            var att = new OrderAttestationService();
            var clock = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 4, 8, att, () => clock.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 4, 8, att, () => clock.T, TimeSpan.TicksPerSecond, 20);
            var f = new byte[] { 7 };
            for (int i = 0; i < 4; i++) Assert.True(w.TryPublish(f));
            clock.T += 500_000;                                      // 리더 50ms 정지(임계 20ms = 200,000 tick)
            Assert.False(w.TryPublish(f));                           // 가득참 — 라이터는 살아서 재시도
            Assert.Equal(1L, w.BackpressureBeats);
            var dst = new byte[8];
            Assert.True(r.TryConsume(dst, out int len));             // ★허위 단절 판정 없음
            Assert.Equal(1, len);
            Assert.False(r.IsCorrupted);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Backpressure_RealThreads_ReaderStall_NoFalsePoison()
        {
            // [실결함 · 실 스레드 Red] 리더를 40ms 씩 정지(선점 모사) — 임계 20ms · 용량 64 라 정지마다 링이 가득 찬다.
            //   수정 전: 정지 직후 첫 소비에서 Poison(재현 100%). 수정 후: 라이터 백프레셔 하트비트로 무오판.
            var st = RunSpscRounds(rounds: 1, perRound: 2_000, capacity: 64, heartbeatMs: 20,
                                   readerHook: got => { if (got % 500 == 0) Thread.Sleep(40); });
            Assert.Equal(0L, st.ReaderCorrupt);
            Assert.Equal(0L, st.WriterCorrupt);
            Assert.Equal(0L, st.Torn);
            Assert.Equal(2_000L, st.Got);
        }

        [Fact]
        public void LegacyHarness_WriterStall_BeyondHeartbeat_IsDetected_ByDesign()
        {
            // 원 하네스의 나머지 실패 경로(W): 라이터가 생존 임계보다 오래 멈추면 리더가 '단절' 판정 — 설계상 정당한 생존 감지다.
            //   ∴ 순서/찢김 검증 하네스를 20ms 임계에 묶으면 스케줄러 지연만으로 실패한다(결정론 재현). 교정 하네스는 임계를 분리.
            var st = RunSpscRounds(rounds: 1, perRound: 2_000, capacity: 64, heartbeatMs: 20,
                                   writerHook: i => { if (i == 500) Thread.Sleep(60); });
            Assert.Equal(1L, st.ReaderCorrupt);                      // ★스케줄링만으로 '단절' — 하네스가 생존 임계와 분리돼야 하는 이유
            Assert.True(st.Got < 2_000);
            Assert.Equal(0L, st.Torn);                               // 오염 판정은 데이터 찢김이 아니라 생존 판정
        }

        [Fact]
        public void Poison_RaisesInterlockBit_AndHostBindsFabric()
        {
            string name = UniqueName("IL");
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.MmfFabric);
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, null, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, null, () => rc.T, TimeSpan.TicksPerSecond, 20);
            r.BindInterlock(mask, ArmingInterlockSource.MmfFabric);
            Assert.True(mask.Seal(out _));
            Assert.False(mask.Blocked);
            Assert.True(w.TryPublish(new byte[] { 1 }));
            rc.T += 300_000;                                         // 라이터 30ms 무응답
            Assert.False(r.TryConsume(new byte[64], out _));
            Assert.True(r.IsCorrupted);
            Assert.True(mask.Blocked);                               // ★Poison 즉시 집계 비트

            var bind = typeof(MmfIpcFabric).GetMethod(nameof(MmfIpcFabric.BindInterlock))!;
            bool found = false;
            const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var types = new System.Collections.Generic.List<Type> { host };
            types.AddRange(host.GetNestedTypes(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public));
            foreach (var t in types)
                foreach (var m in System.Linq.Enumerable.Concat<System.Reflection.MethodBase>(t.GetMethods(All), t.GetConstructors(All)))
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null || found) continue;
                    for (int i = 0; i + 4 < il.Length && !found; i++)
                        if (il[i] is 0x28 or 0x6F)
                            try { found = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == bind; } catch (ArgumentException) { }
                }
            Assert.True(found, "BlazorHomeForm 이 MmfIpcFabric.BindInterlock 을 호출하지 않음");
        }

        [Fact]
        public void BackpressurePath_ZeroAllocation()
        {
            string name = UniqueName("BPA");
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 4, 8, heartbeatTimeoutMs: 60_000);
            var f = new byte[] { 1 };
            for (int i = 0; i < 4; i++) w.TryPublish(f);
            w.TryPublish(f);
            long min = long.MaxValue;
            for (int k = 0; k < 3 && min != 0; k++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100_000; i++) w.TryPublish(f);   // 가득참 백프레셔 + 하트비트 갱신 경로
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            Assert.Equal(0L, min);
            Assert.True(w.BackpressureBeats >= 100_000);
        }

        [Fact]
        public void HotLoop_PublishConsume_ZeroAllocation()
        {
            const string name = "MS2E_TEST_MMF_ALLOC";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 64, 32);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 64, 32);
            var frame = new byte[16];
            var dst   = new byte[32];
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 200_000; i++) { w.TryPublish(frame); r.TryConsume(dst, out _); }   // 워밍업
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long sink = 0;
                    for (int i = 0; i < 100_000; i++) { w.TryPublish(frame); if (r.TryConsume(dst, out int len)) sink += len; }
                    long after = GC.GetAllocatedBytesForCurrentThread();
                    GC.KeepAlive(sink);
                    alloc = after - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★unsafe 포인터 발행/소비 핫루프 힙 할당 0(δ=0)
        }

        [Fact]
        public void Dispose_ConcurrentWithHotOps_NoUseAfterFree()
        {
            // ★적대검증 회귀(결함C): Dispose 가 Beat/TryPublish 와 겹쳐도 인플라이트 드레인으로 언매핑 후 역참조(AccessViolation) 차단.
            const string name = "MS2E_TEST_MMF_DISPRACE";
            var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 64, 32);
            var frame = new byte[8];
            var stop = new bool[1];
            var hammer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop[0])) { w.Beat(); w.TryPublish(frame); }
            });
            hammer.Start();
            Thread.Sleep(25);   // 잠시 해머링(hot 연산 인플라이트 확보)
            w.Dispose();        // ★해머링 중 Dispose — 드레인 후 언매핑, 크래시 없어야 함
            Volatile.Write(ref stop[0], true);
            Assert.True(hammer.Join(10_000));
            Assert.False(w.TryPublish(frame));   // Dispose 후 no-op
        }

        [Fact]
        public void Dispose_Idempotent_NoOpAfter()
        {
            const string name = "MS2E_TEST_MMF_DISPOSE";
            var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 32);
            w.Dispose();
            w.Dispose();   // 재호출 무해
            Assert.False(w.TryPublish(new byte[] { 1 }));   // Dispose 후 전 연산 false
        }

        [Fact]
        public void LiveArmed_FabricOpen_TransportsBytes_ButNeverEmitsOrder()
        {
            const string name = "MS2E_TEST_MMF_ARMED";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64);
            // 주문처럼 보이는 프레임을 전송해도 패브릭은 '전송'만 — 외부 브로커 소켓 미오픈(intra-machine IPC).
            var orderLike = new byte[] { 10, 0, 0, 0, 1, 2, 3 };
            Assert.True(w.TryPublish(orderLike));
            var dst = new byte[64];
            Assert.True(r.TryConsume(dst, out _));
            Assert.False(w.IsCorrupted);
            // 실주문 송출 게이트는 여전히 수학적 0 — 패브릭 개통이 무장이 아니다.
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.False(DispatchArming.ResolvePredicate(wired: false, gate)());
        }
    }
}

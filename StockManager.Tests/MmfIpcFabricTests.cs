// ============================================================================
// MmfIpcFabricTests — v4.94.0 과제3: MMF 제로레이턴시 IPC 패브릭 (unsafe·δ=0)
//
//  §1 라운드트립·빔·백프레셔    — 발행/소비 일치·빈 링·가득참(부패 아님).
//  §2 오염 방어 → ForceFreeze   — 라이터/상대 하트비트 20ms 단절·매직 파손 → 격리+동결.
//  §3 레이턴시(µs)·SPSC torn=0·δ=0 무할당·Dispose·liveArmed:false.
//  [v12.18.4-DEV] 실 스레드 생존 판정 테스트 = 스텝 락 코디네이터(논리 시계 · 이벤트 순서 · Thread.Sleep 0) — OS 스케줄링 무관.
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
            // [v12.18.9-DEV] 드레인 규약(TeardownDrain): 합류 시간 초과 · 단언 실패로 빠져나가도 Barrier/MMF 를 '살아 있는 스레드 밑에서'
            //   폐기하지 않는다 — 취소 토큰으로 작업 스레드를 깨워 종료 확인 후 폐기(생존 = 격리 · 시험은 실패).
            var start = new Barrier(3);
            var end = new Barrier(3);
            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            bool roundOpen = false;
            var wt = new Thread(() =>
            {
                try
                {
                    var buf = new byte[16];
                    for (int k = 0; k < rounds; k++)
                    {
                        if (!start.SignalAndWait(60_000, ct)) return;
                        var fw = Volatile.Read(ref w)!;
                        for (long i = 0; i < perRound; i++)
                        {
                            BitConverter.TryWriteBytes(buf.AsSpan(0, 8), i);
                            BitConverter.TryWriteBytes(buf.AsSpan(8, 8), ~i);
                            while (!fw.TryPublish(buf)) { if (fw.IsCorrupted || ct.IsCancellationRequested) goto done; Thread.SpinWait(4); }
                            writerHook?.Invoke(i);
                        }
                    done:
                        if (!end.SignalAndWait(60_000, ct)) return;
                    }
                }
                catch (OperationCanceledException) { }
            }) { IsBackground = true, Name = "mmf-writer" };
            var rt = new Thread(() =>
            {
                try
                {
                    var dst = new byte[32];
                    for (int k = 0; k < rounds; k++)
                    {
                        if (!start.SignalAndWait(60_000, ct)) return;
                        var fr = Volatile.Read(ref r)!;
                        long got = 0;
                        while (got < perRound && !ct.IsCancellationRequested)
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
                        if (!end.SignalAndWait(60_000, ct)) return;
                    }
                }
                catch (OperationCanceledException) { }
            }) { IsBackground = true, Name = "mmf-reader" };
            bool joined = false;
            try
            {
                wt.Start(); rt.Start();
                for (int k = 0; k < rounds; k++)
                {
                    string name = UniqueName("RND");
                    Volatile.Write(ref w, new MmfIpcFabric(name, MmfFabricRole.Writer, capacity, 16, heartbeatTimeoutMs: heartbeatMs));
                    Volatile.Write(ref r, new MmfIpcFabric(name, MmfFabricRole.Reader, capacity, 16, heartbeatTimeoutMs: heartbeatMs));
                    roundOpen = true;
                    Assert.True(start.SignalAndWait(60_000), $"라운드 {k} 출발 합류 타임아웃");
                    Assert.True(end.SignalAndWait(120_000), $"라운드 {k} 종료 합류 타임아웃");
                    if (w!.IsCorrupted) st.WriterCorrupt++;
                    if (r!.IsCorrupted) st.ReaderCorrupt++;
                    w.Dispose(); r.Dispose();
                    roundOpen = false;
                    st.Rounds++;
                }
                joined = TeardownDrain.JoinAll(TimeSpan.FromSeconds(60), wt, rt);
            }
            finally
            {
                cts.Cancel();
                bool drained = TeardownDrain.JoinAll(TimeSpan.FromSeconds(30), wt, rt);
                TeardownDrain.DisposeOrQuarantine(drained, start, end, cts, roundOpen ? w : null, roundOpen ? r : null);
            }
            Assert.True(joined, "작업 스레드 60초 미종료");
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

        // ── [v12.18.4-DEV · 방향 A] 결정론 스텝 락 이벤트 배리어 ──────────────────────────────────────────────
        //  실 스레드 백프레셔 테스트가 전체 스위트에서 간헐 실패한 근본 원인(정직): 테스트가 '실 시계 + 20ms 생존 임계'에 묶여 있었다.
        //   리더 40ms 정지(Thread.Sleep) 뒤 라이터가 '마지막 20ms 안에' 백프레셔 하트비트를 찍어야 통과 — 라이터 스레드가 OS 에 20ms 이상
        //   선점되면(스위트 후반 · 메모리 압박 · 다른 프로세스) 리더는 정당하게 '라이터 단절'을 판정한다(설계상 생존 감지 · 엔진 정상).
        //   ∴ 실패는 엔진 결함이 아니라 '시간을 OS 스케줄러에 맡긴' 하네스 결함이다.
        //  교정: 시간을 OS 에서 빼앗는다. 두 패브릭은 코디네이터의 논리 시계(주입 nowTicks · 1 tick = 100ns)만 읽고, 논리 시계는
        //   코디네이터가 명시적으로 전진시킬 때만 움직인다. OS 가 어느 스레드를 얼마나 멈추든(20ms · 100ms · 1초) 논리 시간은 0 이다.
        //   리더 '정지'는 소비 중단 + 논리 시계 전진(40ms)으로 표현하고, 순서는 이벤트로 강제한다:
        //    ① 리더 소비 중단 → ArmBeats(현재 백프레셔 수) → 라이터가 가득 찬 링에서 백프레셔 하트비트를 1회 이상 찍을 때까지 대기(MRES).
        //       (리더가 멈춘 뒤의 백프레셔 = 링 가득 참 확정)
        //    ② 논리 시계 +40ms(임계 20ms 의 2배 — 하트비트 교정 ⑤가 없으면 반드시 Poison).
        //    ③ ArmBeats(현재 수 + 1) 대기 = 전진 '후' 시작된 백프레셔 하트비트 1회 이상 보장:
        //       캡처한 수 C 이후 C+1 번째 증가는 캡처 뒤에 일어났지만 그 하트비트의 시계 판독은 전진 전일 수 있다. C+2 번째의 시계 판독은
        //       C+1 증가(전체 펜스) 뒤 = 캡처 뒤 = 전진 뒤 — 라이터 프로그램 순서로 보장. 그래서 임계값 = C + 1(증가 수 > C+1 ⇔ ≥ C+2).
        //    ④ 소비 재개 — 리더의 now = 라이터 하트비트(같은 논리 시각) → 경과 0 ≤ 20ms → Poison 불가(수학적: 논리 시계 차 = 0).
        //   대조군(LegacyHarness): 라이터를 게이트에 세운 채(백프레셔 0) 같은 전진 → 리더는 반드시 '라이터 단절'(결정론 Red).
        //  대기 = ManualResetEventSlim(라이터가 백프레셔 직후 Notify) — 코어 1개여도 대기 리더는 블록되고 라이터가 진행한다(스핀 기아 없음).
        //  Thread.Sleep 0 · 실 시계 판정 0. 대기 상한(30초)은 교착 탐지용이며 판정에 쓰이지 않는다.
        private sealed class StepLockCoordinator : IDisposable
        {
            public const long Disarmed = long.MaxValue;
            private long _clock = 1_000_000, _threshold = Disarmed;
            private readonly ManualResetEventSlim _beat = new(false), _writerGate = new(true), _parked = new(false);
            private int _stopped;
            public long WaitTimeouts;
            public bool Stopped => Volatile.Read(ref _stopped) != 0;
            public void Stop() { Volatile.Write(ref _stopped, 1); _writerGate.Set(); }

            public long Now() => Volatile.Read(ref _clock);
            public void Advance(long ticks) => Interlocked.Add(ref _clock, ticks);

            /// <summary>라이터: 백프레셔(TryPublish=false) 직후 호출. 무장 임계 초과 = 대기자 깨움.</summary>
            public void NotifyBackpressure(long beats)
            {
                if (beats > Volatile.Read(ref _threshold)) _beat.Set();
            }

            /// <summary>
            /// 리더: 백프레셔 수가 threshold 를 넘을 때까지 대기. 반환 false = 교착(상한 초과).
            /// ★이벤트는 '힌트'이고 진실은 계수다(조건 변수 패턴 — 깨어나면 술어 재확인 · 아니면 다시 대기).
            ///  [실측 결함 · 25회 중 2회] 이전 단계의 지연된 Set: 라이터가 ①의 임계를 읽고(넘음) Set 하기 직전 선점 → 리더는 계수로 이미 통과 ·
            ///  전진 · ③ 무장(Reset) → 그 뒤 늦게 도착한 ①의 Set 이 ③ 대기를 '전진 뒤 하트비트' 없이 통과시켰다 → 소비 재개 = 정당한 단절 판정.
            ///  Reset→무장 순서만으로는 막을 수 없다(Set 주체가 옛 임계를 이미 읽었다) — 계수 재확인 루프가 유일한 정답.
            /// </summary>
            public bool WaitBeatsAbove(MmfIpcFabric writer, long threshold)
            {
                Volatile.Write(ref _threshold, threshold);
                long deadline = Environment.TickCount64 + 30_000;                        // 교착 탐지 전용(판정 무관)
                bool ok = true;
                while (writer.BackpressureBeats <= threshold)
                {
                    _beat.Reset();
                    if (writer.BackpressureBeats > threshold) break;                    // Reset 과 Set 사이의 증가 재확인
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0 || !_beat.Wait((int)left)) { ok = writer.BackpressureBeats > threshold; break; }
                }
                Volatile.Write(ref _threshold, Disarmed);
                if (!ok) Interlocked.Increment(ref WaitTimeouts);
                return ok;
            }

            /// <summary>
            /// 대조군: 라이터를 게이트에 세우고 '실제로 섰음'(parked)을 확인한 뒤 반환 — 게이트를 이미 지난 진행 중 발행이
            /// 끝난 뒤라야 시계를 전진시킬 수 있다(그 발행이 전진 후 시각을 하트비트로 찍는 경합 차단).
            /// </summary>
            public bool HoldWriter()
            {
                _parked.Reset();
                _writerGate.Reset();
                bool ok = _parked.Wait(30_000);
                if (!ok) Interlocked.Increment(ref WaitTimeouts);
                return ok;
            }
            public void ReleaseWriter() => _writerGate.Set();
            /// <summary>라이터: 발행 시도 전마다 호출. 게이트 닫힘 = parked 통지 후 대기. 반환 false = 중단.</summary>
            public bool WriterPass()
            {
                if (!_writerGate.IsSet) { _parked.Set(); if (!_writerGate.Wait(30_000)) return false; }
                return !Stopped;
            }

            public void Dispose() { _beat.Dispose(); _writerGate.Dispose(); _parked.Dispose(); }
        }

        private sealed class StepLockStats { public long Got, Torn, Stalls, ReaderCorrupt, WriterCorrupt, Beats, WaitTimeouts; public bool Joined; public string Reason = ""; }

        /// <summary>
        /// 스텝 락 SPSC 1 라운드 — 라이터 · 리더 전용 스레드 · 논리 시계 주입. 리더는 stallEvery 프레임마다 ①~④ 정지를 수행한다.
        /// holdWriterAtStall = 대조군(정지 동안 라이터 게이트 차단 → 백프레셔 하트비트 0 · 같은 전진 → 단절 판정 기대).
        /// burners = OS 스케줄링 교란(전 코어 초과 구독 스핀 스레드 — 실 시간 지연을 크게 만들어도 논리 판정이 불변임을 보인다).
        /// </summary>
        private static StepLockStats RunStepLock(long perRound, int capacity, long stallEvery, long stallTicks, bool holdWriterAtStall = false, int burners = 0)
        {
            var st = new StepLockStats();
            var co = new StepLockCoordinator();
            string name = UniqueName("STEP");
            var w = new MmfIpcFabric(name, MmfFabricRole.Writer, capacity, 16, null, co.Now, TimeSpan.TicksPerSecond, 20);
            var r = new MmfIpcFabric(name, MmfFabricRole.Reader, capacity, 16, null, co.Now, TimeSpan.TicksPerSecond, 20);
            var go = new CountdownEvent(2);                                     // 두 스레드 준비 래치
            var start = new ManualResetEventSlim(false);
            int stopBurn = 0, abort = 0;                                           // [v12.18.9-DEV] abort = 리더 드레인(라이터 사망 시 무한 양보 방지)
            var burn = new Thread[burners];
            for (int b = 0; b < burners; b++)
            {
                burn[b] = new Thread(() => { long x = 0; while (Volatile.Read(ref stopBurn) == 0) x++; GC.KeepAlive(x); }) { IsBackground = true, Name = "mmf-step-burner" };
                burn[b].Start();
            }
            var wt = new Thread(() =>
            {
                var buf = new byte[16];
                go.Signal(); start.Wait();
                for (long i = 0; i < perRound; i++)
                {
                    BitConverter.TryWriteBytes(buf.AsSpan(0, 8), i);
                    BitConverter.TryWriteBytes(buf.AsSpan(8, 8), ~i);
                    while (true)
                    {
                        if (!co.WriterPass()) return;
                        if (w.TryPublish(buf)) break;
                        if (w.IsCorrupted) return;
                        co.NotifyBackpressure(w.BackpressureBeats);
                        Thread.Yield();                                                // 코어 1개: 리더/코디네이터에 양보(시간 판정과 무관)
                    }
                }
            }) { IsBackground = true, Name = "mmf-step-writer" };
            var rt = new Thread(() =>
            {
                var dst = new byte[32];
                go.Signal(); start.Wait();
                long got = 0;
                while (got < perRound && Volatile.Read(ref abort) == 0)
                {
                    if (r.TryConsume(dst, out int len))
                    {
                        long a = BitConverter.ToInt64(dst, 0), b = BitConverter.ToInt64(dst, 8);
                        if (len != 16 || a != got || b != ~got) st.Torn++;
                        got++;
                        if (got % stallEvery == 0 && got < perRound)
                        {
                            // ① 소비 중단 → 링 가득 참(백프레셔) 확인
                            if (holdWriterAtStall && !co.HoldWriter()) break;
                            if (!holdWriterAtStall && !co.WaitBeatsAbove(w, w.BackpressureBeats)) break;
                            // ② 논리 시계 전진(리더 정지 = stallTicks)
                            co.Advance(stallTicks);
                            // ③ 전진 뒤 시작된 백프레셔 하트비트 1회 이상(대조군은 라이터 정지 — 하트비트 없음)
                            if (!holdWriterAtStall && !co.WaitBeatsAbove(w, w.BackpressureBeats + 1)) break;
                            st.Stalls++;
                            // ④ 소비 재개
                        }
                    }
                    else if (r.IsCorrupted) break;
                    else Thread.Yield();
                }
                st.Got = got;
                co.Stop();                                                             // 라이터 종료(가득 찬 링 재시도 탈출 · 게이트 해제)
            }) { IsBackground = true, Name = "mmf-step-reader" };
            // [v12.18.9-DEV] 드레인 규약: 준비 래치 단언 실패 · Join 시간 초과로 빠져나가도 이벤트 · MMF(비관리 뷰)를 살아 있는 스레드 밑에서 폐기하지 않는다.
            bool rj = false, wj = false, drained = false;
            try
            {
                wt.Start(); rt.Start();
                Assert.True(go.Wait(30_000), "스레드 준비 래치 타임아웃");
                start.Set();
                rj = rt.Join(60_000);
                co.Stop();
                wj = wt.Join(60_000);
                st.Joined = rj && wj;
                st.ReaderCorrupt = r.IsCorrupted ? 1 : 0;
                st.WriterCorrupt = w.IsCorrupted ? 1 : 0;
                st.Beats = w.BackpressureBeats;
                st.Reason = "리더[" + r.LastReason + "] 라이터[" + w.LastReason + "] got=" + st.Got + " stalls=" + st.Stalls;
                st.WaitTimeouts = co.WaitTimeouts;
            }
            finally
            {
                Volatile.Write(ref abort, 1); co.Stop(); start.Set();                     // 미출발 스레드도 깨워 즉시 종료
                Volatile.Write(ref stopBurn, 1);
                var all = new Thread[burn.Length + 2]; all[0] = wt; all[1] = rt; Array.Copy(burn, 0, all, 2, burn.Length);
                drained = TeardownDrain.JoinAll(TimeSpan.FromSeconds(30), all);
                TeardownDrain.DisposeOrQuarantine(drained, w, r, go, start, co);
            }
            return st;
        }

        [Fact]
        public void Backpressure_RealThreads_ReaderStall_NoFalsePoison()
        {
            // [실결함 · 실 스레드] 리더 40ms 정지(논리 시계) × 3회 — 임계 20ms · 용량 64 라 정지마다 링이 가득 찬다.
            //   수정 전 엔진(백프레셔 하트비트 없음): 정지 직후 첫 소비에서 Poison. 수정 후: 백프레셔 하트비트로 무오판(논리 시각 차 0).
            //   판정은 OS 스케줄링과 무관 — 시간은 코디네이터 전진으로만 흐른다(스텝 락 ①~④).
            var st = RunStepLock(perRound: 2_000, capacity: 64, stallEvery: 500, stallTicks: 40 * TimeSpan.TicksPerMillisecond);
            Assert.True(st.Joined, "스텝 락 교착");
            Assert.Equal(0L, st.WaitTimeouts);
            Assert.Equal(3L, st.Stalls);
            Assert.True(st.Beats >= 2 * st.Stalls, "정지마다 백프레셔 하트비트(가득 참 1 + 전진 뒤 1) " + st.Beats);
            Assert.True(st.ReaderCorrupt == 0, st.Reason);
            Assert.Equal(0L, st.WriterCorrupt);
            Assert.Equal(0L, st.Torn);
            Assert.Equal(2_000L, st.Got);
        }

        [Fact]
        public void Backpressure_StepLock_UnderCpuOversubscription_AndLongStalls_StillNoFalsePoison()
        {
            // 적대 조건: 전 코어 × 2 의 스핀 스레드로 OS 스케줄링을 교란 + 정지 길이 100ms(임계 5배) · 정지 20회.
            //   실 시계 하네스라면 라이터/리더 선점 지연이 그대로 판정에 섞인다. 스텝 락은 논리 시간만 보므로 결과가 같아야 한다.
            var st = RunStepLock(perRound: 10_000, capacity: 64, stallEvery: 500, stallTicks: 100 * TimeSpan.TicksPerMillisecond,
                                 burners: Math.Max(2, Environment.ProcessorCount * 2));
            Assert.True(st.Joined, "스텝 락 교착");
            Assert.Equal((0L, 19L), (st.WaitTimeouts, st.Stalls));
            Assert.Equal((0L, 0L, 0L), (st.ReaderCorrupt, st.WriterCorrupt, st.Torn));
            Assert.Equal(10_000L, st.Got);
        }

        [Fact]
        public void LegacyHarness_WriterStall_BeyondHeartbeat_IsDetected_ByDesign()
        {
            // 대조군(결정론 Red): 같은 정지(논리 +60ms) 동안 라이터를 게이트에 세워 하트비트를 끊으면 리더는 반드시 '라이터 단절' 판정 —
            //   설계상 정당한 생존 감지. 위 무오판이 '판정을 끈 결과'가 아니라 백프레셔 하트비트 덕분임을 같은 하네스로 입증한다.
            var st = RunStepLock(perRound: 2_000, capacity: 64, stallEvery: 500, stallTicks: 60 * TimeSpan.TicksPerMillisecond, holdWriterAtStall: true);
            Assert.True(st.Joined, "스텝 락 교착");
            Assert.Equal(1L, st.ReaderCorrupt);                      // ★정확히 첫 정지 뒤 첫 소비에서 단절
            Assert.Equal(500L, st.Got);
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

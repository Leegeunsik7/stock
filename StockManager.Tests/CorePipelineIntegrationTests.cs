// ============================================================================
// CorePipelineIntegrationTests — v12.0.3 Core System Pipeline Ignition & Multi-Broker Stress Suite
//   ★개별 검증된 코어 엔진들을 '파이프라인 형태'로 조립해 통합 무결을 지상 실증한다(단위 중복 아님 — 합성 증명).
//     Part 1 실시간 이중 브로커 수집·단절 3중 상호잠금 · Part 2 백테스트 스트림 격리·미세캡 CAS · Part 3 신규 이중 엔진.
//
//   ★정직 스코핑(수용된 전례): 실 키움 x86 COM(HTS)/토스 REST(MTS) 라이브 피드는 스텁 트랜스포트 + liveArmed:false +
//     실 자격증명 부재로 에이전트가 구동 불가(실 매매 0). 따라서 파이프라인은 ★실 어댑터가 구현하는 포트 계약과
//     동형인 시뮬레이션으로 증명한다(가짜 라이브 피드 날조 금지). "0-byte LOH" 는 ★틱 수집 핫패스(Publish/Drain)에
//     적용 — IndicatorMath 는 프레임당 결과 List(Gen0) 를 쓰므로 δ=0 아님(정직 명기).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Realtime;
using PortIndicatorMath = MS2E.UI.Portable.Infrastructure.IndicatorMath;   // ★RCL 순수 지표(호스트 IndicatorMath 와 동명 → 명시 별칭)
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class CorePipelineIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public CorePipelineIntegrationTests(ITestOutputHelper o) => _out = o;

        // 차트 소비용 캔들 뷰(ICandleView 구현 — 드레인된 틱을 지표 파이프라인에 사출). ★도메인 Candle 과 충돌 회피 개명.
        private sealed record PipeCandle(DateTime Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume) : ICandleView;

        private static PoisonedSecretBuffer NewSecret() =>
            new(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });

        // ── §1 실시간 이중 브로커 수집 — 인터리브 발행 → 순서 정합 드레인 ─────────────
        [Fact]
        public void RealtimeIngestion_DualBroker_DrainsMonotonic()
        {
            var co = new CrossBrokerAdaptiveRateCoalescer(1024);
            // 키움(0)·토스(1) 인터리브 발행.
            for (int i = 0; i < 500; i++)
            {
                co.Publish(0, 5930, 70_000 + i, 10);   // 키움
                co.Publish(1, 660, 120_000 + i, 5);    // 토스
            }
            Assert.Equal(1000, co.Produced);
            Assert.Equal(500, co.ProducedByBroker(0));
            Assert.Equal(500, co.ProducedByBroker(1));

            Span<BrokerTick> buf = stackalloc BrokerTick[1024];
            int n = co.Drain(buf);
            Assert.Equal(1000, n);   // 용량 내 → 전량 무손실
            long lastSeq = -1;
            for (int i = 0; i < n; i++) { Assert.True(buf[i].Seq > lastSeq); lastSeq = buf[i].Seq; }   // 전역 단조
            Assert.Equal(0, co.DroppedOldest);
        }

        // ── §2 비차단(non-blocking) — 생산자 절대 거부 없음(백프레셔 병목 제거) ─────────
        [Fact]
        public void AdaptiveCoalescer_NonBlocking_NeverRejectsProducer()
        {
            var co = new CrossBrokerAdaptiveRateCoalescer(64);
            long prevSeq = -1;
            // 용량의 10배 발행 — 기존 drop-newest 라면 대량 거부됐을 것. 이쪽은 전량 수용(단조 시퀀스).
            for (int i = 0; i < 640; i++)
            {
                long seq = co.Publish(i & 1, 100, 1_000 + i, 1);
                Assert.True(seq > prevSeq);   // ★거부 없이 유일 단조 시퀀스 배정
                prevSeq = seq;
            }
            Assert.Equal(640, co.Produced);   // 생산자 무차단
        }

        // ── §3 drop-oldest — 최신가 보존(오래된 것만 유실) ──────────────────────────
        [Fact]
        public void AdaptiveCoalescer_DropOldest_PreservesFreshest()
        {
            var co = new CrossBrokerAdaptiveRateCoalescer(16);
            long lastSeq = -1;
            for (int i = 0; i < 100; i++) lastSeq = co.Publish(0, 5930, 70_000 + i, 1);   // 용량 초과 발행(드레인 없이)

            Span<BrokerTick> buf = stackalloc BrokerTick[16];
            int n = co.Drain(buf);
            Assert.True(n > 0 && n <= 16);
            Assert.Equal(lastSeq, buf[n - 1].Seq);          // ★가장 최신 틱은 보존됨
            Assert.Equal(100 - 16, co.DroppedOldest);       // 오래된 84개는 최신-보존 드롭
            Assert.Equal(70_000 + 99, buf[n - 1].PriceRaw); // 최신 가격 정합
        }

        // ── §4 수신 불균형비 관측(적응 신호) ────────────────────────────────────────
        [Fact]
        public void AdaptiveCoalescer_ImbalanceRatio_Reflects_Skew()
        {
            var co = new CrossBrokerAdaptiveRateCoalescer(2048);
            for (int i = 0; i < 1000; i++) co.Publish(0, 5930, i, 1);   // 키움 1000
            for (int i = 0; i < 100; i++) co.Publish(1, 660, i, 1);     // 토스 100
            long ppm = co.ImbalanceRatioPpm;
            Assert.InRange(ppm, 9_900_000L, 10_100_000L);   // ≈10:1 (10_000_000 ppm)
            _out.WriteLine($"[Coalescer] imbalance={ppm}ppm produced0={co.ProducedByBroker(0)} produced1={co.ProducedByBroker(1)}");
        }

        // ── §5 HTS/MTS 단절 → 3중 텔레메트리 상호잠금 + Secret 중독(0ms 원자) ─────────
        [Fact]
        public void Disconnect_TripleTelemetryInterlock_Disarms_And_Poisons()
        {
            var recorder = new AnomalyFlightRecorder(64);
            var interlock = new MultiTelemetryDisarmInterlock(recorder);
            interlock.Configure(latencyMicros: 50_000, http429Count: 5, slippageBps: 30);   // 3소스 문턱
            using var secret = NewSecret();
            recorder.StagePending(0, 1_000_000);   // 인가 대기 주문(소거 대상)

            Assert.True(interlock.CanEmit);   // 정상 — 사출 가능

            var sw = Stopwatch.StartNew();
            interlock.Note(TelemetrySource.Latency, 60_000);   // 지연>50ms
            interlock.Note(TelemetrySource.Http429, 9);        // 429 초과
            interlock.Note(TelemetrySource.Slippage, 45);      // 슬리피지 초과
            if (interlock.IsDisarmed) secret.PurgeAndPoison(); // 단절 인터락: Secret 0x00 중독
            sw.Stop();

            Assert.True(interlock.IsDisarmed);
            Assert.False(interlock.CanEmit);                   // ★사출 100% 차단(fail-closed)
            Assert.True(interlock.BreachedSource(TelemetrySource.Latency));
            Assert.True(interlock.BreachedSource(TelemetrySource.Http429));
            Assert.True(interlock.BreachedSource(TelemetrySource.Slippage));
            Assert.True(recorder.HasCaptured && recorder.PendingBufferIsZero);   // 스냅샷 고정 + 대기주문 0x00
            Assert.True(secret.IsPoisoned && secret.AllZero());
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));    // 뒤늦은 API키 재참조 차단
            Assert.True(sw.Elapsed.TotalMilliseconds < 50, $"disarm {sw.Elapsed.TotalMilliseconds}ms");
        }

        // ── §6 단일 소스만 초과해도 전역 Disarm(fail-closed) ────────────────────────
        [Fact]
        public void Disconnect_SingleSource_GlobalDisarm()
        {
            var interlock = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder(8));
            interlock.Configure(50_000, 5, 30);
            Assert.True(interlock.Note(TelemetrySource.Slippage, 100));   // 슬리피지만 초과
            Assert.True(interlock.IsDisarmed);
            Assert.False(interlock.CanEmit);
            Assert.False(interlock.BreachedSource(TelemetrySource.Latency));   // 나머지는 미breach
        }

        // ── §7 드레인 틱 → IndicatorMath 사출(기능 정합·프레임당 Gen0 — δ=0 아님, 정직) ──
        [Fact]
        public void DrainedTicks_Feed_IndicatorMath_Bounded()
        {
            var co = new CrossBrokerAdaptiveRateCoalescer(256);
            for (int i = 0; i < 60; i++) co.Publish(0, 5930, 70_000 + (i % 7) * 100, 100);
            Span<BrokerTick> buf = stackalloc BrokerTick[64];
            int n = co.Drain(buf);

            var candles = new List<ICandleView>(n);
            for (int i = 0; i < n; i++)
            {
                decimal px = buf[i].PriceRaw;
                candles.Add(new PipeCandle(default, px, px, px, px, buf[i].QtyRaw));
            }
            var rsi = PortIndicatorMath.Rsi(candles, 14);
            var (macd, signal, hist) = PortIndicatorMath.Macd(candles);
            Assert.Equal(candles.Count, rsi.Count);
            Assert.Equal(candles.Count, macd.Count);
            foreach (var v in rsi) Assert.InRange(v, 0m, 100m);   // RSI 경계
            Assert.Equal(macd.Count, hist.Count);
        }

        // ── §8 미세캡 CAS — 백테스트/실시간 양 경로 0.1% 한도 통제 ─────────────────────
        [Fact]
        public void MicroCap_CAS_DualPath_RespectsCap()
        {
            var acc = new AtomicMicroCapAccumulator();
            long cap = 10_000_000L;   // 총자산 100억 × 0.1%
            acc.Arm(cap);

            // 백테스트 경로(순차) — 정확히 cap 까지 예약.
            long q = 1_000_000L;
            for (int i = 0; i < 10; i++) Assert.True(acc.TryReserve(q));
            Assert.Equal(cap, acc.SpentRaw);
            Assert.False(acc.TryReserve(1));   // ★1원 초과 → 봉쇄
            Assert.True(acc.Breached);
            Assert.Equal(0L, acc.RemainingRaw);
        }

        // ── §9 백테스트 스트림 격리 — Project 로 리더→레코드 직접 투사(DataRow 무물성화) ──
        [Fact]
        public void BacktestStream_RowMapper_ProjectsWithoutDataRow()
        {
            var cur = new PriceCursor(new long[] { 70_000, 70_500, 69_800, 71_200 });
            Span<Bar> dst = stackalloc Bar[4];
            int n = ZeroAllocRowStreamMapper.Project(cur, dst, static c => new Bar(c.Int64(0), c.Double(1)));
            Assert.Equal(4, n);
            Assert.Equal(70_000L, dst[0].Px);
            Assert.Equal(71_200L, dst[3].Px);
        }
        private readonly record struct Bar(long Px, double Vol);
        private sealed class PriceCursor : IRowCursor
        {
            private readonly long[] _px; private int _i = -1;
            public PriceCursor(long[] px) => _px = px;
            public bool Read() => ++_i < _px.Length;
            public long Int64(int col) => _px[_i];
            public double Double(int col) => _px[_i];
            public decimal Decimal(int col) => _px[_i];
            public string Str(int col) => "";
            public DateTime Time(int col) => default;
            public bool IsNull(int col) => false;
        }

        // ── §10 JS 위조 콜백 fail-closed(TrackedJsRef GCHandle 균형은 Phase94L 기검증) ──
        [Fact]
        public void JsInvokableToken_ForgedCallback_FailClosed()
        {
            var verifier = new JSInvokableTokenVerifier();
            long tok = verifier.Issue();
            Assert.True(verifier.Verify(tok));            // 정당 콜백
            Assert.False(verifier.Verify(tok + 1));       // 위조 토큰
            long tok2 = verifier.Issue();                 // 재발급 → 구토큰 무효화
            Assert.False(verifier.Verify(tok));           // stale
            Assert.True(verifier.Verify(tok2));
        }

        // ── §11 IPortfolioHubPort MPO 엔진 → 미세캡 게이트 결선(리밸런싱→한도) ──────────
        [Fact]
        public void MpoRebalance_Notional_GatedByMicroCap()
        {
            // MarkowitzOptimizer(포트 어댑터가 위임하는 엔진)로 리밸런싱 가중 산출.
            double[][] R = { Series(0.02, 40), Series(-0.01, 40), Series(0.005, 40) };
            var mpo = MarkowitzOptimizer.Optimize(R, new[] { "A", "B", "C" }, targetVolAnnualPct: 15, maxWeight: 0.6);
            Assert.True(mpo.IsValid);

            // 리밸런싱 명목액을 미세캡 게이트에 통과 — 한도 내만 인가.
            var acc = new AtomicMicroCapAccumulator();
            acc.Arm(5_000_000L);
            Assert.True(acc.TryReserve(3_000_000L));
            Assert.True(acc.TryReserve(2_000_000L));
            Assert.False(acc.TryReserve(1L));   // cap 도달 → 이후 리밸런싱 발주 봉쇄
        }
        private static double[] Series(double drift, int n)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = drift + ((i % 5) - 2) * 0.001;   // 결정론 파형
            return a;
        }

        // ── §12 네이티브 백테스트 캐시 — 반복 조회 히트 + 랩 미스 ──────────────────────
        [Fact]
        public void NativeCache_RepeatedLookup_Hits_And_WrapMiss()
        {
            var cache = new NativeBacktestRingCache(16);
            for (long b = 0; b < 16; b++) cache.Put(new Candle64(b, b, b, b, 70_000 + b, b));

            for (int rep = 0; rep < 100; rep++)               // ★반복 재조회 → 전량 히트(L3 안정)
                for (long b = 0; b < 16; b++) Assert.True(cache.TryGet(b, out var c) && c.CloseRaw == 70_000 + b);

            cache.Put(new Candle64(16, 0, 0, 0, 71_000, 0));  // 슬롯0 덮어씀(bar16 → idx0)
            Assert.False(cache.TryGet(0, out _));             // bar0 은 랩아웃 → 미스
            Assert.True(cache.TryGet(16, out var c16) && c16.CloseRaw == 71_000);
            Assert.True(cache.HitRatePpm > 900_000);          // 반복 조회 히트율 高
            _out.WriteLine($"[NativeCache] hits={cache.Hits} misses={cache.Misses} hitRate={cache.HitRatePpm}ppm");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §13 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class CorePipelineIntegrationConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public CorePipelineIntegrationConcurrencyTests(ITestOutputHelper o) => _out = o;

        // ── 코얼레서 수집+소비 핫패스 δ=0 ──────────────────────────────────────────
        private static readonly CrossBrokerAdaptiveRateCoalescer _hotCo = new(1024);
        private static long _hotSym;
        private static void PubDrainHot()
        {
            long s = Interlocked.Increment(ref _hotSym);
            _hotCo.Publish((int)(s & 1), 5930, 70_000 + s, 1);
            Span<BrokerTick> d = stackalloc BrokerTick[8];
            _hotCo.Drain(d);
        }

        [Fact]
        public void AdaptiveCoalescer_PublishDrain_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) PubDrainHot();   // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) PubDrainHot();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★수집(Publish)+소비(Drain) 핫패스 힙 0 → 0-byte LOH
        }

        // ── 네이티브 캐시 기입/조회 핫패스 δ=0(POH 사전할당) ───────────────────────
        private static readonly NativeBacktestRingCache _hotCache = new(1024);
        private static long _hotBar;
        private static void CacheHot()
        {
            long b = Interlocked.Increment(ref _hotBar) & 1023;
            _hotCache.Put(new Candle64(b, b, b, b, 70_000 + b, b));
            _hotCache.TryGet(b, out _);
        }

        [Fact]
        public void NativeCache_PutGet_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) CacheHot();
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) CacheHot();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★POH 고정 → 조회/기입 GC 0 Byte
        }

        // ── 락프리 2브로커 동시 수신 — 회계 정합·틱 무결·생산자 무차단 ────────────────
        [Fact]
        public void AdaptiveCoalescer_Concurrent_TwoBrokers_Consistent()
        {
            var co = new CrossBrokerAdaptiveRateCoalescer(256);
            const int perBroker = 50_000;
            long drained = 0, badTick = 0, producersDone = 0;
            Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(3);

            void Producer(int broker)
            {
                try { start.Wait(); for (int i = 0; i < perBroker; i++) co.Publish(broker, 100 + broker, 1_000 + i, 1); }
                catch (Exception e) { err = e; }
                finally { Interlocked.Increment(ref producersDone); done.Signal(); }
            }
            var consumer = new Thread(() =>
            {
                try
                {
                    start.Wait();
                    var buf = new BrokerTick[512];
                    int emptyAfterDone = 0;
                    while (true)
                    {
                        int n = co.Drain(buf);
                        for (int i = 0; i < n; i++)
                        {
                            var t = buf[i];
                            if (t.BrokerId != 0 && t.BrokerId != 1) Interlocked.Increment(ref badTick);
                            Interlocked.Increment(ref drained);
                        }
                        // 생산자 전원 종료 후 연속 빈 드레인 → 링 완전 소진(더는 쓰기 없음 → torn 없음).
                        if (Volatile.Read(ref producersDone) == 2)
                        {
                            if (n == 0) { if (++emptyAfterDone > 3) break; }
                            else emptyAfterDone = 0;
                        }
                    }
                }
                catch (Exception e) { err = e; }
                finally { done.Signal(); }
            }) { IsBackground = true };

            var p0 = new Thread(() => Producer(0)) { IsBackground = true };
            var p1 = new Thread(() => Producer(1)) { IsBackground = true };
            consumer.Start(); p0.Start(); p1.Start();
            start.Set();
            Assert.True(done.Wait(30_000));
            done.Dispose(); start.Dispose();
            Assert.Null(err);

            long produced = co.Produced;
            long got = Interlocked.Read(ref drained);
            Assert.Equal(perBroker * 2L, produced);          // ★생산자 무차단 — 전량 생산
            Assert.Equal(0L, Interlocked.Read(ref badTick));  // 모든 드레인 틱 brokerId∈{0,1}(무결)
            Assert.Equal(produced, got + co.DroppedOldest);   // ★정확 회계 — 모든 틱은 드레인 또는 최신-보존 드롭(누락 0)
            Assert.True(got > 0);
            _out.WriteLine($"[Coalescer/2t] produced={produced} drained={got} droppedOldest={co.DroppedOldest}");
        }

        // ── 미세캡 실시간 경로 — 12스레드 동시 예약, cap 1비트도 초과 불가 ──────────────
        [Fact]
        public void MicroCap_Concurrent_RealtimePath_NeverExceedsCap()
        {
            var acc = new AtomicMicroCapAccumulator();
            long cap = 10_000_000L;
            acc.Arm(cap);
            const int threads = 12; const long q = 50_000L;   // cap/q = 200
            long authorized = 0; Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(threads);
            for (int t = 0; t < threads; t++)
            {
                new Thread(() =>
                {
                    try { start.Wait(); for (int i = 0; i < 100; i++) if (acc.TryReserve(q)) Interlocked.Increment(ref authorized); }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true }.Start();
            }
            start.Set();
            Assert.True(done.Wait(30_000));
            done.Dispose(); start.Dispose();
            Assert.Null(err);

            Assert.True(acc.SpentRaw <= cap, $"spent {acc.SpentRaw} > cap {cap}");   // ★1비트도 초과 불가
            Assert.Equal(Interlocked.Read(ref authorized) * q, acc.SpentRaw);        // 인가×단위 = 실지출(정합)
            Assert.True(acc.Breached);   // 총 시도(1200×50k=6천만)>cap → 초과 시점 봉쇄
        }
    }
}

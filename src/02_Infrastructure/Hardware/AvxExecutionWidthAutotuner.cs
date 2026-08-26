// ============================================================================
// AvxExecutionWidthAutotuner — v8.0.0 ★Think Beyond Gemini: 실행폭 경험적 자동조율기
//   "AVX 웜업 지연(A) + 다운클럭 거버너(B) — 하나의 지연-피드백 루프로 통합"
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  제미나이는 두 미세아키텍처 리스크를 서로 다른 두 모듈로 방어하자 제안했다:
//    · A — ZMM 웜업 keep-alive(휴면 후 첫 512-bit 명령 100~300ns 콜드 페널티 방지 위해 더미 ZMM 상시 주사).
//    · B — 다운클럭 거버너(AVX-512 실행으로 코어 주파수 강하 감지 시 AVX2 듀얼레인 스플릿).
//  A 는 '웜업이 항상 이득'이라 가정하고, B 는 '다운클럭만이 문제'라 가정한다. 그러나 실리콘의 진짜 최적 폭은
//  '가정'이 아니라 '현재 전력/열/웜업 상태에서 실측한 지연'이 결정한다.
//
//  이 오토튜너는 A·B 를 '하나의 경험적 지연-피드백 불변식'으로 통합한다: 콜드 슬롯에서 각 폭(ZMM/YMM/스칼라)의
//  고정 워크로드 지연을 QPC 로 실측(min-of-bursts)해 '가장 빠른 폭'을 SelectedWidth 로 좁힌다.
//    (1) ZMM 웜업 지연↑ → ZMM 실측 지연이 커짐 → 자동으로 YMM 선택(A 초월: keep-alive 를 '측정이 곧 웜업'으로 흡수 —
//        계측의 ZMM 워크로드 자체가 실행 유닛을 상시 hot-ready 로 유지).
//    (2) 다운클럭으로 ZMM 유효 처리량 < YMM → ZMM 실측 지연 > YMM → 자동 YMM 전환(B 초월: 전력카운터 비의존·이식적).
//    ∴ 두 파멸 리스크를 '동일 지연 신호'로 동시 포착 — 가정이 아닌 실측으로 나노초 최적 폭에 수렴한다.
//
//  ★불변: δ=0(핫패스 SelectedWidth/PreferMaxWidth 읽기 힙 0·Volatile) · 결정론(주입 시계·순수 선택 로직) ·
//    liveArmed:false(순수 계측/선택 — IBroker·소켓·onSend 참조 0. 폭 선택은 알파 '속도'만 조율·발주 무관) ·
//    fail-closed(계측 실패/GC 개입 → 폐기·기존 선택 유지). ★계측 워크로드는 이식형 Vector512/256(에뮬레이트 비용 포함 실측).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>AVX 실행폭 경험적 자동조율기(웜업+다운클럭 통합 방어·δ=0). 콜드 스레드가 각 폭 지연 실측 → 최속 폭 선택.</summary>
    public sealed class AvxExecutionWidthAutotuner : IDisposable
    {
        private readonly int  _iters;         // 폭당 워크로드 반복(보정)
        private readonly int  _bursts;        // 샘플당 버스트(최소 채택)
        private readonly int  _intervalMs;
        private readonly Func<long> _nowTicks;

        private int  _selectedWidth = (int)AlphaAccelTier.Avx512;   // 초기 = 최고(계측 전 낙관)
        private long _zmmTicks, _ymmTicks, _scalarTicks;
        private long _calibrations, _widthDowngrades, _warmKeepAlives;
        private long _sink;                    // DCE 방지

        private Thread? _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private int _started, _disposed;

        public AvxExecutionWidthAutotuner(int iters = 4096, int bursts = 3, int intervalMs = 100, Func<long>? nowTicks = null)
        {
            _iters = Math.Max(256, iters);
            _bursts = Math.Max(1, bursts);
            _intervalMs = Math.Clamp(intervalMs, 20, 2000);
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            // 부팅 시 가용 최고 폭으로 초기화(계측 전).
            Volatile.Write(ref _selectedWidth, (int)Avx512MicroAlphaEngine.BestAvailableTier);
        }

        /// <summary>★현재 최속 실행폭(δ=0·핫패스). 알파 엔진이 읽어 preferMaxWidth 를 결정.</summary>
        public AlphaAccelTier SelectedWidth => (AlphaAccelTier)Volatile.Read(ref _selectedWidth);
        /// <summary>ZMM 이 최속으로 판정됐는가(δ=0). false 면 웜업/다운클럭 회피 위해 YMM 강등 지시.</summary>
        public bool PreferMaxWidth  => Volatile.Read(ref _selectedWidth) == (int)AlphaAccelTier.Avx512;
        public long ZmmLatencyTicks => Interlocked.Read(ref _zmmTicks);
        public long YmmLatencyTicks => Interlocked.Read(ref _ymmTicks);
        public long ScalarLatencyTicks => Interlocked.Read(ref _scalarTicks);
        public long Calibrations    => Interlocked.Read(ref _calibrations);
        /// <summary>ZMM 이 가용하나 웜업/다운클럭으로 YMM 이하로 강등된 횟수(A·B 리스크 실측 발생 계수).</summary>
        public long WidthDowngrades => Interlocked.Read(ref _widthDowngrades);
        public long WarmKeepAlives  => Interlocked.Read(ref _warmKeepAlives);
        /// <summary>선택 폭의 워크로드 iter당 실측 지연(ns·텔레메트리). 미계측/무한 시 0. 주입 시계 주파수는 Stopwatch 기준.</summary>
        public long SelectedOpLatencyNanos
        {
            get
            {
                long t = SelectedWidth switch { AlphaAccelTier.Avx512 => ZmmLatencyTicks, AlphaAccelTier.Avx2 => YmmLatencyTicks, _ => ScalarLatencyTicks };
                if (t <= 0 || t == long.MaxValue || _iters <= 0) return 0L;
                return (long)(t * 1_000_000_000.0 / Stopwatch.Frequency / _iters);
            }
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "AvxWidthAutotuner", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            try { while (!_stop.Wait(_intervalMs)) Calibrate(); }
            catch (ObjectDisposedException) { /* 종료 경합 — 안전 탈출 */ }
        }

        /// <summary>1회 실측 조율(콜드·테스트 직접 호출). 각 가용 폭 지연 실측 → 최속 선택. GC 개입 시 폐기(오탐 방지).</summary>
        public void Calibrate()
        {
            int gc0 = GC.CollectionCount(0);
            long zmm = Vector512.IsHardwareAccelerated || !Avx512MicroAlphaEngine.BestAvailableTier.Equals(AlphaAccelTier.Scalar)
                       ? MeasureWidth(AlphaAccelTier.Avx512) : long.MaxValue;
            long ymm = Vector256.IsHardwareAccelerated ? MeasureWidth(AlphaAccelTier.Avx2) : long.MaxValue;
            long sca = MeasureWidth(AlphaAccelTier.Scalar);
            if (GC.CollectionCount(0) != gc0) return;   // GC 개입 → 폐기(기존 선택 유지)

            Interlocked.Exchange(ref _zmmTicks, zmm);
            Interlocked.Exchange(ref _ymmTicks, ymm);
            Interlocked.Exchange(ref _scalarTicks, sca);
            Interlocked.Increment(ref _warmKeepAlives);   // ZMM 워크로드 실행 = keep-alive(hot-ready 유지)

            bool zmmAvail = Vector512.IsHardwareAccelerated;   // 하드웨어 ZMM 존재(에뮬레이트는 미가속)
            bool ymmAvail = Vector256.IsHardwareAccelerated;
            var sel = SelectFrom(zmm, ymm, sca, zmmAvail, ymmAvail);
            var prev = (AlphaAccelTier)Volatile.Read(ref _selectedWidth);
            Volatile.Write(ref _selectedWidth, (int)sel);
            // ZMM 가용한데 ZMM 미선택 = 웜업/다운클럭 실측 발생(A·B 리스크) → 강등 계수.
            if (zmmAvail && sel != AlphaAccelTier.Avx512 && prev == AlphaAccelTier.Avx512) Interlocked.Increment(ref _widthDowngrades);
            Interlocked.Increment(ref _calibrations);
        }

        /// <summary>★순수 선택 로직(결정론·테스트 가능): 가용 폭 중 실측 지연 최소를 선택. 동률 시 넓은 폭 우선(처리량).</summary>
        public static AlphaAccelTier SelectFrom(long zmmTicks, long ymmTicks, long scalarTicks, bool zmmAvail, bool ymmAvail)
        {
            AlphaAccelTier best = AlphaAccelTier.Scalar; long bestT = scalarTicks;
            if (ymmAvail && ymmTicks <= bestT) { best = AlphaAccelTier.Avx2; bestT = ymmTicks; }
            if (zmmAvail && zmmTicks <= bestT) { best = AlphaAccelTier.Avx512; bestT = zmmTicks; }
            return best;
        }

        // 폭별 고정 워크로드 지연 실측(min-of-bursts·δ=0). 이식형 Vector512/256(에뮬레이트/다운클럭 비용 포함 실측).
        private long MeasureWidth(AlphaAccelTier w)
        {
            long best = long.MaxValue;
            for (int b = 0; b < _bursts; b++)
            {
                long t0 = _nowTicks();
                long r = w switch
                {
                    AlphaAccelTier.Avx512 => WorkloadZmm(_iters),
                    AlphaAccelTier.Avx2   => WorkloadYmm(_iters),
                    _                     => WorkloadScalar(_iters),
                };
                long dt = _nowTicks() - t0;
                _sink = r;                          // DCE 방지
                if (dt > 0 && dt < best) best = dt;
            }
            return best == long.MaxValue ? long.MaxValue : best;
        }

        // 데이터 의존 SIMD 누산 체인(스킵 불가) — 폭별 실측용. δ=0(스택 Vector).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long WorkloadZmm(int iters)
        {
            var acc = Vector512<float>.One; var m = Vector512.Create(1.0000001f);
            for (int i = 0; i < iters; i++) acc = acc * m + m;
            return (long)Vector512.Sum(acc);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long WorkloadYmm(int iters)
        {
            var acc = Vector256<float>.One; var m = Vector256.Create(1.0000001f);
            for (int i = 0; i < iters; i++) acc = acc * m + m;
            return (long)Vector256.Sum(acc);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long WorkloadScalar(int iters)
        {
            float acc = 1f; const float m = 1.0000001f;
            for (int i = 0; i < iters; i++) acc = acc * m + m;
            return (long)acc;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Set();
            bool exited; try { exited = _thread == null || _thread.Join(2000); } catch { exited = false; }
            if (exited) { try { _stop.Dispose(); } catch { } }
        }
    }
}

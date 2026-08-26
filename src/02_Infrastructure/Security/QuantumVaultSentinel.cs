// ============================================================================
// QuantumVaultSentinel — v8.1.0 안티-RAM 퀀텀 금고(자가 소멸 센티널)
//
//  외부 프로세스 메모리 스크래핑·디버거 댓치를 감지하면, 등록된 시크릿(브로커 토큰·전략 파라미터·키)을 즉시
//  검증 제로화(VerifiedSecureZeroizer)하고 onBreach(프로덕션: OrderAttestationService.ForceFreeze → 볼트 Seal →
//  Environment.FailFast 하드 소멸)를 원자 1회 격발한다. 탐지 술어·onBreach 는 주입식 — 테스트는 페이크 탐지 +
//  플래그 onBreach 로 프로세스를 죽이지 않고 전 경로를 단언한다(오탐 0·테스트 안전).
//
//  ── 정직한 한계 & 방어 조합 ────────────────────────────────────────────────
//  · 관리형 런타임은 외부의 '수동 읽기'를 감지할 수 없다 — 본 센티널은 (1)디버거 댓치(IsDebuggerPresent/
//    CheckRemoteDebuggerPresent + Debugger.IsAttached), (2)HoneyKeyDecoyMesh 카나리 변조, (3)AntiHooking 진입점
//    변조를 '능동 공격' 신호로 삼는다. 라이브 토큰 캐시가 immutable string 이면 in-place 와이프 불가 → onBreach 의
//    Seal(볼트 봉인) + FailFast(프로세스 하드 소멸)로 덤프 전 소멸이 실질 방어다(char[]/byte[] 등록분은 즉시 와이프).
//  · WEBONLY 빌드: kernel32 네이티브 anti-debug 제외 → Debugger.IsAttached(관리형) 만. 오탐 없이 안심 강등.
//
//  ★불변: δ=0(IsArmed/IsBreached/CheckOnce[false 탐지] 힙 0·Volatile — 브리치 경로만 할당) · 결정론(주입 술어·시계) ·
//    liveArmed:false(순수 방어 — IBroker·소켓·onSend 참조 0. onBreach 는 tighten-only[Freeze/FailFast]·발주 유발 0) ·
//    fail-closed(탐지 → 즉시 제로화+봉인+소멸). ★탐지 술어 예외는 fail-safe(오탐 자멸 방지 — 트레이딩 가용성 우선).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>안티-RAM 퀀텀 금고 센티널(자가 소멸·주입식 탐지/onBreach·δ=0 상태 읽기). Register(시크릿)→Start→브리치 시 검증 제로화.</summary>
    public sealed class QuantumVaultSentinel : IDisposable
    {
        private readonly Action<string>         _onBreach;
        private readonly VerifiedSecureZeroizer _zeroizer;
        private readonly Func<bool>             _debuggerDetector;
        private readonly Func<bool>?            _extraDetector;   // 허니키 변조·anti-hook 등 결합 탐지
        private readonly int                    _intervalMs;

        private readonly List<byte[]> _byteSecrets = new();
        private readonly List<char[]> _charSecrets = new();
        private readonly object _regLock = new();
        private int _armed, _breached, _zeroizedCount;

        private Thread? _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private int _started, _disposed;

        /// <param name="onBreach">비인가 접근 탐지 시 1회 격발. 프로덕션: att.ForceFreeze+vault.Seal+Environment.FailFast. 테스트: 플래그.</param>
        /// <param name="debuggerDetector">디버거 탐지 술어(기본 DefaultDebuggerDetector). 테스트는 페이크 주입.</param>
        /// <param name="extraDetector">추가 탐지(허니키 변조·anti-hook 결합·옵션).</param>
        public QuantumVaultSentinel(Action<string> onBreach, VerifiedSecureZeroizer? zeroizer = null,
                                    Func<bool>? debuggerDetector = null, Func<bool>? extraDetector = null, int intervalMs = 50)
        {
            _onBreach = onBreach ?? throw new ArgumentNullException(nameof(onBreach));
            _zeroizer = zeroizer ?? new VerifiedSecureZeroizer();
            _debuggerDetector = debuggerDetector ?? DefaultDebuggerDetector;
            _extraDetector = extraDetector;
            _intervalMs = Math.Clamp(intervalMs, 10, 5000);
        }

        public bool IsArmed        => Volatile.Read(ref _armed) == 1;
        public bool IsBreached     => Volatile.Read(ref _breached) == 1;
        public long Detections     => Interlocked.Read(ref _detectionsLong);
        public int  ZeroizedBuffers => Volatile.Read(ref _zeroizedCount);
        private long _detectionsLong;

        /// <summary>브리치 시 와이프할 시크릿 버퍼 등록(byte[]). SecretLease/명시 민감 버퍼.</summary>
        public void Register(byte[] secret) { if (secret is { Length: > 0 }) lock (_regLock) _byteSecrets.Add(secret); }
        /// <summary>브리치 시 와이프할 시크릿 버퍼 등록(char[]·string 유래 시크릿).</summary>
        public void RegisterChars(char[] secret) { if (secret is { Length: > 0 }) lock (_regLock) _charSecrets.Add(secret); }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            Volatile.Write(ref _armed, 1);
            _thread = new Thread(Loop) { IsBackground = true, Name = "QuantumVault", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            try { while (!_stop.Wait(_intervalMs)) CheckOnce(); }
            catch (ObjectDisposedException) { /* 종료 경합 — 안전 탈출 */ }
        }

        /// <summary>1회 비인가 접근 판정(δ=0·탐지 술어 false 시 무할당). 탐지 시 원자 브리치(제로화+onBreach). 테스트 직접 호출.</summary>
        public void CheckOnce()
        {
            if (Volatile.Read(ref _breached) == 1) return;
            bool bad;
            try { bad = _debuggerDetector() || (_extraDetector?.Invoke() ?? false); }
            catch { bad = false; }   // ★탐지 자체 예외 → fail-safe(오탐 자멸 방지·트레이딩 가용성 우선)
            if (bad) Breach("퀀텀 금고 — 비인가 메모리 접근/디버거 댓치 탐지");
        }

        private void Breach(string reason)
        {
            if (Interlocked.CompareExchange(ref _breached, 1, 0) != 0) return;   // 1회만
            Interlocked.Increment(ref _detectionsLong);
            // ★등록 시크릿 원자 검증 제로화 — 램 소멸(0.1ms 급).
            lock (_regLock)
            {
                foreach (var b in _byteSecrets) { if (_zeroizer.Zeroize(b)) Interlocked.Increment(ref _zeroizedCount); }
                foreach (var c in _charSecrets) { if (_zeroizer.ZeroizeChars(c)) Interlocked.Increment(ref _zeroizedCount); }
            }
            try { _onBreach(reason); } catch { /* onBreach 실패해도 브리치 래치 유지 */ }
        }

        /// <summary>기본 디버거 탐지: Debugger.IsAttached(관리형·항상) ∨ 네이티브 IsDebuggerPresent/CheckRemoteDebuggerPresent(Windows·비-WEBONLY).</summary>
        public static bool DefaultDebuggerDetector()
        {
            if (System.Diagnostics.Debugger.IsAttached) return true;
#if !WEBONLY
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    if (QuantumVaultNative.IsDebuggerPresent()) return true;
                    int remote = 0;
                    QuantumVaultNative.CheckRemoteDebuggerPresent(QuantumVaultNative.GetCurrentProcess(), ref remote);
                    if (remote != 0) return true;
                }
                catch { }   // 네이티브 실패 → 관리형 결과만
            }
#endif
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Volatile.Write(ref _armed, 0);
            _stop.Set();
            bool exited; try { exited = _thread == null || _thread.Join(2000); } catch { exited = false; }
            if (exited) { try { _stop.Dispose(); } catch { } }
        }
    }

#if !WEBONLY
    // Windows 네이티브 anti-debug(kernel32·LibraryImport 소스생성 — SYSLIB1054 회피·TreatWarningsAsErrors 준수).
    //   ★WEBONLY 빌드에선 이 블록 전체가 컴파일 제외(플랫폼 네이티브 디커플링) — 센티널은 Debugger.IsAttached 만.
    internal static partial class QuantumVaultNative
    {
        [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsDebuggerPresent();

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref int pbDebuggerPresent);   // PBOOL = 4바이트 int

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetCurrentProcess();
    }
#endif
}

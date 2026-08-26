// ============================================================================
// AntiHookingCodeIntegrityGuard — v8.1.0 Native API 후킹 코드 무결성 가드
//
//  악성 인젝터가 kernel32.dll/ntdll.dll 의 anti-debug 관련 export 진입점을 인라인 후킹(0xE9 near JMP·0xEB short
//  JMP 로 첫 명령 덮어쓰기)해 탐지를 무력화하는 것을 방어한다. 감시 대상 export 의 첫 바이트를 판독해 JMP 서명이면
//  변조로 판정, 무장 상태를 Fail-Closed 로 원자 하향(SafeHoldActive → blockWhile tighten-only)한다.
//
//  ── 설계 ──────────────────────────────────────────────────────────────────
//  · 정상 x64 export 진입점은 mov edi,edi(0x8BFF hotpatch)·REX(0x48/0x4C)·push(0x55) 등으로 시작한다. 첫 바이트가
//    0xE9/0xEB(JMP)면 인라인 후킹 서명 — 보수적 판정(오탐 최소). 진입점 판독기는 주입식 — 테스트는 페이크(clean/hooked)로 단언.
//  · WEBONLY 빌드/비-Windows: 네이티브 판독 제외 → HookStateCode=2(미지원)·무탐지(안심 강등·오탐 0).
//
//  ★불변: δ=0(IsTampered/SafeHoldActive/HookStateCode 힙 0·Volatile — CheckIntegrity 는 콜드) · 결정론(주입 판독기) ·
//    liveArmed:false(순수 무결성 관측 — IBroker·소켓·onSend 참조 0. SafeHoldActive 는 tighten-only) · fail-closed(후킹 → 무장 하향).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>Native API 후킹 무결성 가드(δ=0 상태). CheckIntegrity(진입점 JMP 서명) → 변조 시 SafeHoldActive(무장 하향).</summary>
    public sealed class AntiHookingCodeIntegrityGuard
    {
        // 공격자가 흔히 후킹하는 anti-debug 관련 export.
        private static readonly (string mod, string fn)[] Monitored =
        {
            ("kernel32.dll", "IsDebuggerPresent"),
            ("kernel32.dll", "CheckRemoteDebuggerPresent"),
            ("ntdll.dll",    "DbgUiRemoteBreakin"),
            ("ntdll.dll",    "NtSetInformationThread"),
        };

        private readonly Func<string, string, byte?> _reader;   // (module,function)→진입점 첫 바이트(null=미가용)
        private int  _tampered, _supported;
        private long _checks, _tamperHits;

        /// <param name="entryByteReader">진입점 첫 바이트 판독기(기본 네이티브 GetProcAddress). 테스트는 페이크 주입.</param>
        public AntiHookingCodeIntegrityGuard(Func<string, string, byte?>? entryByteReader = null)
        {
            _reader = entryByteReader ?? DefaultReader;
            Volatile.Write(ref _supported, (entryByteReader != null || SupportedNatively()) ? 1 : 0);
        }

        public bool IsTampered      => Volatile.Read(ref _tampered) == 1;
        /// <summary>★후킹 탐지 시 무장 술어 blockWhile 이 읽어 tighten-only 억제(무장 하향).</summary>
        public bool SafeHoldActive  => Volatile.Read(ref _tampered) == 1;
        /// <summary>0 clean / 1 tampered / 2 unsupported(WebOnly·비-Windows).</summary>
        public long HookStateCode   => Volatile.Read(ref _supported) == 0 ? 2L : (IsTampered ? 1L : 0L);
        public long Checks          => Interlocked.Read(ref _checks);
        public long TamperHits      => Interlocked.Read(ref _tamperHits);

        /// <summary>감시 export 진입점 무결성 대조. JMP 서명(0xE9/0xEB) 포착 시 변조 트립 + false. 미지원 시 true(안심). 콜드/테스트 호출.</summary>
        public bool CheckIntegrity()
        {
            Interlocked.Increment(ref _checks);
            if (Volatile.Read(ref _supported) == 0) return true;   // 미지원 → 무탐지(안심 강등)
            foreach (var (mod, fn) in Monitored)
            {
                byte? first = _reader(mod, fn);
                if (first is byte b && (b == 0xE9 || b == 0xEB))   // near/short JMP = 인라인 후킹 서명
                {
                    Interlocked.Increment(ref _tamperHits);
                    Volatile.Write(ref _tampered, 1);
                    return false;
                }
            }
            return Volatile.Read(ref _tampered) == 0;
        }

        private static bool SupportedNatively()
        {
#if !WEBONLY
            return OperatingSystem.IsWindows();
#else
            return false;
#endif
        }

        private static byte? DefaultReader(string module, string function)
        {
#if !WEBONLY
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                IntPtr h = AntiHookNative.GetModuleHandleW(module);
                if (h == IntPtr.Zero) return null;
                IntPtr p = AntiHookNative.GetProcAddress(h, function);
                if (p == IntPtr.Zero) return null;
                return Marshal.ReadByte(p);   // 진입점 첫 바이트(자기 프로세스 코드 판독)
            }
            catch { return null; }
#else
            return null;
#endif
        }
    }

#if !WEBONLY
    // Windows 네이티브 모듈/심볼 해석(kernel32·LibraryImport 소스생성 — SYSLIB1054 회피). ★WEBONLY 시 컴파일 제외.
    internal static partial class AntiHookNative
    {
        [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr GetModuleHandleW(string lpModuleName);

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    }
#endif
}

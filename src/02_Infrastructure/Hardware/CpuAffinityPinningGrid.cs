// ============================================================================
// CpuAffinityPinningGrid — v7.3.0 CPU P-Core 물리 격리 & 친화도 핀 바인딩
//
//  50ms 발주 드레인 스레드·실시간 시세 수신 스레드를 Windows OS 스케줄러의 무단 코어 이동(E-Core 강등)으로부터
//  격리한다. GetLogicalProcessorInformationEx(RelationProcessorCore) 로 물리 코어의 EfficiencyClass 를 읽어
//  '고성능(P) 코어'의 논리프로세서 affinity 마스크를 산출하고, SetThreadAffinityMask 로 대상 스레드를 P-Core 에
//  물리 핀 고정한다. 프로세스/스레드 우선순위를 승격(SetPriorityClass·SetThreadPriority)해 컨텍스트 스위칭 지터를
//  최소화한다.
//
//  ── ★안전 자세(물리 제약 준수) ────────────────────────────────────────────
//  · REALTIME_PRIORITY_CLASS 는 OS 전체를 기아시킬 수 있어 '옵트인 전용'이며 기본은 HIGH_PRIORITY_CLASS.
//    권한 부족 시 SetPriorityClass(REALTIME) 은 자연히 실패(false) — 예외 없이 안전 강등.
//  · 모든 Win32 호출은 반환값을 검사하고 실패해도 예외를 밖으로 던지지 않는다(fail-safe — 핀 실패가 앱을
//    흔들지 않는다). 비-Windows/구형 API 실패 시 '전체 코어 마스크'로 안심 강등(핀 이득 0·무해).
//  · liveArmed:false: 스케줄링 제어일 뿐 — IBroker/소켓/onSend/PublishRaw 참조 0. 코어 핀이 발주를 유발하는
//    경로는 존재하지 않는다. δ=0: Pin/우선순위 승격은 콜드 1회. 핫패스 폴(CurrentCore/AttestPinned)은 무할당.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>우선순위 승격 등급(안전 기본 High — Realtime 은 옵트인).</summary>
    public enum CpuPriorityTier { Normal = 0, High = 1, Realtime = 2 }

    /// <summary>
    /// P-Core 친화도 핀 바인딩 그리드. DetectPCoreMask(콜드 1회) → PinCurrentThread/ElevateProcess(콜드) →
    /// CurrentCore/AttestPinnedCore(δ=0 폴). 전 Win32 호출 fail-safe·비-Windows 안심 강등.
    /// </summary>
    public sealed partial class CpuAffinityPinningGrid
    {
        private const uint HIGH_PRIORITY_CLASS     = 0x00000080;
        private const uint REALTIME_PRIORITY_CLASS = 0x00000100;
        private const uint NORMAL_PRIORITY_CLASS   = 0x00000020;
        private const int  THREAD_PRIORITY_TIME_CRITICAL = 15;
        private const int  THREAD_PRIORITY_HIGHEST        = 2;
        private const int  RelationProcessorCore = 0;

        private readonly nuint _pCoreMask;      // P-Core 논리프로세서 affinity 마스크(0=감지 실패→전체)
        private readonly int   _pCoreCount;
        private readonly bool  _supported;      // Windows + 마스크 산출 성공
        private long _pinnedThreads, _migrationEvents, _pinFailures;

        public CpuAffinityPinningGrid()
        {
            if (OperatingSystem.IsWindows())
            {
                (_pCoreMask, _pCoreCount) = DetectPCoreMask();
                _supported = _pCoreMask != 0;
            }
        }

        public bool  Supported        => _supported;
        public nuint PCoreMask        => _pCoreMask;
        public int   PCoreCount       => _pCoreCount;
        public long  PinnedThreads    => Interlocked.Read(ref _pinnedThreads);
        public long  MigrationEvents  => Interlocked.Read(ref _migrationEvents);
        public long  PinFailures      => Interlocked.Read(ref _pinFailures);

        /// <summary>현재 스레드를 P-Core 마스크에 물리 핀 고정(콜드). 실패해도 false 반환·예외 없음(fail-safe).</summary>
        public bool PinCurrentThread(int threadPriority = THREAD_PRIORITY_TIME_CRITICAL)
        {
            if (!_supported) return false;
            try
            {
                IntPtr h = GetCurrentThread();
                nuint prev = SetThreadAffinityMask(h, _pCoreMask);   // 0 = 실패
                if (prev == 0) { Interlocked.Increment(ref _pinFailures); return false; }
                SetThreadPriority(h, threadPriority);                 // 실패는 무해(우선순위만 미승격)
                Interlocked.Increment(ref _pinnedThreads);
                return true;
            }
            catch { Interlocked.Increment(ref _pinFailures); return false; }   // ★fail-safe
        }

        /// <summary>프로세스 우선순위 등급 승격(콜드). Realtime 은 옵트인 — 권한 부족 시 자연 실패(false)·안전.</summary>
        public bool ElevateProcessPriority(CpuPriorityTier tier)
        {
            if (!OperatingSystem.IsWindows() || tier == CpuPriorityTier.Normal) return false;
            try
            {
                uint cls = tier == CpuPriorityTier.Realtime ? REALTIME_PRIORITY_CLASS : HIGH_PRIORITY_CLASS;
                return SetPriorityClass(GetCurrentProcess(), cls);   // false = 실패(권한 등) — 예외 없음
            }
            catch { return false; }
        }

        /// <summary>현재 논리프로세서 번호(δ=0·무할당). 비-Windows 는 -1.</summary>
        public int CurrentCore() => OperatingSystem.IsWindows() ? (int)GetCurrentProcessorNumber() : -1;

        /// <summary>현재 스레드가 P-Core 마스크 위에 있는지 δ=0 확인. 이탈(마이그레이션) 시 계수 + false.</summary>
        public bool AttestPinnedCore()
        {
            if (!_supported) return false;
            int core = CurrentCore();
            if (core < 0 || core >= 64) return false;
            bool onP = (_pCoreMask & ((nuint)1 << core)) != 0;
            if (!onP) Interlocked.Increment(ref _migrationEvents);   // OS 가 P-Core 밖으로 이주시킴(핀 무효화 징후)
            return onP;
        }

        // ── P-Core 감지: GetLogicalProcessorInformationEx(RelationProcessorCore) 로 최고 EfficiencyClass 코어의
        //    논리프로세서 마스크 산출. 실패/구형 API → (0,0)(호출자 안심 강등). unsafe 가변길이 레코드 워킹. ──
        private static unsafe (nuint mask, int count) DetectPCoreMask()
        {
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);   // 필요 크기 질의
                if (len == 0) return (0, 0);
                byte* buf = stackalloc byte[(int)Math.Min(len, 64 * 1024)];
                if (len > 64 * 1024) return (0, 0);   // 비정상 대형 — 안심 강등
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, (IntPtr)buf, ref len)) return (0, 0);

                // 1차 패스: 최고 EfficiencyClass 탐색(P-Core = 값이 큼).
                byte maxEff = 0;
                for (uint off = 0; off + 8 <= len;)
                {
                    var rec = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)(buf + off);
                    uint size = rec->Size;
                    if (size == 0 || off + size > len) break;
                    if (rec->Relationship == RelationProcessorCore && rec->Processor.EfficiencyClass > maxEff)
                        maxEff = rec->Processor.EfficiencyClass;
                    off += size;
                }
                // 2차 패스: 최고 EfficiencyClass 코어들의 GroupMask affinity 합산.
                nuint mask = 0; int count = 0;
                for (uint off = 0; off + 8 <= len;)
                {
                    var rec = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)(buf + off);
                    uint size = rec->Size;
                    if (size == 0 || off + size > len) break;
                    if (rec->Relationship == RelationProcessorCore && rec->Processor.EfficiencyClass == maxEff)
                    {
                        // GroupCount≥1 — 그룹 0 의 마스크만 사용(64논리 이하 단일 그룹 가정·초과분 무시).
                        nuint m = rec->Processor.GroupMask0.Mask;
                        mask |= m;
                        count += System.Numerics.BitOperations.PopCount((ulong)m);
                    }
                    off += size;
                }
                return (mask, count);
            }
            catch { return (0, 0); }   // ★fail-safe: 감지 실패 → 안심 강등(핀 미적용)
        }

        // ── Native (LibraryImport 소스생성 — SYSLIB1054 회피, TreatWarningsAsErrors 준수) ──
        [StructLayout(LayoutKind.Sequential)]
        private struct GROUP_AFFINITY { public nuint Mask; public ushort Group; public ushort Reserved0, Reserved1, Reserved2; }

        // PROCESSOR_RELATIONSHIP 앞부분(Flags·EfficiencyClass·Reserved20·GroupCount·첫 GroupMask). 가변 GroupMask[] 는 첫 개만.
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags; public byte EfficiencyClass;
            public byte R0, R1, R2, R3, R4, R5, R6, R7, R8, R9, R10, R11, R12, R13, R14, R15, R16, R17, R18, R19;
            public ushort GroupCount;
            public GROUP_AFFINITY GroupMask0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public int Relationship;   // LOGICAL_PROCESSOR_RELATIONSHIP
            public uint Size;
            public PROCESSOR_RELATIONSHIP Processor;   // union — RelationProcessorCore 일 때 유효
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentThread();

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentProcess();

        [LibraryImport("kernel32.dll")]
        private static partial nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetThreadPriority(IntPtr hThread, int nPriority);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentProcessorNumber();
    }
}

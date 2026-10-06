// ============================================================================
// PipeServerIdentity — [사전 검증] 네임드 파이프 서버 프로세스 신원 판독(콜드 · Windows)
//
//  KiwoomBridgeClient 는 잘 알려진 이름(MS2E_KIWOOM_BRIDGE_V1)의 파이프에 '클라이언트'로 붙는다. 같은 사용자 세션의 다른 프로세스가
//  프록시보다 먼저 그 이름으로 서버를 열면(파이프 선점 · squatting) 호스트는 가짜 프록시와 통신한다 — 위조 체결·시세·잔고 주입,
//  주문 프레임 탈취. 연결 직후 GetNamedPipeServerProcessId 로 서버 PID 를 얻고, QueryFullProcessImageNameW 로 그 실행 파일 경로를
//  기대 경로(설치 폴더의 MS2E.KiwoomProxyHost.exe)와 대조한다. 판독 실패 = 불일치(fail-closed).
//  ★한계(정직): 경로 대조는 '같은 파일 경로'를 증명할 뿐 파일 내용을 증명하지 않는다 — 내용 무결성은 BootIntegrityGate(manifest.sig)
//    가 프록시 바이너리 해시를 별도로 봉인한다. 관리자 권한 공격자는 범위 밖.
// ============================================================================
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MS2E.Infrastructure
{
    public static partial class PipeServerIdentity
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

        /// <summary>연결된 클라이언트 파이프의 서버 PID. 실패 = false.</summary>
        public static bool TryGetServerProcessId(SafePipeHandle pipe, out int processId)
        {
            processId = -1;
            if (!OperatingSystem.IsWindows() || pipe is null || pipe.IsInvalid || pipe.IsClosed) return false;
            if (!GetNamedPipeServerProcessId(pipe, out uint pid) || pid == 0 || pid > int.MaxValue) return false;
            processId = (int)pid;
            return true;
        }

        /// <summary>PID → 실행 파일 전체 경로. 실패 = false.</summary>
        public static unsafe bool TryGetProcessImagePath(int processId, out string path)
        {
            path = "";
            if (!OperatingSystem.IsWindows() || processId <= 0) return false;
            using var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (h.IsInvalid) return false;
            const int Cap = 1024;
            char* buf = stackalloc char[Cap];
            uint size = Cap;
            if (!QueryFullProcessImageName(h, 0, buf, ref size) || size == 0 || size >= Cap) return false;
            path = new string(buf, 0, (int)size);
            return true;
        }

        /// <summary>기대 실행 파일 경로와 일치하는 서버만 통과시키는 검증기(대소문자 무시 · 정규화 경로).</summary>
        public static Func<int, bool> ImagePathVerifier(string expectedImagePath)
        {
            if (string.IsNullOrWhiteSpace(expectedImagePath)) throw new ArgumentException("기대 경로 필요", nameof(expectedImagePath));
            string expected = Path.GetFullPath(expectedImagePath);
            return pid => TryGetProcessImagePath(pid, out var actual)
                          && string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}

// ============================================================================
// TestHostExitSentinel — [크래시 트리아지] 테스트 호스트 종료 원인 기록기(모듈 초기화 · 콜드 경로)
//
//  배경(실측 2026-10-07): 전체 스위트 실행 중 testhost 가 관리 스택 없이 종료(VSTest 진단: exitcode -1 · stderr 공백)되는 사례가
//    반복됐다. 런타임 내부 원인(미처리 예외 · 스택 오버플로 · FailFast · AccessViolation)은 Windows 이벤트 로그 '.NET Runtime 1026' /
//    'Application Error 1000' 을 반드시 남기는데(2026-10-06 14:28 이전 크래시는 실제로 ObjectDisposedException 스택이 기록됨),
//    이후 크래시는 그 기록이 전무했다 → 프로세스 '내부' 원인인지 '외부' 강제 종료(TerminateProcess)인지 판별할 증거가 필요하다.
//
//  기록(%TEMP%\ms2e-testhost\<pid>.log · 이벤트 발생 시에만 쓰기 · 정상 실행 시 파일 0):
//    ① AppDomain.UnhandledException — 백그라운드 스레드 예외 전문(종료 직전) + 스레드 이름/ID
//    ② AppDomain.ProcessExit — Environment.Exit/정상 종료 시 종료 코드 + 호출 스택(누가 Exit 를 불렀는가)
//    ③ TaskScheduler.UnobservedTaskException — 관측 안 된 Task 예외(크래시는 아니지만 누수 단서)
//  판정: 크래시 후 그 pid 의 로그가 없으면 = 런타임이 아무 콜백도 실행하지 못한 종료 = 외부 TerminateProcess(또는 커밋 고갈로
//    CLR 이 콜백조차 못 돈 경우 — 시스템 로그 2004 '가상 메모리 부족'과 교차 확인).
//  안전: 기록 실패는 삼킨다(진단기가 크래시를 만들면 안 됨) · 핫패스 무관(이벤트 시에만).
// ============================================================================
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Tests
{
    internal static class TestHostExitSentinel
    {
        private static readonly object Gate = new();
        private static int _installed;

        public static string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "ms2e-testhost");
        public static string LogPath { get; } = Path.Combine(LogDirectory, Environment.ProcessId + ".log");

#pragma warning disable CA2255 // 테스트 어셈블리 전용 진단 — 라이브러리 모듈 초기화 경고 비해당
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write("UnhandledException(terminating=" + e.IsTerminating + ") thread=" + Thread.CurrentThread.ManagedThreadId + ":" + Thread.CurrentThread.Name + "\n" + e.ExceptionObject);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Write("ProcessExit exitCode=" + Environment.ExitCode + " thread=" + Thread.CurrentThread.ManagedThreadId + "\n" + Environment.StackTrace);
            TaskScheduler.UnobservedTaskException += (_, e) => Write("UnobservedTaskException\n" + e.Exception);
        }

        internal static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + "\n\n", new UTF8Encoding(false));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

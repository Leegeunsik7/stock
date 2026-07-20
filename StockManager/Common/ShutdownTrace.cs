// ============================================================================
// ShutdownTrace — 종료 지연 진단 계측(운영 버그 추적)
//
//  프로그램 종료 시 각 단계(WM_CLOSE→FormClosing→FormClosed 핸들러들→Exit)의 소요 시간을 고해상도로 파일에 기록한다.
//  '닫으면 바로 안 닫힘'의 정확한 원인(어느 핸들러/단계가 몇 ms 잡아먹는지)을 실측하기 위한 일회성 계측 — 트레이딩
//  결정 경로가 아니므로 Stopwatch/DateTime 사용 허용(δ=0·결정론 불변식은 트레이딩 핫패스에만 적용).
//
//  로그 경로: %LOCALAPPDATA%\MS2E.StockSuite\shutdown_trace.log (종료마다 append).
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace StockManager.Common
{
    /// <summary>종료 단계별 소요 시간을 파일에 기록하는 진단 계측(스레드 안전·예외 격리).</summary>
    public static class ShutdownTrace
    {
        private static readonly object _lock = new();
        private static readonly StringBuilder _sb = new();
        private static long _t0;

        public static string LogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MS2E.StockSuite", "shutdown_trace.log");

        /// <summary>종료 시퀀스 시작(WM_CLOSE 최초 진입) — 기준 시각 리셋.</summary>
        public static void Begin(string origin)
        {
            lock (_lock)
            {
                if (_t0 != 0) return;   // 이미 시작됨(중복 WM_CLOSE 방어)
                _t0 = Stopwatch.GetTimestamp();
                _sb.Clear();
                _sb.AppendLine($"[shutdown trace] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  origin={origin}");
            }
        }

        /// <summary>단계 경계 마킹(기준 시각 대비 경과 ms).</summary>
        public static void Mark(string phase)
        {
            lock (_lock)
            {
                double ms = _t0 == 0 ? 0 : Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;
                _sb.AppendLine($"  {ms,9:F1} ms | {phase}");
            }
        }

        /// <summary>이름붙은 종료 단계 실행 + 개별 소요 기록(예외 격리). 어느 Dispose 가 느린지 특정용.</summary>
        public static void Step(string name, Action a)
        {
            long s = Stopwatch.GetTimestamp();
            string tag = name;
            try { a(); }
            catch (Exception ex) { tag = name + " THREW " + ex.GetType().Name; }
            double dur = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
            lock (_lock)
            {
                double at = _t0 == 0 ? 0 : Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;
                _sb.AppendLine($"  {at,9:F1} ms | step {tag,-24} took {dur,8:F1} ms");
            }
        }

        /// <summary>누적 로그를 파일에 flush(Exit 직전 호출).</summary>
        public static void Flush()
        {
            try
            {
                lock (_lock)
                {
                    double total = _t0 == 0 ? 0 : Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;
                    _sb.AppendLine($"  {total,9:F1} ms | == shutdown total ==");
                    var dir = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(LogPath, _sb.ToString() + Environment.NewLine);
                }
            }
            catch { /* 진단 실패는 종료를 막지 않음 */ }
        }
    }
}

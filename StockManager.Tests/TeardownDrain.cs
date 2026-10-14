// ============================================================================
// TeardownDrain — [v12.18.9-DEV] 시험 스레드 수명 종료 규약(Hard Drain → Dispose-or-Quarantine)
//
//  결함(실측 · 전체 스위트 부하): 시험이 작업 스레드를 시간 제한 Join 으로 기다린 뒤 결과를 무시하거나(시간 초과) 단언 실패로 빠져나가면
//   `using var` 가 동기화 객체 · MMF · 비관리 메모리를 '살아 있는 스레드 밑에서' 폐기한다. 그 스레드의 다음 접근 = ObjectDisposedException
//   (원시 스레드 → 프로세스 종료 · 0xE0434352) 또는 비관리 메모리 해제 후 사용(AV). 실측: S29GammaRingTelemetryTests Barrier ODE 로
//   시험 호스트 종료(호스트 '중단'만 보고 · 이후 시험 전량 미실행).
//  규약: ① 정지 신호(취소 · 플래그 · 이벤트 Set) → ② JoinAll(예산) → ③ 전부 종료 = Dispose · 하나라도 생존 = Quarantine(정적 루트로
//   영구 보유 — 폐기하지 않는다) + 시험은 시간 초과로 '실패'(크래시 아님). 누수는 프로세스 종료까지의 메모리뿐 — 크래시보다 낫다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace StockManager.Tests
{
    internal static class TeardownDrain
    {
        private static readonly List<object> s_quarantine = new();
        private static long s_quarantined;

        /// <summary>격리(미폐기) 처리된 객체 누계 — 0 이 아니면 어떤 시험이 생존 스레드를 남겼다.</summary>
        public static long Quarantined => Interlocked.Read(ref s_quarantined);

        /// <summary>남은 예산 안에서 모든 스레드 종료 대기. 반환 = 전부 종료.</summary>
        public static bool JoinAll(TimeSpan budget, params Thread?[] threads)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool all = true;
            foreach (var t in threads)
            {
                if (t is null || t.ThreadState.HasFlag(ThreadState.Unstarted)) continue;
                var left = budget - sw.Elapsed;
                if (!t.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero)) all = false;
            }
            return all;
        }

        /// <summary>전부 종료면 폐기 · 아니면 격리(정적 루트 보유 · 폐기 0). 폐기 예외는 삼킨다(정리 경로가 시험 결과를 덮지 않게).</summary>
        public static void DisposeOrQuarantine(bool drained, params IDisposable?[] items)
        {
            foreach (var d in items)
            {
                if (d is null) continue;
                if (drained) { try { d.Dispose(); } catch (Exception) { } }
                else { lock (s_quarantine) s_quarantine.Add(d); Interlocked.Increment(ref s_quarantined); }
            }
        }
    }
}

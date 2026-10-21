// ============================================================================
// ZeroAllocProbe — [v12.18.48-DEV] 무할당(δ=0) 단언 공통 측정기(다중 창 최솟값)
//
//  왜: GC.GetAllocatedBytesForCurrentThread() 단일 창 측정은 타 스레드가 일으킨 GC 와 창이 겹치면 할당 컨텍스트 회계가
//   튀어 실 할당 0 인데도 수 KB 를 보고한다(실측: v12.18.47-GM [7/11] 6,264B · 2026-10-07 8,168B). 테스트 61곳이 같은 헬퍼를
//   각자 복제하고 있었고, 100여 곳은 여전히 단일 창이었다 → 공통 헬퍼 1개로 통일.
//  판정력: 실 누수(호출당 할당)는 '모든' 창에서 > 0 → 최솟값도 > 0 → 실패. 임계는 0 그대로 — 완화 없음. 노이즈만 걸러진다.
//  창 규칙: 첫 창이 0 이면 즉시 종료(본문 1회 실행 = 기존 단일 창과 같은 부작용) · > 0 일 때만 최대 windows 회 재측정.
//   ※ 본문이 상태를 소모해 재실행 시 다른 경로를 타면(예: 용량 고갈 → 거부 경로) 재측정이 그 경로를 재게 된다 — 그런 본문은
//     매 창 상태를 되돌리는 reset 을 넘긴다(reset 은 측정 창 밖에서 실행).
//  δ: 헬퍼 자체는 창 안에서 할당 0(델리게이트는 호출 전에 만들어짐 · Invoke 는 무할당).
// ============================================================================
using System;

namespace StockManager.Tests
{
    internal static class ZeroAllocProbe
    {
        public const int DefaultWindows = 3;

        /// <summary>본문 실행 동안 현재 스레드 할당 바이트의 창별 최솟값(첫 0 에서 조기 종료). reset = 매 창 직전(측정 밖) 상태 복원.</summary>
        public static long MinAllocated(Action body, int windows = DefaultWindows, Action? reset = null)
        {
            ArgumentNullException.ThrowIfNull(body);
            if (windows < 1) throw new ArgumentOutOfRangeException(nameof(windows));
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                reset?.Invoke();
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        /// <summary>
        /// 기록형 판정(재실행 불가 — 동시성 단계 · 비동기 UI 루프 · 상태 일회성 시퀀스): 연속 측정 창 중 0 이 아닌 창이 최대 1개 · 창 ≥ 3.
        /// 실 누수(호출당 할당)는 모든 창 > 0 → 실패. 창 1개 잡음(GC 겹침)만 허용 — 임계 0 은 나머지 창 전부에 그대로 적용.
        /// </summary>
        public const int MaxNoisyWindows = 1;
        public const int MinRecordedWindows = 3;

        public static bool PassesRecorded(long totalWindows, long nonZeroWindows)
            => totalWindows >= MinRecordedWindows && nonZeroWindows <= MaxNoisyWindows && nonZeroWindows >= 0;

        public static bool PassesRecorded(ReadOnlySpan<long> windows)
        {
            int nz = 0;
            foreach (long w in windows) if (w != 0) nz++;
            return PassesRecorded(windows.Length, nz);
        }

        /// <summary>자체 측정 함수(창 1개의 할당 바이트를 반환)를 최대 windows 회 호출한 최솟값(첫 0 에서 조기 종료).</summary>
        public static long MinOf(Func<long> measure, int windows = DefaultWindows)
        {
            ArgumentNullException.ThrowIfNull(measure);
            if (windows < 1) throw new ArgumentOutOfRangeException(nameof(windows));
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++) min = Math.Min(min, measure());
            return min;
        }
    }
}

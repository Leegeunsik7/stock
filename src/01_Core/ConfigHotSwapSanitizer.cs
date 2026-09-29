// ============================================================================
// ConfigHotSwapSanitizer — v12.13.0 [Step 7/7] Think Beyond Gemini Idea A:
//   Zero-Alloc Config Hot-Swap Struct Sanitizer
//
//   SettingsPage.razor 감사에서 확인: 각 Apply*() 메서드가 `Math.Clamp(...)` 를 산발적으로 직접 호출해 값을
//   교정하고 있었다(LAN 포트/SMTP 포트/레짐 간격/드리프트 밴드·간격/야간 발송 시각/Ollama 타임아웃 — 6곳, 개별
//   경계값 하드코딩). 동작은 이미 정확했으나(감사 결과 결함 無) 동일 패턴이 반복돼 신규 설정 추가 시 검증이
//   누락되기 쉬운 구조였다. 이 엔진은 그 검증을 단일 δ=0 값타입 유틸로 통일한다.
//
//   불변: 결정론(순수 함수) · δ=0(Sanitize/SanitizeBatch 힙 0 — 값타입 입출력) · fail-closed(경계 자체가
//        모순(Min&gt;Max)이면 DefaultValue 로 안전 폴백) · liveArmed:false(설정 값 교정만 — 발주 경로 무접촉).
// ============================================================================
using System;

namespace MS2E.Core
{
    /// <summary>정수 설정값의 허용 범위(값타입) — Min/Max/경계 위반 시 폴백할 DefaultValue.</summary>
    public readonly record struct ConfigBoundary(long Min, long Max, long DefaultValue);

    public static class ConfigHotSwapSanitizer
    {
        /// <summary>단일 설정값을 경계 내로 즉시 교정(δ=0). 경계 자체가 모순(Min&gt;Max)이면 DefaultValue 로
        /// 안전 폴백(fail-closed — 잘못된 경계 설정이 잘못된 값을 통과시키지 않도록).</summary>
        public static long Sanitize(long value, ConfigBoundary boundary)
        {
            if (boundary.Min > boundary.Max) return boundary.DefaultValue;
            if (value < boundary.Min) return boundary.Min;
            if (value > boundary.Max) return boundary.Max;
            return value;
        }

        /// <summary>여러 설정값을 배치 교정(δ=0 — 호출자 제공 결과 버퍼). values/boundaries/결과 span 은 동일 길이.</summary>
        public static void SanitizeBatch(ReadOnlySpan<long> values, ReadOnlySpan<ConfigBoundary> boundaries, Span<long> resultsOut)
        {
            int n = values.Length;
            if (boundaries.Length != n || resultsOut.Length != n)
                throw new ArgumentException("values/boundaries/results span 길이가 모두 일치해야 합니다.");
            for (int i = 0; i < n; i++) resultsOut[i] = Sanitize(values[i], boundaries[i]);
        }

        /// <summary>교정이 실제로 발생했는지(원본 값이 경계를 벗어났었는지) — UI 피드백("입력값이 조정되었습니다")용.</summary>
        public static bool WasClamped(long original, long sanitized) => original != sanitized;
    }
}

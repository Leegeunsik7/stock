// ============================================================================
// UtcNowLeapCacheExpirer — [v12.18.17-DEV] δ=0 측정 적대 도구: DateTime.UtcNow 윤초 캐시 강제 만료
//
//  배경(실측 · 2026-10-18 · .NET 8 · Windows 10 19045): 윤초 지원 OS 에서 DateTime.UtcNow 는 System.DateTime+LeapSecondCache(32B)를
//   유효 창(약 7분 24초) 만료 후 '첫 호출자 스레드'에서 새로 할당한다. δ=0 측정 창 안에 숨은 UtcNow 호출이 있으면 그 창은 수 분에 한 번만 32B 를 보인다 —
//   전체 스위트(약 4분)에서만 드물게 보이는 플래키의 정체(v12.18.11 기록 'LongSoak 3중 경합 퍼저 32B').
//  이 도구: 만료된(필드 0) 캐시 인스턴스를 미리 만들어 두고(콜드) Expire() 마다 하나씩 꽂는다(할당 0 — 준비 단계에서 실측 검증).
//   측정 직전 Expire() → 창 안의 숨은 UtcNow 는 '매번' 32B → 드문 플래키가 결정론적 실패로 바뀐다.
//  비지원(윤초 미지원 OS · 런타임 내부 구조 변경) = Supported false · Expire 무동작(검출력 저하만 · 오실패 0). 테스트 전용(제품 코드 무관).
// ============================================================================
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StockManager.Tests
{
    internal sealed class UtcNowLeapCacheExpirer
    {
        private readonly FieldInfo? _field;
        private readonly object[] _stale = Array.Empty<object>();
        private int _next;

        public bool Supported { get; }
        public int Capacity => _stale.Length;
        public int Used => Math.Min(Volatile.Read(ref _next), _stale.Length);

        /// <param name="capacity">Expire 가능 횟수(각 32B 사전 할당 · 소진 후 무동작).</param>
        public UtcNowLeapCacheExpirer(int capacity)
        {
            var cacheType = typeof(DateTime).GetNestedType("LeapSecondCache", BindingFlags.NonPublic);
            if (cacheType is null) return;
            const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
            var supports = cacheType.GetField("s_systemSupportsLeapSeconds", S) ?? typeof(DateTime).GetField("s_systemSupportsLeapSeconds", S);
            _field = cacheType.GetField("s_leapSecondCache", S) ?? typeof(DateTime).GetField("s_leapSecondCache", S);
            if (_field is null || _field.FieldType != cacheType || _field.IsInitOnly || supports?.GetValue(null) is not true) { _field = null; return; }
            _stale = new object[capacity];
            for (int i = 0; i < capacity; i++) _stale[i] = RuntimeHelpers.GetUninitializedObject(cacheType);
            // 자기 검증: 만료 → UtcNow 가 실제로 할당하는가(검출력) · Expire 자체는 할당 0 인가(측정 오염 0)
            var probe = RuntimeHelpers.GetUninitializedObject(cacheType);
            for (int w = 0; w < 3; w++) { _field.SetValue(null, probe); _ = DateTime.UtcNow; }
            _field.SetValue(null, probe);
            long a = GC.GetAllocatedBytesForCurrentThread();
            _ = DateTime.UtcNow;
            bool detects = GC.GetAllocatedBytesForCurrentThread() - a > 0;
            long b = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++) _field.SetValue(null, probe);
            bool cheap = GC.GetAllocatedBytesForCurrentThread() - b == 0;
            _ = DateTime.UtcNow;                                                    // 정상 캐시 복원
            Supported = detects && cheap;
            if (!Supported) { _field = null; _stale = Array.Empty<object>(); }
        }

        /// <summary>다음 DateTime.UtcNow 호출이 캐시를 재할당하게 만든다(임의 스레드 · 할당 0). 소진/비지원 = 무동작.</summary>
        public void Expire()
        {
            var f = _field;
            if (f is null) return;
            int i = Interlocked.Increment(ref _next) - 1;
            if (i < _stale.Length) f.SetValue(null, _stale[i]);
        }
    }
}

// ============================================================================
// StateAwareRenderGovernor — v10.1.0 Phase 94-A: 상태 인지 렌더링 거버너 (Gemini 보강 과제)
//
//   백그라운드 핫패스 고부하 또는 ForceFreeze 상태 시 UI 컴포넌트의 불필요한 재렌더링(StateHasChanged)을 코얼레싱해
//   DOM 스래싱을 차단하는 무할당(δ=0) 렌더 스로틀. 컴포넌트가 ref long 슬롯(마지막 렌더 µs)을 보유하고, 렌더 직전
//   ShouldRender 로 게이팅한다. 상태별 최소 렌더 간격: Normal=무제한 · Elevated=50ms · High=100ms · Frozen=250ms.
//
//   ★불변: δ=0(ShouldRender 핫패스 힙 0 — 순수 산술 + Interlocked) · 결정론(주입 µs 시계) · fail-closed(ForceFreeze →
//     하드 스로틀[250ms]로 버스트 렌더 억제) · liveArmed:false(순수 렌더 판정 — 실 발주 유발 0).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    /// <summary>UI 부하 등급.</summary>
    public enum UiLoadLevel : byte { Normal = 0, Elevated = 1, High = 2 }

    /// <summary>δ=0 상태 인지 렌더 스로틀 거버너. 고부하/동결 시 재렌더 코얼레싱(DOM 스래싱 차단).</summary>
    public sealed class StateAwareRenderGovernor
    {
        private const long ElevatedMicros = 50_000;   // 20fps
        private const long HighMicros     = 100_000;  // 10fps
        private const long FrozenMicros   = 250_000;  // 4fps(안전 관제 하드 스로틀)

        private long _loadLevel;   // UiLoadLevel
        private long _frozen;      // 0/1
        private long _allowed, _throttled;

        public void SetLoad(UiLoadLevel level) => Interlocked.Exchange(ref _loadLevel, (long)level);
        public void SetFrozen(bool frozen)     => Interlocked.Exchange(ref _frozen, frozen ? 1L : 0L);

        public bool Frozen        => Volatile.Read(ref _frozen) == 1L;
        public UiLoadLevel Load   => (UiLoadLevel)Volatile.Read(ref _loadLevel);
        public long Allowed       => Interlocked.Read(ref _allowed);
        public long Throttled     => Interlocked.Read(ref _throttled);

        /// <summary>
        /// ★δ=0 재렌더 허용 여부. 허용 시 lastRenderMicros 를 now 로 갱신하고 true. 최소 간격 미달(고부하/동결) → false
        /// (렌더 코얼레싱). Normal 상태는 항상 허용. 컴포넌트: <c>if (gov.ShouldRender(ref _slot, now)) StateHasChanged();</c>
        /// </summary>
        public bool ShouldRender(ref long lastRenderMicros, long nowMicros)
        {
            long minGap = MinGapMicros();
            if (minGap == 0L || (nowMicros - lastRenderMicros) >= minGap)
            {
                lastRenderMicros = nowMicros;
                Interlocked.Increment(ref _allowed);
                return true;
            }
            Interlocked.Increment(ref _throttled);
            return false;
        }

        private long MinGapMicros()
        {
            if (Volatile.Read(ref _frozen) == 1L) return FrozenMicros;   // ★동결 우선(하드 스로틀)
            return (UiLoadLevel)Volatile.Read(ref _loadLevel) switch
            {
                UiLoadLevel.High     => HighMicros,
                UiLoadLevel.Elevated => ElevatedMicros,
                _                    => 0L,
            };
        }
    }
}

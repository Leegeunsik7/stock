// ============================================================================
// SnapshotRenderGovernor + ZeroAllocNumberText — [시세 하드닝 §2] Adaptive State-Aware Render Governor
//
//   교정 전(실측): StateAwareRenderGovernor.ShouldRender 는 "선행 에지"만 있는 호출측 풀(pull) 게이트다 —
//     ① 부하 등급 Normal 에서는 간격 0 → 이벤트마다 렌더(시세 연타 시 StateHasChanged 폭주)
//     ② 스로틀 창 안에서 거부된 마지막 변경은 "다음 이벤트가 올 때까지" 렌더되지 않는다(버스트가 끝나면 최종 시세가 화면에 영영
//        반영되지 않는 유실 — 후행 에지 부재).
//   이 거버너는 푸시(push)형으로 두 결함을 막는다:
//     ▸ Signal(snapshotVersion) — 임의 스레드 · δ=0(Interlocked 만). 유휴면 즉시 1회 렌더(선행), 창(기본 100ms) 안의 신호는
//       더티 1비트로 흡수되고 창 종료 타이머가 1회 후행 렌더 → 마지막 상태 반드시 반영.
//     ▸ 스냅샷 변경 감지 — 후행 시점의 버전이 직전 렌더 버전과 같으면 렌더 생략(값 미변동 → StateHasChanged 0).
//     ▸ 적응 창 — StateAwareRenderGovernor(선택)의 부하/동결 등급이 요구하는 간격이 더 길면 그 간격을 따른다(동결 250ms 등).
//     ▸ 렌더 콜백 예외는 타이머 스레드로 전파하지 않고 계수. Dispose 후 신호·타이머 무동작(파기된 컴포넌트 렌더 0).
//   ZeroAllocNumberText — 0~1023 정수 문자열을 정적 캐시에서 반환(할당 0), 그 밖의 값은 "직전 값과 같으면 직전 문자열 재사용"
//     (값 미변동 시 string 할당 0 · 변동 시에만 1회). Blazor DOM 텍스트는 string 이 필요하므로 새 큰 값 1회 할당은 물리 한계(정직).
//   Pure RCL: BCL(System.Threading.ITimer/TimeProvider)만 사용 — 엔진 참조 0.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    public sealed class SnapshotRenderGovernor : IDisposable
    {
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(100);
        private const int Idle = 0, Cooling = 1, CoolingDirty = 2, Disposed = 3;

        private readonly Action _render;
        private readonly TimeSpan _window;
        private readonly StateAwareRenderGovernor? _load;
        private readonly ITimer _timer;
        private int _state;
        private long _pendingVersion, _renderedVersion = long.MinValue;
        private long _signals, _renders, _suppressedUnchanged, _faults;

        public SnapshotRenderGovernor(Action render, TimeSpan? window = null, StateAwareRenderGovernor? loadGovernor = null, TimeProvider? time = null)
        {
            _render = render ?? throw new ArgumentNullException(nameof(render));
            _window = window ?? DefaultWindow;
            if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
            _load = loadGovernor;
            _timer = (time ?? TimeProvider.System).CreateTimer(static s => ((SnapshotRenderGovernor)s!).OnWindow(), this,
                                                              Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public long Signals => Interlocked.Read(ref _signals);
        public long Renders => Interlocked.Read(ref _renders);
        public long SuppressedUnchanged => Interlocked.Read(ref _suppressedUnchanged);
        public long RenderFaults => Interlocked.Read(ref _faults);

        /// <summary>상태 변경 신호(δ=0 · 임의 스레드). snapshotVersion = 표시 모델의 단조 버전(값이 바뀔 때만 증가).</summary>
        public void Signal(long snapshotVersion)
        {
            Interlocked.Increment(ref _signals);
            Volatile.Write(ref _pendingVersion, snapshotVersion);
            while (true)
            {
                int s = Volatile.Read(ref _state);
                if (s == Idle)
                {
                    if (Interlocked.CompareExchange(ref _state, Cooling, Idle) != Idle) continue;
                    Arm();
                    RenderIfChanged();
                    return;
                }
                if (s == Cooling)
                {
                    if (Interlocked.CompareExchange(ref _state, CoolingDirty, Cooling) != Cooling) continue;
                    return;
                }
                return;   // CoolingDirty(후행 예약됨) · Disposed
            }
        }

        private void OnWindow()
        {
            while (true)
            {
                int s = Volatile.Read(ref _state);
                if (s == CoolingDirty)
                {
                    if (Interlocked.CompareExchange(ref _state, Cooling, CoolingDirty) != CoolingDirty) continue;
                    Arm();
                    RenderIfChanged();   // ★후행 에지 — 창 안 마지막 상태 반영
                    return;
                }
                if (s == Cooling) { if (Interlocked.CompareExchange(ref _state, Idle, Cooling) != Cooling) continue; }
                return;
            }
        }

        private void Arm()
        {
            TimeSpan w = _window;
            if (_load is not null)
            {
                TimeSpan lg = _load.Frozen ? TimeSpan.FromMilliseconds(250) : _load.Load switch
                {
                    UiLoadLevel.High => TimeSpan.FromMilliseconds(100),
                    UiLoadLevel.Elevated => TimeSpan.FromMilliseconds(50),
                    _ => TimeSpan.Zero,
                };
                if (lg > w) w = lg;   // 적응 창 — 부하/동결이 더 긴 간격을 요구하면 따른다
            }
            try { _timer.Change(w, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        private void RenderIfChanged()
        {
            if (Volatile.Read(ref _state) == Disposed) return;
            long v = Volatile.Read(ref _pendingVersion);
            if (Interlocked.Exchange(ref _renderedVersion, v) == v) { Interlocked.Increment(ref _suppressedUnchanged); return; }   // 값 미변동 → 렌더 0
            Interlocked.Increment(ref _renders);
            try { _render(); } catch { Interlocked.Increment(ref _faults); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, Disposed) == Disposed) return;
            _timer.Dispose();
        }
    }

    /// <summary>수치 → 표시 문자열(δ=0 경로). 0~1023 정적 캐시 · 그 외는 직전 값 재사용(인스턴스별 단일 슬롯).</summary>
    public sealed class ZeroAllocNumberText
    {
        private static readonly string[] Small = BuildSmall();
        private long _lastValue = long.MinValue;
        private string _lastText = "";

        private static string[] BuildSmall()
        {
            var a = new string[1024];
            for (int i = 0; i < a.Length; i++) a[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return a;
        }

        public string For(long value)
        {
            if ((ulong)value < 1024UL) return Small[value];
            if (value == _lastValue) return _lastText;   // 미변동 → 할당 0
            _lastText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _lastValue = value;
            return _lastText;
        }
    }
}

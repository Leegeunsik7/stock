// ============================================================================
// AutonomousUiSwitchVerifier — [자율 UI 검증 · 추천 A] 헤드리스 Blazor 스위치 퍼저 · 무장 인터락 수검 코어
//
//  목적: 순수 RCL(MS2E.UI.Portable)의 실 컴포넌트(설정 · 내비 · 대시보드)를 메모리에서 렌더하고, 그 안의 모든 클릭 가능 요소
//  (onclick 요소 · checkbox · 내비 앵커)를 순차/무작위로 '실제 이벤트 디스패치' 해 — 어떤 조합의 조작으로도 liveArmed 가
//  true 로 전이되지 않고(물리 이중 키 부재), 30 인터락 비트(v12.17.6)가 1비트도 오염되지 않음을 매 상호작용마다 판정한다.
//
//  구성(클릭 주입기는 IUiClickSurface 로 추상화 — CLI 는 본 파일의 HeadlessUiSurface, 테스트는 bUnit 표면을 꽂는다):
//   ① UiStateFingerprint / UiStateMatrix   [방향 A] 렌더 상태 무할당 해싱(FNV-1a 64) + 고정 용량 지문 집합(상태 폭주·꼬임 추적).
//      · 토글 대칭: checkbox 를 2회 토글하면 그 루트의 지문이 원상 복귀해야 한다(불일치 = 상태 꼬임 · SymmetryBreaks).
//      · 고유 상태 수 · 포화(Saturated)는 진단 관측치 — 난수 PIN 생성 등 정당한 무한 상태 공간이 있어 판정에서 제외.
//   ② SwitchStormIsolationGuard           [방향 B] UI 조작 고정 창 허용 계수기(CAS · δ=0) — 초과 조작은 폐기(큐잉·재시도 없음).
//      CoreTickIsolationMonitor             스톰 동안 전용 스레드에서 코어 틱(허브 OnTick + 집계어 판독)의 처리 시간을 측정.
//   ③ UiArmingInvariantProbe               실 안전 코어(집계어 29 소스 + 샌니티 = 30 비트 · 포렌식 기록기 · LiveArmingGate ·
//      DispatchArming 술어 · CanaryHealthSentinel)를 호스트와 같은 설정 경로(RuntimeConfig ← 설정 저장소)로 조립하고, 매 회전
//      '호스트가 재무장 시 하는 일'(설정의 이중 키로 카나리 Arm 시도)까지 재현해 불법 무장을 판정.
//   ④ AutonomousUiSwitchFuzzer             순차 스윕 → 시드 고정 무작위 → 적대 기입(UI 포트로 무장 키 직접 기입) 루프 + 보고서.
//
//  ★liveArmed:false — 본 하네스는 IBroker · 소켓 · onSend 참조 0. 무장 키를 '시도' 하는 적대 기입도 UI 포트(→ UiSettingWriteGuard)
//    경유만 하며, 판정은 관측(읽기)만 한다. CLI 는 운영 DB 가 아닌 임시 LocalDatabase 로만 동작(UiSwitchVerifierCli).
//  ★δ=0 범위: 지문 해싱 · 지문 집합 Observe · 스톰 가드 TryAdmit · 코어 틱 루프. 렌더러/디스패치 자체는 UI 측(할당 허용).
// ============================================================================
#pragma warning disable BL0006   // RenderTree 타입 직접 사용 — 헤드리스 렌더러(bUnit 와 동일 기법)는 프레임 판독이 본업
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports;
using StockManager.Blazor.Services;
using StockManager.Telemetry;

namespace StockManager.Simulation
{
    // ════════════════════════════════════════════════════════════════════════
    //  ① [방향 A] 무할당 UI 상태 지문
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>FNV-1a 64 증분 해셔(값 타입 · 힙 0). UTF-16 코드 유닛을 하위/상위 바이트 순으로 접는다.</summary>
    public struct Fnv64
    {
        public const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _h;

        public static Fnv64 Create() => new() { _h = Offset };
        public readonly ulong Value => _h;

        public void Add(char c)
        {
            ulong h = _h;
            h = (h ^ (byte)c) * Prime;
            h = (h ^ (byte)(c >> 8)) * Prime;
            _h = h;
        }

        public void Add(ReadOnlySpan<char> s)
        {
            ulong h = _h;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                h = (h ^ (byte)c) * Prime;
                h = (h ^ (byte)(c >> 8)) * Prime;
            }
            _h = h;
        }

        /// <summary>필드 경계 표지(서로 다른 필드 분할이 같은 해시로 접히는 것을 방지).</summary>
        public void Separator() => Add('\u0001');
    }

    public static class UiStateFingerprint
    {
        /// <summary>
        /// 렌더 마크업 지문(δ=0). bUnit 마크업의 <c>blazor:on*="17"</c> 같은 렌더러 내부 속성(이벤트 핸들러 id · 요소 참조)은
        /// 렌더마다 번호가 바뀌는 비결정 값이므로 이름·값 모두 건너뛴다 — 같은 화면 상태 = 같은 지문.
        /// </summary>
        public static ulong HashMarkup(ReadOnlySpan<char> markup)
        {
            var h = Fnv64.Create();
            int i = 0, n = markup.Length;
            while (i < n)
            {
                // 공백 + "blazor:" 로 시작하는 속성 토큰 → 다음 공백/'>'(따옴표 값 포함)까지 생략
                if (markup[i] == ' ' && i + 8 <= n && markup.Slice(i + 1, 7).SequenceEqual("blazor:"))
                {
                    int j = i + 8;
                    bool quoted = false;
                    while (j < n)
                    {
                        char c = markup[j];
                        if (c == '"') quoted = !quoted;
                        else if (!quoted && (c == ' ' || c == '>' || c == '/')) break;
                        j++;
                    }
                    i = j;
                    continue;
                }
                h.Add(markup[i]);
                i++;
            }
            return Remap(h.Value);
        }

        /// <summary>0 은 빈 슬롯 표지로 예약 — 지문 0 은 1 로 사상.</summary>
        public static ulong Remap(ulong v) => v == 0 ? 1UL : v;
    }

    /// <summary>
    /// 고정 용량 지문 집합(선형 탐사 · 생성 후 할당 0). 고유 상태 수 추적 — 부하율 3/4 이후 새 상태는 Saturated 로 계수(진단).
    /// 단일 라이터(퍼저 루프) 전용.
    /// </summary>
    public sealed class UiStateMatrix
    {
        private readonly ulong[] _slots;
        private readonly int _mask;
        private int _distinct;
        private long _observations, _saturated;

        public UiStateMatrix(int capacityPow2 = 4096)
        {
            if (capacityPow2 < 16 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacityPow2));
            _slots = new ulong[capacityPow2];
            _mask = capacityPow2 - 1;
        }

        public int Capacity => _slots.Length;
        public int Distinct => _distinct;
        public long Observations => _observations;
        public long Saturated => _saturated;

        /// <summary>지문 관측. 새 상태면 true. 부하율 3/4 초과 시 새 상태는 기록하지 않고 Saturated 계수.</summary>
        public bool Observe(ulong fingerprint)
        {
            _observations++;
            ulong fp = UiStateFingerprint.Remap(fingerprint);
            int i = (int)(fp ^ (fp >> 29)) & _mask;
            while (true)
            {
                ulong s = _slots[i];
                if (s == fp) return false;
                if (s == 0)
                {
                    if (_distinct >= (_slots.Length >> 2) * 3) { _saturated++; return false; }
                    _slots[i] = fp;
                    _distinct++;
                    return true;
                }
                i = (i + 1) & _mask;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ② [방향 B] 스위치 스톰 격리 가드 + 코어 틱 격리 계측
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// UI 조작 고정 창 허용 계수기(락 없음 · δ=0). 창(windowTicks)당 maxPerWindow 건만 통과, 초과는 폐기(Dropped 계수).
    /// ★큐잉·재시도 없음 — 스톰은 '버려서' 흡수한다(코어 틱 스레드와 CPU/GC 를 다투는 UI 작업량 상한).
    /// 창 경계 경합: 창 교체 CAS 승자만 계수기를 리셋 — 경계에서 최대 동시 호출자 수만큼 초과 허용 가능(상한 유한 · 문서화).
    /// </summary>
    public sealed class SwitchStormIsolationGuard
    {
        private readonly int _max;
        private readonly long _window;
        private readonly Func<long> _now;
        private long _windowStart, _inWindow, _admitted, _dropped, _windows;

        /// <param name="maxPerWindow">창당 허용 조작 수(≥1).</param>
        /// <param name="windowTicks">창 길이(Stopwatch 틱 · ≥1).</param>
        /// <param name="nowTicks">단조 시계(기본 Stopwatch.GetTimestamp · 테스트 주입).</param>
        public SwitchStormIsolationGuard(int maxPerWindow, long windowTicks, Func<long>? nowTicks = null)
        {
            _max = Math.Max(1, maxPerWindow);
            _window = Math.Max(1, windowTicks);
            _now = nowTicks ?? Stopwatch.GetTimestamp;
            _windowStart = _now();
        }

        public static SwitchStormIsolationGuard PerMilliseconds(int maxPerWindow, int windowMs)
            => new(maxPerWindow, Stopwatch.Frequency * Math.Max(1, windowMs) / 1000);

        public int MaxPerWindow => _max;
        public long Admitted => Interlocked.Read(ref _admitted);
        public long Dropped => Interlocked.Read(ref _dropped);
        public long Windows => Interlocked.Read(ref _windows);

        public bool TryAdmit()
        {
            long now = _now();
            long ws = Volatile.Read(ref _windowStart);
            if (now - ws >= _window && Interlocked.CompareExchange(ref _windowStart, now, ws) == ws)
            {
                Interlocked.Exchange(ref _inWindow, 0);
                Interlocked.Increment(ref _windows);
            }
            if (Interlocked.Increment(ref _inWindow) <= _max) { Interlocked.Increment(ref _admitted); return true; }
            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

    /// <summary>
    /// 코어 틱 격리 계측 — 전용 스레드가 '코어 틱 1건'(대시보드 허브 OnTick = 실 생산자 경로 + 집계어 Blocked 판독)을 반복하며 처리
    /// 시간을 잰다. UI 스톰이 같은 프로세스에서 돌 때 틱 처리 시간이 예산(기본 1ms)을 넘은 건수 · 최댓값 · 스레드 할당을 보고.
    /// ★정직 공개: 측정값에는 OS 선점(틱 처리 도중 스레드가 밀려남)이 포함된다 — 프로세스 내 코드는 선점을 막을 수 없으므로
    ///   판정은 '최댓값 &lt; 예산' 이 아니라 '예산 초과 비율 ≤ 허용 비율' 로 한다(최댓값은 보고만).
    /// </summary>
    public sealed class CoreTickIsolationMonitor : IDisposable
    {
        private readonly DashboardSnapshotHub _hub;
        private readonly ArmingInterlockBitmask _mask;
        private readonly long _budget;
        private Thread? _thread;
        private volatile bool _stop;
        private long _ticks, _overBudget, _maxTicks, _blockedSeen, _allocated = -1;

        public CoreTickIsolationMonitor(DashboardSnapshotHub hub, ArmingInterlockBitmask mask, long budgetTicks = 0)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _mask = mask ?? throw new ArgumentNullException(nameof(mask));
            _budget = budgetTicks > 0 ? budgetTicks : Stopwatch.Frequency / 1000;   // 1ms
        }

        public long Ticks => Interlocked.Read(ref _ticks);
        public long OverBudget => Interlocked.Read(ref _overBudget);
        public long BudgetTicks => _budget;
        public double MaxMicros => Interlocked.Read(ref _maxTicks) * 1_000_000.0 / Stopwatch.Frequency;
        public long BlockedSeen => Interlocked.Read(ref _blockedSeen);
        /// <summary>틱 루프(워밍업 이후) 스레드 할당 바이트 — 정지 후 확정(-1 = 미확정).</summary>
        public long AllocatedBytes => Interlocked.Read(ref _allocated);

        public void Start()
        {
            if (_thread is not null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ui-fuzz-core-tick", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _thread?.Join();
        }

        private void Loop()
        {
            const long Symbol = 5930;
            long price = 70_000;
            for (int w = 0; w < 1024; w++) { _hub.OnTick(Symbol, price); _ = _mask.Blocked; }   // JIT/티어 워밍업(측정 제외)
            long allocBase = GC.GetAllocatedBytesForCurrentThread();
            long max = 0;
            while (!_stop)
            {
                long t0 = Stopwatch.GetTimestamp();
                price = price >= 71_000 ? 70_000 : price + 10;
                _hub.OnTick(Symbol, price);
                if (_mask.Blocked) Interlocked.Increment(ref _blockedSeen);
                long dt = Stopwatch.GetTimestamp() - t0;
                Interlocked.Increment(ref _ticks);
                if (dt > _budget) Interlocked.Increment(ref _overBudget);
                if (dt > max) { max = dt; Interlocked.Exchange(ref _maxTicks, max); }
                Thread.SpinWait(64);
            }
            Interlocked.Exchange(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - allocBase);
        }

        public void Dispose() => Stop();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ③ 무장 불변식 프로브(실 안전 코어 · 호스트와 같은 설정 경로)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 실 안전 코어를 '최악의 출발점' 으로 조립한다: 29 소스가 전부 건강하게 결속 + 봉인 → 집계어 0(차단 비트 없음).
    /// 이 상태에서도 liveArmed 를 막는 것은 물리 이중 키(Dispatch.ValveWired ∧ Dispatch.LiveArmingWired)와 점화락·LiveArmingWired
    /// 술어뿐이다 — UI 조작이 그 키들을 설정 저장소에 써넣을 수 있다면 Check() 의 카나리 재무장 시도가 성공해 위반으로 잡힌다.
    /// </summary>
    public sealed class UiArmingInvariantProbe
    {
        /// <summary>집계어 비트 수 = 소스 29(0..28 · v12.17.6 OrderPathLatency) + StartupSanity 1.</summary>
        public const int InterlockBitCount = 30;

        /// <summary>UI 경로로 바뀌면 안 되는 무장/한도 설정 키(호스트 RuntimeConfig 가 판독하는 실 키).</summary>
        public static readonly string[] SafetyKeys =
        {
            "Dispatch.ValveWired", "Dispatch.LiveArmingWired", "Broker.LiveArmed", "Canary.CapBps", "Canary.CapitalKrw",
        };

        private sealed class HealthySource : IArmingBlockSource { public bool ArmingBlocked => false; }

        private readonly Func<string, string?> _raw;
        private readonly RuntimeConfig _cfg;
        private readonly IgnitionLock _ignition = new();
        private readonly OrderAttestationService _attestation = new();
        private readonly LiveArmingGate _gate;
        private readonly string?[] _baseSafety;
        private readonly long _baseMask, _baseTransitions;
        private long _checks, _violations;
        private string? _firstViolation;

        public ArmingInterlockBitmask Mask { get; }
        public InterlockForensicRecorder Recorder { get; }
        public ZeroAllocForensicAuditStream AuditStream { get; }
        public CanaryHealthSentinel Canary { get; } = new();

        /// <param name="raw">설정 원시 판독(호스트와 동일 precedence 를 호출자가 결정 — CLI/테스트: 환경변수 → 임시 DB).</param>
        public UiArmingInvariantProbe(Func<string, string?> raw)
        {
            _raw = raw ?? throw new ArgumentNullException(nameof(raw));
            _cfg = new RuntimeConfig(raw);

            var sources = new List<ArmingInterlockSource>();
            foreach (ArmingInterlockSource s in Enum.GetValues<ArmingInterlockSource>())
                if (s != ArmingInterlockSource.StartupSanity) sources.Add(s);
            Mask = new ArmingInterlockBitmask(sources.ToArray());
            AuditStream = new ZeroAllocForensicAuditStream(1024);
            Recorder = new InterlockForensicRecorder(AuditStream);
            Mask.AttachTransitionSink(Recorder);                 // 이후 모든 비트 전이 = 해시 체인 포렌식 각인
            var healthy = new HealthySource();
            foreach (var s in sources) Mask.Bind(s, healthy);
            if (!Mask.Seal(out long missing)) throw new InvalidOperationException("프로브 집계어 봉인 실패 missing=0x" + missing.ToString("X"));

            // 호스트 배선 미러: brokerArmed = BrokerHub.IsArmed(db) = 설정 "Broker.LiveArmed" == "1"
            _gate = new LiveArmingGate(_ignition, _attestation, () => _raw("Broker.LiveArmed") == "1");

            _baseSafety = new string?[SafetyKeys.Length];
            for (int i = 0; i < SafetyKeys.Length; i++) _baseSafety[i] = raw(SafetyKeys[i]);
            _baseMask = Mask.Mask;
            _baseTransitions = Mask.Transitions;
        }

        public int BitCount => System.Numerics.BitOperations.PopCount((ulong)Mask.RequiredMask) + 1;
        public long Checks => Interlocked.Read(ref _checks);
        public long Violations => Interlocked.Read(ref _violations);
        public string? FirstViolation => Volatile.Read(ref _firstViolation);
        public IgnitionStage IgnitionStage => _ignition.Stage;

        /// <summary>호스트 디스패처가 쓰는 바로 그 술어 선택(DispatchArming) + 카나리 무장 상태.</summary>
        public bool LiveArmed
            => DispatchArming.ResolvePredicate(_cfg.GetBool("Dispatch.LiveArmingWired", false), _gate)() || Canary.IsArmed;

        /// <summary>
        /// 1회전 판정. 호스트 재무장 경로 재현(설정의 이중 키로 카나리 Arm 시도) 후 불변식 전수 검사. 위반 시 false(+최초 사유 보존).
        /// </summary>
        public bool Check(string context)
        {
            Interlocked.Increment(ref _checks);
            Canary.Arm(1, _cfg.GetBool("Dispatch.ValveWired", false), _cfg.GetBool("Dispatch.LiveArmingWired", false));

            string? why = null;
            if (Canary.IsArmed) why = "카나리 무장 성립(설정 경유 이중 키 충족)";
            else if (LiveArmed) why = "디스패처 무장 술어 true";
            else if (_ignition.Stage != IgnitionStage.Locked) why = "점화락 Locked 이탈";
            else if (Mask.Mask != _baseMask) why = "인터락 집계어 변조 0x" + Mask.Mask.ToString("X");
            else if (Mask.Transitions != _baseTransitions) why = "인터락 비트 전이 발생";
            else
            {
                for (int i = 0; i < SafetyKeys.Length; i++)
                    if (!string.Equals(_raw(SafetyKeys[i]), _baseSafety[i], StringComparison.Ordinal)) { why = "안전 설정 변조 " + SafetyKeys[i]; break; }
            }
            if (why is null) return true;
            Interlocked.Increment(ref _violations);
            Interlocked.CompareExchange(ref _firstViolation, why + " @ " + context, null);
            Canary.Disarm();
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ④ 클릭 표면 추상 + 퍼저 루프
    // ════════════════════════════════════════════════════════════════════════

    public enum UiControlKind : byte { Click = 0, Checkbox = 1, NavLink = 2 }

    /// <summary>클릭 주입 표면(헤드리스 렌더러 · bUnit 등). 인덱스는 직전 Refresh 기준.</summary>
    public interface IUiClickSurface
    {
        /// <summary>현재 렌더 트리에서 조작 가능 요소 재탐색 → 개수.</summary>
        int Refresh();
        UiControlKind KindOf(int index);
        /// <summary>요소 1회 조작(onclick 디스패치 · checkbox 반전 · 앵커 내비게이션). 동기 완료.</summary>
        void Activate(int index);
        /// <summary>상호작용 루트 전체 지문(변동성 루트 제외 · δ=0 해싱).</summary>
        ulong Fingerprint();
        /// <summary>요소가 속한 루트 컴포넌트만의 지문(토글 대칭 판정용).</summary>
        ulong RootFingerprint(int index);
        /// <summary>렌더러가 흡수한 컴포넌트 예외 누계.</summary>
        long Faults { get; }
        /// <summary>보고용 요소 설명(콜드).</summary>
        string Describe(int index);
    }

    public sealed record UiFuzzReport(
        int ControlsDiscovered, long Executed, long ThrottleWaits, long Clicks, long Checkboxes, long NavLinks,
        long UiFaults, long ProbeChecks, long ArmingViolations, string? FirstViolation,
        long HostileWrites, long HostileAdmitted, long HostileRejected, int DistinctStates, long StateSaturated,
        long SymmetryChecks, long SymmetryBreaks, bool LiveArmedFinal, long InterlockMaskFinal, int InterlockBits,
        long ForensicRaised, long ForensicCleared, long ForensicFaults)
    {
        public bool Passed => UiFaults == 0 && ArmingViolations == 0 && !LiveArmedFinal && SymmetryBreaks == 0
                              && HostileAdmitted == 0 && InterlockBits == UiArmingInvariantProbe.InterlockBitCount;
        // StateSaturated 는 진단(판정 제외): 설정 화면 'PIN 생성' 처럼 클릭마다 새 난수 값을 그리는 정당한 무한 상태 공간이 있다(실측).
        //   상태 꼬임 판정의 권위는 토글 대칭(SymmetryBreaks) — 고유 상태 수는 회귀 비교용 관측치로만 보고.

        public void AppendTo(StringBuilder sb)
        {
            sb.AppendLine("[ui-fuzz] controls=" + ControlsDiscovered + " executed=" + Executed + " (click=" + Clicks + " checkbox=" + Checkboxes + " nav=" + NavLinks + ") throttleWaits=" + ThrottleWaits);
            sb.AppendLine("[ui-fuzz] interlockBits=" + InterlockBits + " maskFinal=0x" + InterlockMaskFinal.ToString("X") + " liveArmedFinal=" + LiveArmedFinal + " probeChecks=" + ProbeChecks + " violations=" + ArmingViolations);
            if (FirstViolation is not null) sb.AppendLine("[ui-fuzz] FIRST VIOLATION: " + FirstViolation);
            sb.AppendLine("[ui-fuzz] hostileWrites=" + HostileWrites + " admitted=" + HostileAdmitted + " guardRejected=" + HostileRejected + " uiFaults=" + UiFaults);
            sb.AppendLine("[ui-fuzz] distinctStates=" + DistinctStates + " saturated=" + StateSaturated + " symmetryChecks=" + SymmetryChecks + " symmetryBreaks=" + SymmetryBreaks);
            sb.AppendLine("[ui-fuzz] forensic raised=" + ForensicRaised + " cleared=" + ForensicCleared + " faults=" + ForensicFaults);
            sb.AppendLine("[ui-fuzz] verdict=" + (Passed ? "PASS" : "FAIL"));
        }
    }

    public sealed record UiStormReport(long Attempted, long Admitted, long Dropped, double ElapsedMs, long CoreTicks, long CoreOverBudget,
                                       double CoreMaxMicros, long CoreAllocatedBytes, long UiFaults, long ArmingViolations, double AllowedOverBudgetRatio)
    {
        public double AttemptsPerSecond => ElapsedMs <= 0 ? 0 : Attempted * 1000.0 / ElapsedMs;
        public double OverBudgetRatio => CoreTicks == 0 ? 1 : (double)CoreOverBudget / CoreTicks;
        public bool Passed => CoreTicks > 0 && OverBudgetRatio <= AllowedOverBudgetRatio && CoreAllocatedBytes == 0 && UiFaults == 0 && ArmingViolations == 0;

        public void AppendTo(StringBuilder sb)
        {
            sb.AppendLine("[ui-storm] attempted=" + Attempted + " (" + AttemptsPerSecond.ToString("F0") + "/s) admitted=" + Admitted + " dropped=" + Dropped + " elapsedMs=" + ElapsedMs.ToString("F1"));
            sb.AppendLine("[ui-storm] coreTicks=" + CoreTicks + " overBudget(>1ms)=" + CoreOverBudget + " ratio=" + OverBudgetRatio.ToString("E2") + " (허용 ≤ " + AllowedOverBudgetRatio.ToString("E2") + ") maxUs=" + CoreMaxMicros.ToString("F1") + " coreAlloc=" + CoreAllocatedBytes + "B");
            sb.AppendLine("[ui-storm] uiFaults=" + UiFaults + " armingViolations=" + ArmingViolations + " verdict=" + (Passed ? "PASS" : "FAIL"));
        }
    }

    /// <summary>자율 UI 스위치 퍼저(단일 스레드 · 시드 결정론 — 같은 시드 = 같은 조작 순서).</summary>
    public static class AutonomousUiSwitchFuzzer
    {
        /// <summary>적대 기입 대상 = 무장/한도 키 + 위장 변형(대소문자 · 공백 · 허용 접두사 사칭).</summary>
        public static readonly string[] HostileKeys =
        {
            "Dispatch.ValveWired", "Dispatch.LiveArmingWired", "Broker.LiveArmed", "Canary.CapBps", "Canary.CapitalKrw",
            "dispatch.valvewired", " Dispatch.LiveArmingWired", "Lan.", "Email", "Risk.MaxDailyLossPct", "Security.AntiDebug", "",
        };

        /// <param name="hostileWrite">UI 가 쥔 설정 포트로의 적대 기입(compromised 컴포넌트 모사). null = 생략.</param>
        /// <param name="hostileEvery">적대 기입 주기(상호작용 수 · 0 = 생략).</param>
        /// <param name="readBack">적대 기입 직후 저장소 재판독(값이 바뀌면 HostileAdmitted — 판정 권위). null = 프로브 안전 키 판정만.</param>
        public static UiFuzzReport Run(IUiClickSurface surface, UiArmingInvariantProbe probe, SwitchStormIsolationGuard guard,
                                       int interactions, int seed, Action<string, string>? hostileWrite = null, int hostileEvery = 50,
                                       Func<string, string?>? readBack = null, UiStateMatrix? matrix = null)
        {
            ArgumentNullException.ThrowIfNull(surface);
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentNullException.ThrowIfNull(guard);
            matrix ??= new UiStateMatrix(4096);
            var rng = new Random(seed);
            long executed = 0, waits = 0, clicks = 0, checks = 0, navs = 0, hostile = 0, hostileAdmitted = 0, symChecks = 0, symBreaks = 0;
            long faults0 = surface.Faults;
            long rejected0 = UiSettingWriteGuard.Rejected;
            int discovered = surface.Refresh();
            matrix.Observe(surface.Fingerprint());
            probe.Check("baseline");

            for (int step = 0; step < interactions; step++)
            {
                int n = surface.Refresh();
                if (n == 0) break;
                if (n > discovered) discovered = n;
                // 1차: 순차 스윕(모든 요소 최소 1회) → 이후 시드 무작위
                int idx = step < n ? step : rng.Next(n);

                while (!guard.TryAdmit()) { waits++; Thread.Yield(); }   // 퍼저 = 사용자 — 스로틀 창을 기다린다(스톰 측정은 RunStorm)

                var kind = surface.KindOf(idx);
                if (kind == UiControlKind.Checkbox && rng.Next(4) == 0)
                {
                    // [방향 A] 토글 대칭: 2회 반전 → 해당 루트 지문 원상 복귀해야 함
                    ulong before = surface.RootFingerprint(idx);
                    surface.Activate(idx);
                    surface.Refresh();
                    surface.Activate(idx);
                    surface.Refresh();
                    symChecks++;
                    if (surface.RootFingerprint(idx) != before) symBreaks++;
                    checks += 2; executed += 2;
                }
                else
                {
                    surface.Activate(idx);
                    executed++;
                    if (kind == UiControlKind.Click) clicks++; else if (kind == UiControlKind.Checkbox) checks++; else navs++;
                }

                surface.Refresh();
                matrix.Observe(surface.Fingerprint());
                probe.Check(kind + "#" + idx + " step " + step);

                if (hostileWrite is not null && hostileEvery > 0 && step % hostileEvery == hostileEvery - 1)
                {
                    string key = HostileKeys[rng.Next(HostileKeys.Length)];
                    hostile++;
                    string? before = readBack?.Invoke(key);
                    try { hostileWrite(key, rng.Next(2) == 0 ? "1" : "true"); }
                    catch (Exception) { /* 포트가 던져도 무장 실패 방향 — 기입 여부는 재판독이 판정 */ }
                    if (readBack is not null && !string.Equals(before, readBack(key), StringComparison.Ordinal)) hostileAdmitted++;
                    probe.Check("hostile " + key);
                }
            }

            return new UiFuzzReport(discovered, executed, waits, clicks, checks, navs,
                surface.Faults - faults0, probe.Checks, probe.Violations, probe.FirstViolation,
                hostile, hostileAdmitted, UiSettingWriteGuard.Rejected - rejected0, matrix.Distinct, matrix.Saturated,
                symChecks, symBreaks, probe.LiveArmed, probe.Mask.Mask, probe.BitCount,
                probe.Recorder.RaisedEvents, probe.Recorder.ClearedEvents, probe.Recorder.Faults);
        }

        /// <summary>
        /// [방향 B] 스위치 스톰: durationMs 동안 무대기로 checkbox/onclick 을 난사(가드 통과분만 실행 · 초과 폐기) — 동시에
        /// CoreTickIsolationMonitor 가 코어 틱 처리 시간을 잰다. 매 실행 후 무장 프로브 판정.
        /// </summary>
        public static UiStormReport RunStorm(IUiClickSurface surface, UiArmingInvariantProbe probe, SwitchStormIsolationGuard guard,
                                             CoreTickIsolationMonitor core, int durationMs, int seed, double allowedOverBudgetRatio = 1e-3)
        {
            var rng = new Random(seed);
            long attempted = 0, faults0 = surface.Faults, viol0 = probe.Violations, admitted0 = guard.Admitted, dropped0 = guard.Dropped;
            core.Start();
            long t0 = Stopwatch.GetTimestamp(), end = t0 + Stopwatch.Frequency * durationMs / 1000;
            while (Stopwatch.GetTimestamp() < end)
            {
                attempted++;
                if (!guard.TryAdmit()) continue;   // 폐기(재시도 없음)
                int n = surface.Refresh();
                if (n == 0) break;
                int idx = rng.Next(n);
                if (surface.KindOf(idx) == UiControlKind.NavLink) idx = (idx + 1) % n;   // 스톰은 스위치 위주
                surface.Activate(idx);
                probe.Check("storm");
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            core.Stop();
            return new UiStormReport(attempted, guard.Admitted - admitted0, guard.Dropped - dropped0, ms, core.Ticks, core.OverBudget,
                core.MaxMicros, core.AllocatedBytes, surface.Faults - faults0, probe.Violations - viol0, allowedOverBudgetRatio);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ⑤ 헤드리스 렌더러 표면(CLI 용 — bUnit 없이 실 Blazor Renderer 로 RCL 컴포넌트 렌더 · 이벤트 디스패치)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>헤드리스 NavigationManager — 내비게이션 = URI 갱신 + LocationChanged 통지(NavLink 활성 클래스 재평가).</summary>
    public sealed class HeadlessNavigationManager : NavigationManager
    {
        public HeadlessNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        public long Navigations { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            Navigations++;
            NotifyLocationChanged(false);
        }
    }

    /// <summary>
    /// 실 Blazor Renderer 위 헤드리스 표면. 루트 컴포넌트 여럿을 한 디스패처에서 렌더하고, 렌더 트리 프레임을 직접 읽어
    /// onclick 요소 · checkbox(onchange) · 앵커(href)를 찾는다. 모든 트리 판독/디스패치는 렌더러 디스패처 안에서 수행
    /// (대시보드 타이머 재렌더와 경합 0).
    /// </summary>
    public sealed class HeadlessUiSurface : Renderer, IUiClickSurface
    {
        private struct Control
        {
            public ulong HandlerId;
            public UiControlKind Kind;
            public bool Checked;
            public int Root;
            public string? Href;
            public string? Tag;
        }

        private const int MaxControls = 1024;
        private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
        private readonly HeadlessNavigationManager _nav;
        private readonly List<(int Id, bool Volatile)> _roots = new();
        private readonly Control[] _controls = new Control[MaxControls];
        private int _count;
        private long _faults;
        private string? _lastFault;

        public HeadlessUiSurface(IServiceProvider services, HeadlessNavigationManager nav) : base(services, NullLoggerFactory.Instance)
            => _nav = nav ?? throw new ArgumentNullException(nameof(nav));

        public override Dispatcher Dispatcher => _dispatcher;
        public long Faults => Interlocked.Read(ref _faults);
        public string? LastFault => Volatile.Read(ref _lastFault);
        public HeadlessNavigationManager Navigation => _nav;

        /// <summary>루트 컴포넌트 렌더(콜드). volatileRoot = 타이머로 스스로 갱신되는 화면(전역 지문에서 제외 · 렌더/스톰 대상엔 포함).</summary>
        public void Mount<TComponent>(bool volatileRoot = false) where TComponent : IComponent
        {
            _dispatcher.InvokeAsync(() =>
            {
                var component = InstantiateComponent(typeof(TComponent));
                int id = AssignRootComponentId(component);
                _roots.Add((id, volatileRoot));
                return RenderRootComponentAsync(id);
            }).GetAwaiter().GetResult();
        }

        protected override void HandleException(Exception exception)
        {
            Interlocked.Increment(ref _faults);
            Interlocked.CompareExchange(ref _lastFault, exception.GetType().Name + ": " + exception.Message, null);
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public int Refresh()
        {
            _dispatcher.InvokeAsync(() =>
            {
                _count = 0;
                for (int r = 0; r < _roots.Count; r++) Scan(_roots[r].Id, r);
            }).GetAwaiter().GetResult();
            return _count;
        }

        private void Scan(int componentId, int root)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (int i = 0; i < frames.Count; i++)
            {
                ref var f = ref frames.Array[i];
                if (f.FrameType == RenderTreeFrameType.Component) { Scan(f.ComponentId, root); continue; }
                if (f.FrameType != RenderTreeFrameType.Element) continue;

                ulong click = 0, change = 0;
                bool checkbox = false, isChecked = false;
                string? href = null;
                for (int a = i + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
                {
                    ref var at = ref frames.Array[a];
                    switch (at.AttributeName)
                    {
                        case "onclick": click = at.AttributeEventHandlerId; break;
                        case "onchange": change = at.AttributeEventHandlerId; break;
                        case "type": checkbox = at.AttributeValue is string t && string.Equals(t, "checkbox", StringComparison.OrdinalIgnoreCase); break;
                        case "checked": isChecked = at.AttributeValue is bool b ? b : at.AttributeValue is not null; break;
                        case "href": href = at.AttributeValue as string; break;
                    }
                }
                if (_count >= MaxControls) continue;
                ref var c = ref _controls[_count];
                if (checkbox && change != 0) { c = new Control { HandlerId = change, Kind = UiControlKind.Checkbox, Checked = isChecked, Root = root, Tag = f.ElementName }; _count++; }
                else if (click != 0) { c = new Control { HandlerId = click, Kind = UiControlKind.Click, Root = root, Tag = f.ElementName }; _count++; }
                else if (href is not null && string.Equals(f.ElementName, "a", StringComparison.Ordinal)) { c = new Control { Kind = UiControlKind.NavLink, Href = href, Root = root, Tag = "a" }; _count++; }
            }
        }

        public UiControlKind KindOf(int index) => _controls[index].Kind;

        public string Describe(int index)
        {
            var c = _controls[index];
            return c.Kind + "<" + c.Tag + ">" + (c.Href is null ? "" : " href=" + c.Href) + " root=" + c.Root;
        }

        public void Activate(int index)
        {
            var c = _controls[index];
            try
            {
                switch (c.Kind)
                {
                    case UiControlKind.Click:
                        _dispatcher.InvokeAsync(() => DispatchEventAsync(c.HandlerId, null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs())).GetAwaiter().GetResult();
                        break;
                    case UiControlKind.Checkbox:
                        _dispatcher.InvokeAsync(() => DispatchEventAsync(c.HandlerId, null, new ChangeEventArgs { Value = !c.Checked })).GetAwaiter().GetResult();
                        break;
                    default:
                        _dispatcher.InvokeAsync(() => _nav.NavigateTo(c.Href!)).GetAwaiter().GetResult();
                        break;
                }
            }
            catch (Exception ex) { HandleException(ex); }
        }

        public ulong Fingerprint()
        {
            ulong v = 0;
            _dispatcher.InvokeAsync(() =>
            {
                var h = Fnv64.Create();
                for (int r = 0; r < _roots.Count; r++)
                    if (!_roots[r].Volatile) { HashTree(ref h, _roots[r].Id); h.Separator(); }
                v = h.Value;
            }).GetAwaiter().GetResult();
            return UiStateFingerprint.Remap(v);
        }

        public ulong RootFingerprint(int index)
        {
            int root = _controls[index].Root;
            ulong v = 0;
            _dispatcher.InvokeAsync(() => { var h = Fnv64.Create(); HashTree(ref h, _roots[root].Id); v = h.Value; }).GetAwaiter().GetResult();
            return UiStateFingerprint.Remap(v);
        }

        /// <summary>프레임 트리 해싱(δ=0) — 요소명 · 문자열/불리언 속성 · 텍스트 · 마크업. 이벤트 핸들러 id(렌더마다 변동)는 이름만.</summary>
        private void HashTree(ref Fnv64 h, int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (int i = 0; i < frames.Count; i++)
            {
                ref var f = ref frames.Array[i];
                switch (f.FrameType)
                {
                    case RenderTreeFrameType.Element: h.Add('<'); h.Add(f.ElementName); break;
                    case RenderTreeFrameType.Text: h.Add(f.TextContent); break;
                    case RenderTreeFrameType.Markup: h.Add(f.MarkupContent); break;
                    case RenderTreeFrameType.Attribute:
                        h.Add(' '); h.Add(f.AttributeName);
                        if (f.AttributeValue is string s) { h.Add('='); h.Add(s); }
                        else if (f.AttributeValue is bool b) h.Add(b ? '1' : '0');
                        break;
                    case RenderTreeFrameType.Component: HashTree(ref h, f.ComponentId); break;
                }
                h.Separator();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ⑥ 하네스 포트(호스트 어댑터 위임 + 부작용 격리)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 설정 포트 하네스 래퍼 — 설정 판독/기입 · 거래 모드는 실 어댑터(→ UiSettingWriteGuard → 임시 DB)에 그대로 위임하고,
    /// 소켓을 여는 RestartLan 만 계수 후 생략(LAN 서버 기동 0). LAN 상태 판독도 정적 서버를 건드리지 않는 고정값.
    /// </summary>
    public sealed class HarnessSettingsPort : IAdminSettingWidgetPort
    {
        private readonly IAdminSettingWidgetPort _inner;
        private long _lanRestarts;

        public HarnessSettingsPort(IAdminSettingWidgetPort inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public long LanRestarts => Interlocked.Read(ref _lanRestarts);
        public string? GetSetting(string key) => _inner.GetSetting(key);
        public void SetSetting(string key, string value) => _inner.SetSetting(key, value);
        public void SetSecret(string key, string value) => _inner.SetSecret(key, value);
        public int TightenSetting(string key, string value) => _inner.TightenSetting(key, value);
        public bool TryGetTightenable(string key, out long effective) => _inner.TryGetTightenable(key, out effective);
        public bool IsLive => _inner.IsLive;
        public void SetLive(bool live) => _inner.SetLive(live);
        public string UpbitAccessKeyMasked => _inner.UpbitAccessKeyMasked;
        public string KisAccessKeyMasked => _inner.KisAccessKeyMasked;
        public bool LanRunning => false;
        public int LanPort => 0;
        public string LanLastError => "";
        public void RestartLan() => Interlocked.Increment(ref _lanRestarts);
        public string ActiveLlm => "harness";
    }

    /// <summary>셸 내비게이션 포트 하네스(읽기 고정값 · 팔레트 요청 계수).</summary>
    public sealed class HarnessShellPort : MS2E.UI.Portable.Ports.Shell.IMainShellNavigationPort
    {
        private long _paletteRequests;
        public long PaletteRequests => Interlocked.Read(ref _paletteRequests);
        public int AlertUnreadCount => 0;
        public int FraudUnreadCount => 0;
        public string AppVersion => "ui-fuzz";
        public bool IsAuthenticated => true;
        public string? GetSetting(string key) => null;
        public void RequestOpenPalette() => Interlocked.Increment(ref _paletteRequests);
    }
}

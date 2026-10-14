// ============================================================================
// UiSwitchFuzzingConcurrencyTests — [v12.18.8-DEV] 활성 텔레메트리(펌프 지터 · 브로커 세션 8상태 전이) 하의 자율 UI 스위치 퍼징
//
//  ★전제(정직): bUnit 퍼저(AutonomousUiSwitchFuzzer + BunitClickSurface) · 10k CLI 퍼징 · 스톰 격리는 v12.17.4-GM 부터 존재
//   (AutonomousUiVerificationTests). 그 리그의 허브엔 지터 센티널 · 세션 관리자가 결선되지 않아 대시보드의 두 패널은 '미결선'으로만 렌더됐다.
//   이 시험은 같은 퍼저를 '살아 있는' 패널 위에서 돌린다 — 허브 50ms 타이머(펌프 지터 기록) + 전용 스레드가 키움/KIS 관리자를
//   무작위 전이(연결 · 단절/백오프 · 허가/재접속 · 대사 · 확정 · 소진 · 불일치 · 이중키 재설정)로 몰아붙이는 동안 10,000 상호작용.
//  §1 퍼징 — UI 예외 0 · 불법 무장 0 · 적대 기입 수용 0 · 토글 대칭 파손 0 · 집계어 30비트 무오염 · 매 50회 게시본 일관성(찢김 0) ·
//     대시보드 마크업에 두 패널 실값(세션 라벨 · 지터 라벨 ≠ 미결선) · 전이 스레드 예외 0 · 펌프 고장 0.
//  §2 누수 — bUnit 컨텍스트 폐기 후 허브 FrameReady 구독자 0 · 퍼징 전후(전체 GC 후) 관리 힙 증가 상한 · 2번째 절반 증가 ≤ 1번째 절반 + 여유.
//  §3 라벨 캐시 — 세션 8 · 지터 4 상태 라벨/클래스 = 같은 참조(정적 배열 · 히트 100%) · 범위 밖 = Unknown/off · 조회 0B · 결선 펌프 0B.
//  ★정직(δ=0 범위): Blazor 렌더(diff · 마크업 · bUnit DOM 파싱)는 프레임워크가 할당한다 — 0B 보증 = 라벨 조회 · 허브 펌프 · 세션 사본.
//  ★liveArmed:false — 리그는 IBroker · 소켓 · onSend 참조 0. 세션 관리자는 격리 델리게이트 없음(관측만) · 실 브로커 송신 경로 없음.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Simulation;
using StockManager.Telemetry;
using StockManager.Tests.Simulation;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Blazor
{
    [Collection("ConcurrencyStress")]
    public sealed class UiSwitchFuzzingConcurrencyTests
    {
        private const int Interactions = 10_000;
        private const int MaxFailures = 3;
        private readonly ITestOutputHelper _out;
        public UiSwitchFuzzingConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static long Settled()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return GC.GetTotalMemory(true);
        }

        /// <summary>세션 관리자 무작위 전이 구동기(전용 스레드 · 수동 시계). 격리 델리게이트 없음 — 관측 대상만.</summary>
        private sealed class SessionChurn : IDisposable
        {
            private readonly SessionRecoveryManager[] _m;
            private readonly long[] _now;
            private readonly bool[] _confirmed;
            private readonly Thread _thread;
            private readonly System.Threading.Tasks.ValueTask<bool>?[] _wait = new System.Threading.Tasks.ValueTask<bool>?[2];
            private int _stop;
            public long Steps, Faults;
            public string? FirstFault;
            public readonly long[] StatesSeen = new long[8];

            public SessionChurn(int seed)
            {
                _now = new long[2];
                _confirmed = new bool[2];
                _m = new SessionRecoveryManager[2];
                for (int b = 0; b < 2; b++)
                {
                    int i = b;
                    _now[i] = 1_000 * TimeSpan.TicksPerSecond;
                    _m[i] = new SessionRecoveryManager(i, isolate: null, requestReconcile: () => Volatile.Write(ref _confirmed[i], false),
                                                       reconciled: () => Volatile.Read(ref _confirmed[i]), maxFailures: MaxFailures,
                                                       nowTicks: () => Volatile.Read(ref _now[i]));
                }
                _thread = new Thread(() => Loop(seed)) { IsBackground = true, Name = "session-churn" };
            }

            public SessionRecoveryManager this[int b] => _m[b];
            public void Start() => _thread.Start();

            private void Loop(int seed)
            {
                var rng = new Random(seed);
                while (Volatile.Read(ref _stop) == 0)
                {
                    int b = rng.Next(2);
                    var m = _m[b];
                    try
                    {
                        switch (rng.Next(9))
                        {
                            case 0: m.OnConnected(); break;                                           // 최초 = 연결 · 재접속 = 대사 중
                            case 1: m.OnConnectionLost(); break;                                      // 백오프(격리)
                            case 2: Interlocked.Add(ref _now[b], 20 * TimeSpan.TicksPerSecond); m.Pump(); break;   // 기한 도래 → 허가
                            case 3:                                                                   // 허가 대기(대기자 1개 계약 — 미완료 대기는 보관 · 완료 시 1회 소비)
                                if (_wait[b] is { } w) { if (w.IsCompleted) { _ = w.Result; _wait[b] = null; } }
                                else { var vt = m.WaitForAttemptAsync(); if (vt.IsCompleted) _ = vt.Result; else _wait[b] = vt; }
                                break;
                            case 4: m.OnAttemptFailed(); break;                                       // 실패 누적 → 소진
                            case 5: Volatile.Write(ref _confirmed[b], true); m.Pump(); break;         // 대사 확정 → 연결
                            case 6: if (rng.Next(8) == 0) m.OnDiscrepancy(); break;                  // 불일치(sticky · 드물게)
                            case 7: if (rng.Next(4) == 0) m.TryReset(true, true); break;             // 이중키 재설정(관리자 래치만 — 무장 무관)
                            default: m.OnReconciliationCompleted(); break;
                        }
                        var s = m.Snapshot();
                        int st = DashSession.Classify(true, (int)s.State, s.EverConnected, s.Exhausted, s.Discrepancy);
                        Interlocked.Increment(ref StatesSeen[st]);
                        Interlocked.Increment(ref Steps);
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.Increment(ref Faults) == 1) FirstFault = ex.GetType().Name + ": " + ex.Message;
                    }
                    if ((Steps & 255) == 0) Thread.Sleep(1);                              // 코어 독점 방지(전체 스위트 · 공유 호스트) — 그래도 초당 수십만 전이
                }
            }

            public void Dispose()
            {
                Volatile.Write(ref _stop, 1);
                if (_thread.IsAlive) Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "전이 스레드 10초 무응답");
                foreach (var m in _m) m.Shutdown();
            }
        }

        /// <summary>
        /// 표면 폐기 후 허브 구독자 수. ★렌더러 폐기는 진행 중 렌더(라이브 프레임 InvokeAsync)가 끝난 뒤 컴포넌트 Dispose 를 마친다 —
        /// 실측 ≤ 수십 ms 지연 해지(누수 아님). 2초 안에 0 이 안 되면 그 값 = 누수.
        /// </summary>
        private int SubscribersAfterDispose(DashboardSnapshotHub hub, string tag)
        {
            var sw = Stopwatch.StartNew();
            int first = hub.FrameReadySubscribers, left = first;
            while (left != 0 && sw.ElapsedMilliseconds < 2_000) { Thread.Sleep(5); left = hub.FrameReadySubscribers; }
            if (first != 0) _out.WriteLine($"[{tag}] 폐기 직후 구독 {first} → {left} ({sw.ElapsedMilliseconds}ms 지연 해지)");
            return left;
        }

        /// <summary>게시본 일관성(리스 → 값 판독 → 반납). 위반 = 사유 문자열.</summary>
        private static string? FrameDefect(DashboardSnapshotHub hub)
        {
            var f = hub.Acquire();
            if (f is null) return null;                                                   // 리스 경합(16회) = 판정 생략
            try
            {
                if (f.SessionCount != 2) return "SessionCount " + f.SessionCount;
                for (int i = 0; i < 2; i++)
                {
                    var s = f.Sessions[i];
                    if (s.Broker != i) return "Broker 순서 " + s.Broker + "@" + i;
                    if ((uint)s.State > DashSession.Discrepancy || s.State == DashSession.Unbound) return "State " + s.State;
                    if (s.MaxFailures != MaxFailures) return "MaxFailures " + s.MaxFailures;
                    if (s.Failures < 0 || s.Failures > 30) return "Failures " + s.Failures;
                    if (s.Losses < 0 || s.Recoveries < 0 || s.Recoveries > s.Losses + 1) return "계수 " + s.Losses + "/" + s.Recoveries;
                }
                if ((uint)f.JitterState > DashJitter.GcSpike) return "JitterState " + f.JitterState;
                if (f.Mask != 0) return "Mask 0x" + f.Mask.ToString("X");
                return null;
            }
            finally { hub.Release(f); }
        }

        // ════════════ §1 · §2 퍼징 + 누수 ════════════

        [Fact]
        public void Fuzz10k_Bunit_LiveJitterAndSessionChurn_NoFaults_NoArming_NoTornFrame_NoSubscriberLeak() => Fuzz(headless: false);

        [Fact]
        public void Fuzz10k_HeadlessRenderer_LiveJitterAndSessionChurn_NoFaults_HeapFlat() => Fuzz(headless: true);

        private void Fuzz(bool headless)
        {
            var jitter = new MicrosecondJitterSentinel();
            var rig = new UiFuzzRig();
            rig.Hub.AttachPumpJitter(jitter);                                             // 허브 50ms 타이머가 이미 가동 중 — 다음 회차부터 기록
            var churn = new SessionChurn(seed: 1018);
            rig.Hub.AttachSessionRecovery(churn[0]);
            rig.Hub.AttachSessionRecovery(churn[1]);
            IUiClickSurface surface = headless ? rig.CreateHeadlessSurface() : BunitClickSurface.CreateStandard(rig);
            string tag = headless ? "headless" : "bUnit";
            int subsMounted = rig.Hub.FrameReadySubscribers, remounts = 0;
            long staleTotal = 0, settleTotal = 0, quiesceTotal = 0;
            Assert.True(subsMounted >= 1, "대시보드 뷰 구독 0");
            churn.Start();

            var sw = Stopwatch.StartNew();
            while (jitter.Samples < 2 && sw.ElapsedMilliseconds < 5_000) Thread.Sleep(10);
            Assert.True(jitter.Samples >= 2, "펌프 지터 표본 없음");

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var guard = SwitchStormIsolationGuard.PerMilliseconds(10_000, 100);
            // 워밍업 1,000 → 기준선 → 4,500 × 2(절반별 힙 증가 비교) = 10,000
            var warm = AutonomousUiSwitchFuzzer.Run(surface, rig.Probe, guard, 1_000, seed: 11, rig.Settings.SetSetting, hostileEvery: 25, readBack: rig.Db.GetSetting);
            long heap0 = Settled();
            long torn = 0, checks = 0;
            string? firstTorn = null;
            var reports = new List<UiFuzzReport> { warm };
            long fuzzStart = Stopwatch.GetTimestamp();
            long[] heaps = new long[2];
            for (int half = 0; half < 2; half++)
            {
                for (int chunk = 0; chunk < 90; chunk++)                                   // 50 상호작용마다 게시본 일관성 판독
                {
                    reports.Add(AutonomousUiSwitchFuzzer.Run(surface, rig.Probe, guard, 50, seed: 1_000 + half * 100 + chunk,
                                                            rig.Settings.SetSetting, hostileEvery: 25, readBack: rig.Db.GetSetting));
                    checks++;
                    if (FrameDefect(rig.Hub) is { } d) { torn++; firstTorn ??= d; }
                    if (!headless && chunk % 40 == 39)
                    {
                        // bUnit 컨텍스트 재탑재(~2,000 상호작용마다): bUnit/AngleSharp 보유(상호작용당 ~24KB)를 풀어 전체 스위트 시험 호스트 점유를 묶는다.
                        // 매 폐기 = 뷰 Dispose → 허브 구독 해지 재확인(누수 0 반복 판정).
                        staleTotal += surface.StaleTargets; settleTotal += (surface as BunitClickSurface)?.SettleTimeouts ?? 0; quiesceTotal += (surface as BunitClickSurface)?.QuiesceTimeouts ?? 0;
                        (surface as IDisposable)?.Dispose();
                        Assert.Equal(0, SubscribersAfterDispose(rig.Hub, "remount " + remounts));
                        remounts++;
                        surface = BunitClickSurface.CreateStandard(rig);
                    }
                }
                heaps[half] = Settled();
            }
            double fuzzSec = (Stopwatch.GetTimestamp() - fuzzStart) / (double)Stopwatch.Frequency;

            Assert.True(surface.Refresh() > 0, "퍼징 후 조작 요소 0(렌더 트리 소실)");

            long executed = reports.Sum(r => r.Executed);
            churn.Dispose();
            long faults = reports.Sum(r => r.UiFaults), arming = reports.Sum(r => r.ArmingViolations), admitted = reports.Sum(r => r.HostileAdmitted);
            long hostile = reports.Sum(r => r.HostileWrites), sym = reports.Sum(r => r.SymmetryBreaks), symChecks = reports.Sum(r => r.SymmetryChecks), symDirty = reports.Sum(r => r.SymmetryContaminated);
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1, dg2 = GC.CollectionCount(2) - g2;
            var sb = new StringBuilder();
            sb.Append($"[§1 {tag}] 상호작용 {executed:N0} ({executed / fuzzSec:N0}/s · {fuzzSec:F1}s) · UI 예외 {faults} · 불법 무장 {arming} · 적대 {hostile}(수용 {admitted}) · ");
            sb.Append($"대칭 {symChecks}(파손 {sym} · 오염 제외 {symDirty}) · 게시본 판독 {checks}(찢김 {torn}) · 세션 전이 {churn.Steps:N0}(예외 {churn.Faults}) · ");
            staleTotal += surface.StaleTargets; settleTotal += (surface as BunitClickSurface)?.SettleTimeouts ?? 0; quiesceTotal += (surface as BunitClickSurface)?.QuiesceTimeouts ?? 0;
            sb.Append($"재탑재 {remounts} · 노후 표적 {staleTotal} · 체크 반영 대기 초과 {settleTotal} · 안정 대기 초과 {quiesceTotal} · 상태 분포 [{string.Join(",", churn.StatesSeen)}] · 지터 표본 {jitter.Samples} · 펌프 {rig.Hub.Pumps}(고장 {rig.Hub.PumpFaults}) · GC {dg0}/{dg1}/{dg2}");
            _out.WriteLine(sb.ToString());
            _out.WriteLine($"[§2 {tag}] 힙(전체 GC 후) 기준 {heap0 / 1024:N0}KB → 1절반 {heaps[0] / 1024:N0}KB → 2절반 {heaps[1] / 1024:N0}KB");

            Assert.True(executed >= Interactions, "executed " + executed);
            Assert.True(faults == 0, "UI 예외: " + (surface as BunitClickSurface)?.LastFault + (surface as HeadlessUiSurface)?.LastFault);
            Assert.Equal(0, arming);
            Assert.True(hostile > 0);
            Assert.Equal(0, admitted);
            Assert.Equal(0, sym);
            Assert.True(torn == 0, "찢긴 게시본: " + firstTorn);
            Assert.True(churn.Faults == 0, "전이 예외: " + churn.FirstFault);
            Assert.True(churn.Steps > 10_000, "전이 " + churn.Steps);
            foreach (int st in new[] { DashSession.Connected, DashSession.Backoff, DashSession.Reconciling, DashSession.Exhausted, DashSession.Discrepancy })
                Assert.True(churn.StatesSeen[st] > 0, "미도달 상태 " + SignalLabelMapper.SessionStateLabel(st));
            Assert.Equal(0, rig.Hub.PumpFaults);
            Assert.Equal(0, rig.Hub.SubscriberFaults);
            Assert.True(jitter.Samples > 2);
            Assert.False(rig.Probe.LiveArmed);
            Assert.Equal(0, rig.Probe.Mask.Mask);
            Assert.Null(rig.Db.GetSetting("Dispatch.ValveWired"));
            Assert.Null(rig.Db.GetSetting("Dispatch.LiveArmingWired"));

            // 렌더된 대시보드 = 실값(퍼징 동안 살아 있던 패널)
            var f = rig.Hub.Acquire();
            Assert.NotNull(f);
            try
            {
                Assert.NotEqual(DashJitter.Unbound, f!.JitterState);
                Assert.Equal(2, f.SessionCount);
            }
            finally { rig.Hub.Release(f!); }

            // §2 누수: 실 렌더러(헤드리스 — 호스트와 같은 Renderer 파생)만 판정. ★bUnit 표면은 라이브 텔레메트리 유무와 무관하게 상호작용당
            //   ~24KB 를 보유(실측 · 텔레메트리 끈 대조군 동일) = 시험 하네스(bUnit/AngleSharp) 보유 — 제품 누수 판정에서 제외 · 수치만 보고.
            long grow1 = heaps[0] - heap0, grow2 = heaps[1] - heaps[0];
            if (headless)
            {
                Assert.True(heaps[1] - heap0 < 8L * 1024 * 1024, $"힙 증가 {(heaps[1] - heap0) / 1024}KB");
                Assert.True(grow2 <= Math.Max(grow1, 0) + 2L * 1024 * 1024, $"단조 증가 의심: 1절반 {grow1 / 1024}KB · 2절반 {grow2 / 1024}KB");
            }

            (surface as IDisposable)?.Dispose();                                                            // 뷰 Dispose → FrameReady 해지
            Assert.Equal(0, SubscribersAfterDispose(rig.Hub, "final"));                  // ★구독 누수 0(리그 자체 구독 없음 — 전량 뷰)
            rig.Dispose();                                                                // 허브 봉인(진행 회차 대기) → 센티널 폐기 경합 0
            jitter.Dispose();
        }

        // ════════════ §3 라벨 캐시 · δ=0 ════════════

        [Fact]
        public void LabelCache_StaticReferences_FullHit_OutOfRange_ZeroAlloc_PumpWithPanels()
        {
            for (int s = 0; s < SignalLabelMapper.SessionStateCount; s++)
            {
                Assert.Same(SignalLabelMapper.SessionStateLabel(s), SignalLabelMapper.SessionStateLabel(s));   // 정적 배열 = 같은 참조(새 문자열 0)
                Assert.Same(SignalLabelMapper.SessionStateClass(s), SignalLabelMapper.SessionStateClass(s));
                Assert.NotEqual(SignalLabelMapper.Unknown, SignalLabelMapper.SessionStateLabel(s));
            }
            for (int s = 0; s < SignalLabelMapper.JitterStateCount; s++)
            {
                Assert.Same(SignalLabelMapper.JitterStateLabel(s), SignalLabelMapper.JitterStateLabel(s));
                Assert.Same(SignalLabelMapper.JitterStateClass(s), SignalLabelMapper.JitterStateClass(s));
            }
            Assert.Same(SignalLabelMapper.Unknown, SignalLabelMapper.SessionStateLabel(-1));
            Assert.Same(SignalLabelMapper.Unknown, SignalLabelMapper.JitterStateLabel(int.MaxValue));
            Assert.Equal("off", SignalLabelMapper.SessionStateClass(8));
            // µs 텍스트 캐시: 같은 값 반복 = 같은 참조(새 값 1회만 생성)
            string a = SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, 777_000);
            Assert.Same(a, SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, 777_999));      // 같은 µs 버킷

            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    SignalLabelMapper.SessionStateLabel(i & 15); SignalLabelMapper.SessionStateClass(i & 15);
                    SignalLabelMapper.JitterStateLabel(i & 7); SignalLabelMapper.JitterStateClass(i & 7);
                    SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, 777_000);
                }
            }));

            // 두 패널 결선 허브 펌프(유휴 · 전이 재게시) = 0B
            using var jitter = new MicrosecondJitterSentinel();
            using var hub = new DashboardSnapshotHub(nowTicks: () => 1);
            var m0 = new SessionRecoveryManager(0, null, null, () => true, maxFailures: MaxFailures);
            var m1 = new SessionRecoveryManager(1, null, null, () => true, maxFailures: MaxFailures);
            hub.AttachPumpJitter(jitter); hub.AttachSessionRecovery(m0); hub.AttachSessionRecovery(m1);
            m1.OnConnected();
            for (int i = 0; i < 64; i++) { m1.OnConnectionLost(); hub.Pump(); m1.OnConnected(); m1.Pump(); hub.Pump(); }
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) hub.Pump(); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) { m1.OnConnectionLost(); hub.Pump(); m1.OnConnected(); m1.Pump(); hub.Pump(); } }));
            Assert.Equal(0, hub.PumpFaults);
        }

        [Fact]
        public void Ratchet_HeldUnderActiveTelemetry_RclPure()
        {
            var r = PureShellFitnessSentinel.Scan(typeof(CanaryHealthSentinel).Assembly);
            Assert.True(r.WithinRatchet && r.HostCoupledUi <= 40, $"coupled {r.HostCoupledUi}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            // bUnit · 리그 결선은 시험 프로젝트에만 — RCL 은 호스트 형식 미참조
            Assert.DoesNotContain(typeof(MS2E.UI.Portable.Helpers.SignalLabelMapper).Assembly.GetReferencedAssemblies(), n => n.Name == "MS2E.StockSuite");
        }
    }
}

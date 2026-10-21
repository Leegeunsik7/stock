// ============================================================================
// SessionRecoveryManagerTests — [v12.18.7-DEV] 브로커 세션 복구 상태 → DashboardSnapshotHub → /aidash 브로커 세션 배지
//
//  ★전제(정직): 세션 복구 관리자 · 백오프 · 침묵 강제 종료 · 소진 래치 · KIS 미체결 대사(TTTC8001R) · 유령 비상 취소(TTTC0803U)는
//   v12.17.0-GM 에서 이미 출하 · 결선(SessionAutoRecoveryTests · KisOpenOrderReconciliationTests). 이 시험은 그 상태를 관제 배지로 잇는 경로만 다룬다.
//  §1 Snapshot — 락 1회 일관 판독: 상태 · 수립 이력 · 소진/불일치 래치 · 연속 실패/상한 · 누적 계수 = 개별 속성과 일치.
//  §2 분류 — 미결선 · 접속 대기(최초) · 연결 · 단절 = 재접속 대기(백오프) · 허가 = 재접속 중 · 재접속 = 대사 중 · 확정 = 연결 · 소진 격리 · 불일치 격리(우선).
//  §3 허브 — 결선 전 SessionCount 0 · 결선 후 다음 펌프에 즉시 반영(상태 변경 = 서명 변경 = 재게시) · 브로커 인덱스 순 · 범위 밖 인덱스 거부.
//  §4 δ=0 — Snapshot · 관리자 2개 결선 펌프(유휴/전이 재게시) · 라벨/클래스 조회 0B.
//  §5 결선 · 순수성 — BlazorHomeForm 결선 · 뷰가 포트 라벨 사용 · RCL 에 StockManager 참조/보간 0 · 래칫 ≤40.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Adapters
{
    [Collection("ConcurrencyStress")]
    public sealed class SessionRecoveryManagerTests
    {
        private const long Sec = TimeSpan.TicksPerSecond;
        private readonly ITestOutputHelper _out;
        public SessionRecoveryManagerTests(ITestOutputHelper o) => _out = o;

        private sealed class Rig
        {
            public long Now = 1_000 * Sec;
            public bool Confirmed;
            public int Isolations;
            public SessionRecoveryManager M = null!;
        }

        private static Rig NewRig(int broker, int maxFailures = 5)
        {
            var r = new Rig();
            r.M = new SessionRecoveryManager(broker, on => { if (on) r.Isolations++; }, requestReconcile: () => r.Confirmed = false,
                                             reconciled: () => r.Confirmed, maxFailures: maxFailures, nowTicks: () => r.Now);
            return r;
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        private static DashBrokerSession[] Sessions(DashboardSnapshotHub hub)
        {
            var f = hub.Acquire();
            Assert.NotNull(f);
            try { return f!.Sessions.Take(f.SessionCount).ToArray(); }
            finally { hub.Release(f!); }
        }

        private static int StateOf(DashboardSnapshotHub hub, int broker)
        {
            hub.Pump();
            return Sessions(hub).Single(s => s.Broker == broker).State;
        }

        // ════════════ §1 Snapshot ════════════

        [Fact]
        public void Snapshot_ConsistentWithProperties_AcrossTransitions()
        {
            var r = NewRig(1, maxFailures: 3);
            var m = r.M;
            void Same()
            {
                var s = m.Snapshot();
                Assert.Equal((m.State, m.Exhausted, m.Discrepancy, m.Isolated, m.ConsecutiveFailures, m.Losses, m.Recoveries, m.Attempts),
                             (s.State, s.Exhausted, s.Discrepancy, s.Isolated, s.ConsecutiveFailures, s.Losses, s.Recoveries, s.Attempts));
                Assert.Equal(3, s.MaxFailures);
            }
            Same();
            Assert.False(m.Snapshot().EverConnected);
            m.OnConnected(); Same();
            Assert.True(m.Snapshot().EverConnected);
            m.OnConnectionLost(); Same();
            Assert.Equal(1, m.Snapshot().Losses);
            for (int i = 0; i < 3; i++) { r.Now += 20 * Sec; m.Pump(); m.OnAttemptFailed(); Same(); }
            Assert.True(m.Snapshot().Exhausted);
            m.OnDiscrepancy(); Same();
            Assert.True(m.Snapshot().Discrepancy);
        }

        // ════════════ §2 분류 ════════════

        [Fact]
        public void Classify_FullLifecycle_LatchesOverrideState()
        {
            Assert.Equal(DashSession.Unbound, DashSession.Classify(false, 1, true, true, true));
            Assert.Equal(DashSession.Discrepancy, DashSession.Classify(true, 1, true, true, true));
            Assert.Equal(DashSession.Exhausted, DashSession.Classify(true, 1, true, true, false));
            Assert.Equal(DashSession.Waiting, DashSession.Classify(true, 0, false, false, false));
            Assert.Equal(DashSession.Reconnecting, DashSession.Classify(true, 0, true, false, false));
            Assert.Equal(DashSession.Unbound, DashSession.Classify(true, 9, true, false, false));

            using var hub = new DashboardSnapshotHub();
            var r = NewRig(1);
            hub.AttachSessionRecovery(r.M);
            Assert.Equal(DashSession.Waiting, StateOf(hub, 1));                                  // 최초 미연결
            r.M.OnConnected();
            Assert.Equal(DashSession.Connected, StateOf(hub, 1));
            r.M.OnConnectionLost();
            Assert.Equal(DashSession.Backoff, StateOf(hub, 1));                                  // 단절 = 백오프(격리)
            r.Now += 2 * Sec; r.M.Pump();
            Assert.True(r.M.WaitForAttemptAsync().IsCompletedSuccessfully);                      // 기한 도래 = 허가(동기)
            Assert.Equal(DashSession.Reconnecting, StateOf(hub, 1));
            r.M.OnAttemptFailed();
            hub.Pump();
            var s = Sessions(hub).Single();
            Assert.Equal((DashSession.Backoff, 1, 5), (s.State, s.Failures, s.MaxFailures));     // 실패 1/5
            r.Now += 3 * Sec; r.M.Pump(); r.M.WaitForAttemptAsync();
            r.M.OnConnected();
            Assert.Equal(DashSession.Reconciling, StateOf(hub, 1));                              // 재접속 = 대사 확정 전 격리
            r.Confirmed = true; r.M.Pump();
            Assert.Equal(DashSession.Connected, StateOf(hub, 1));
            s = Sessions(hub).Single();
            Assert.Equal((1L, 1L, 0), (s.Losses, s.Recoveries, s.Failures));
            _out.WriteLine($"[§2] 복구 후 {SignalLabelMapper.SessionStateLabel(s.State)} · 단절 {s.Losses} · 복구 {s.Recoveries} · 시도 {s.Attempts}");

            r.M.OnDiscrepancy();                                                                  // 원장 불일치/유령 = 상태 무관 sticky
            Assert.Equal(DashSession.Discrepancy, StateOf(hub, 1));
            Assert.Equal("bad", SignalLabelMapper.SessionStateClass(DashSession.Discrepancy));

            var x = NewRig(1, maxFailures: 2);
            using var hub2 = new DashboardSnapshotHub();
            hub2.AttachSessionRecovery(x.M);
            x.M.OnConnected(); x.M.OnConnectionLost();
            for (int i = 0; i < 2; i++) { x.Now += 20 * Sec; x.M.Pump(); x.M.WaitForAttemptAsync(); x.M.OnAttemptFailed(); }
            Assert.Equal(DashSession.Exhausted, StateOf(hub2, 1));
            Assert.Equal("소진 격리", SignalLabelMapper.SessionStateLabel(DashSession.Exhausted));
        }

        // ════════════ §3 허브 ════════════

        [Fact]
        public void Hub_UnboundZero_ImmediateRepublish_BrokerOrder_RangeGuard()
        {
            using var hub = new DashboardSnapshotHub();
            Assert.True(hub.Pump());
            Assert.Empty(Sessions(hub));

            var kis = NewRig(1); var kiwoom = NewRig(0);
            hub.AttachSessionRecovery(kis.M);
            hub.AttachSessionRecovery(kiwoom.M);
            Assert.True(hub.Pump());                                                              // 결선 = 서명 변경 = 재게시
            Assert.Equal(new[] { 0, 1 }, Sessions(hub).Select(s => s.Broker).ToArray());
            Assert.False(hub.Pump());                                                             // 무변경 = 게시 0

            kis.M.OnConnected();
            Assert.True(hub.Pump());                                                              // 상태 전이 = 다음 펌프 즉시 재게시
            kis.M.OnConnectionLost();
            Assert.True(hub.Pump());
            Assert.Equal(DashSession.Backoff, Sessions(hub)[1].State);
            Assert.Equal(DashSession.Waiting, Sessions(hub)[0].State);

            var far = new SessionRecoveryManager(9, null, null, null);
            Assert.Throws<ArgumentOutOfRangeException>(() => hub.AttachSessionRecovery(far));
            Assert.Throws<ArgumentNullException>(() => hub.AttachSessionRecovery(null!));
            Assert.Equal(0, hub.PumpFaults);
        }

        // ════════════ §4 δ=0 ════════════

        [Fact]
        public void ZeroAlloc_Snapshot_HubPumpWithSessions_Labels()
        {
            using var hub = new DashboardSnapshotHub(nowTicks: () => 1);
            var kis = NewRig(1); var kiwoom = NewRig(0);
            hub.AttachSessionRecovery(kiwoom.M);
            hub.AttachSessionRecovery(kis.M);
            kis.M.OnConnected();
            for (int i = 0; i < 64; i++) { kis.M.OnConnectionLost(); hub.Pump(); kis.M.OnConnected(); kis.Confirmed = true; kis.M.Pump(); hub.Pump(); }   // 예열
            for (int s = 0; s < SignalLabelMapper.SessionStateCount; s++) { SignalLabelMapper.SessionStateLabel(s); SignalLabelMapper.SessionStateClass(s); }

            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 100_000; i++) kis.M.Snapshot(); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) hub.Pump(); }));                       // 유휴(무변경)
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 200; i++)                                                                              // 매 회차 전이 = 재게시
                {
                    kis.M.OnConnectionLost(); hub.Pump();
                    kis.M.OnConnected(); kis.Confirmed = true; kis.M.Pump(); hub.Pump();
                }
            }));
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 10_000; i++) { SignalLabelMapper.SessionStateLabel(i & 7); SignalLabelMapper.SessionStateClass(i & 7); }
            }));
            Assert.Equal(0, hub.PumpFaults);
        }

        // ════════════ §5 결선 · 순수성 ════════════

        [Fact]
        public void HostWiring_ViewLabels_RclPurity_Ratchet()
        {
            Assert.Equal(8, SignalLabelMapper.SessionStateCount);
            Assert.Equal("세션 미결선", SignalLabelMapper.SessionStateLabel(DashSession.Unbound));
            Assert.Equal("ok", SignalLabelMapper.SessionStateClass(DashSession.Connected));
            Assert.Equal("wait", SignalLabelMapper.SessionStateClass(DashSession.Reconciling));
            Assert.Equal(SignalLabelMapper.Unknown, SignalLabelMapper.SessionStateLabel(99));
            Assert.Equal("off", SignalLabelMapper.SessionStateClass(-1));

            var r = PureShellFitnessSentinel.Scan(typeof(CanaryHealthSentinel).Assembly);
            Assert.True(r.WithinRatchet && r.HostCoupledUi <= 40, $"coupled {r.HostCoupledUi}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);

            string root = RepoRoot();
            foreach (var file in new[] { @"MS2E.UI.Portable\Helpers\SignalLabelMapper.cs", @"MS2E.UI.Portable\Ports\Trading\PaperDashboardPort.cs" })
            {
                string src = File.ReadAllText(Path.Combine(root, file));
                Assert.DoesNotContain("StockManager.", src);
                Assert.DoesNotContain("$\"", src);
            }
            string view = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Contains("SignalLabelMapper.SessionStateLabel(s.State)", view);
            Assert.Contains("SignalLabelMapper.SessionStateClass(s.State)", view);
            Assert.Equal(2, view.Split('\n').Count(l => l.TrimStart().StartsWith("@inject ")));
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("dashHub.AttachSessionRecovery(kiwoomRecovery)", form);
            Assert.Contains("dashHub.AttachSessionRecovery(kisRecovery)", form);
            Assert.True(form.IndexOf("new StockManager.Adapters.RecoveryStateDriftMonitor(", StringComparison.Ordinal)
                        < form.IndexOf("dashHub.AttachSessionRecovery(kisRecovery)", StringComparison.Ordinal), "관리자 생성 뒤 결선");
        }
    }
}

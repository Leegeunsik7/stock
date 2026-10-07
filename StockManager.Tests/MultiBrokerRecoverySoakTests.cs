// ============================================================================
// MultiBrokerRecoverySoakTests — [Long-Soak · 추천 A] 다중 브로커 단절 · 자동 복구 · 미체결 대사 24h 가속 · 오발주 0 · δ=0 · 포렌식
//
//  §1 24h 가속(86,400 모의 초): KIS 침묵 ~39회 · KIS 서버 장애(1·2·4s 실패 → 8s 성공) ~16회 · 키움 파이프 파기/운용자 복구 ~11회 →
//     ★오발주(진실이 더러운데 발주 가능) 0 · 비공허성(더러운 구간 차단 > 0 · 정상 발주 > 0) · 재접속마다 40건 재구독 · 재접속마다 대사 후 해제 ·
//     백오프 수열 1·2·4·8 실측 · 작동 표류 0 · 시간 창 관리 힙 0(24창 중 GC 겹침 허용 1창).
//  §2 한 번의 복구 전 구간 초 단위 추적: 침묵 → 강제 종료 → 백오프 → 재접속 → 대사 → 해제 — 해제 전 매 초 KIS 발주 불가.
//  §3 소진: 재접속 연속 5회 실패 = 래치 1회 · 이후 서버 복구 · 대사 확정에도 KIS 발주 0 · 이중키 리셋 후에만.
//  §4 유령/원가 불일치 주입: KIS 유령 = 비트 22 sticky + TTTC0803U 비상 취소 · KIS 원가 괴리 = 비트 22(취소 없음) · 키움 유령 = 비트 22 + 키움 취소.
//  §5 [방향 A] 포렌식 모니터: 전이 블랙박스(체류 틱) · 고장 난 격리 작동기 = 표류 1회(디바운스) · δ=0.
//  §6 호스트 결선 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MultiBrokerRecoverySoakTests
    {
        private readonly ITestOutputHelper _out;
        public MultiBrokerRecoverySoakTests(ITestOutputHelper output) => _out = output;

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

        private static bool KisDispatchable(MultiBrokerRecoverySoakHarness h)
        {
            var caps = new BrokerSplitCap[2];
            h.Limits.Fill(caps, MultiBrokerRecoverySoakHarness.Code, OrderSide.Buy, MultiBrokerRecoverySoakHarness.Limit * 100_000_000, 0);
            return caps[MultiBrokerRecoverySoakHarness.KisBroker].Available && !h.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence);
        }

        // ════════════ §1 24h 가속 ════════════

        [Fact]
        public void Soak24h_ZeroStaleDispatch_EveryReconnectReconciled_Backoff_ZeroAllocWindows()
        {
            var h = new MultiBrokerRecoverySoakHarness();
            Assert.True(h.Warmup());
            h.RunSeconds(3_600);                                                        // JIT · 경로 워밍업(1h)
            var r0 = h.Report();
            var windows = new long[24];
            for (int w = 0; w < 24; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                h.RunSeconds(3_600);
                windows[w] = GC.GetAllocatedBytesForCurrentThread() - b;
            }
            var r = h.Report();
            _out.WriteLine($"[24h] {r}");
            _out.WriteLine("[24h] 시간 창 할당(B): " + string.Join(",", windows));
            _out.WriteLine("[24h] 재시도 간격(s): " + string.Join(",", h.AttemptGapsSeconds.ToArray().Take(40)));

            Assert.Equal(0, r.StaleDispatchViolations);                                 // ★오발주 0
            Assert.True(r.DirtyProbesBlocked > 0, "비공허성: 더러운 구간이 실제로 차단돼야 함");
            Assert.True(r.OrdersEmitted > 0, "비공허성: 깨끗한 구간엔 발주 가능해야 함");
            Assert.True(r.KisSilences - r0.KisSilences >= 30);
            Assert.True(r.KisAborts >= r.KisSilences - 1);                              // 침묵마다 죽은 소켓 강제 종료
            Assert.True(r.KisReconnects >= 30);
            Assert.Equal((r.KisReconnects + 1) * h.SubscriptionsPerConnect, r.Resubscriptions);   // 최초 + 재접속마다 40건
            Assert.Equal(40, h.SubscriptionsPerConnect);
            Assert.True(r.KisRecoveries >= r.KisReconnects - 1);                        // 재접속마다 대사 확정 → 해제(마지막 진행 중 1 허용)
            Assert.True(r.KiwoomPipeBreaks >= 10);
            Assert.True(r.KiwoomRecoveries >= r.KiwoomPipeRestoresOrZero() - 1);
            Assert.True(r.KisAttemptFailures >= 3 * 15);                                // 서버 장애 회차 × 3
            Assert.False(r.KisExhausted);
            Assert.Equal(0, r.Exhaustions);
            Assert.Equal(0, r.Drifts);
            Assert.True(r.Transitions > 100);
            Assert.False(h.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));   // 오염 주입 없음 = 비트 22 해제 상태로 종료
            // 백오프 수열: 서버 장애 회차 = 단절 후 1s · 실패 → 2s · 실패 → 4s · 실패 → 8s 성공
            var gaps = h.AttemptGapsSeconds.ToArray();
            bool seq = false;
            for (int i = 0; i + 3 < gaps.Length; i++) if (gaps[i] == 1 && gaps[i + 1] == 2 && gaps[i + 2] == 4 && gaps[i + 3] == 8) { seq = true; break; }
            Assert.True(seq, "백오프 1·2·4·8 수열 미관측");
            Assert.True(gaps.All(g => g is 1 or 2 or 4 or 8 or 16));
            // δ=0: 24 시간 창 중 0B 아닌 창 ≤ 1(타 스레드 GC 겹침 허용 — 호출당 할당이면 사건이 있는 거의 모든 창이 양수)
            Assert.True(windows.Count(x => x != 0) <= 1, "할당 창: " + string.Join(",", windows));
        }

        // ════════════ §2 복구 전 구간 추적 ════════════

        [Fact]
        public void OneRecovery_TracedPerSecond_KisNeverDispatchableUntilReconciledRelease()
        {
            var h = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(h.Warmup());
            h.RunSeconds(40, autoInject: false);                                         // KIS 평시 주기 대사 1회 이상
            Assert.True(KisDispatchable(h));
            h.SetKisServerDown(2);
            h.InjectKisSilence();
            var trace = new List<string>();
            bool released = false;
            for (int i = 0; i < 60 && !released; i++)
            {
                h.RunSeconds(1, autoInject: false);
                bool open = KisDispatchable(h);
                trace.Add($"{i}:{h.KisRecovery.State}/{(h.KisSocketUp ? "up" : "down")}/{(open ? "OPEN" : "held")}");
                if (h.KisRecovery.State != SessionRecoveryState.Connected) Assert.False(open, "복구 중 발주 가능: " + string.Join(" ", trace));
                released = h.KisRecovery.State == SessionRecoveryState.Connected && open && h.KisRecovery.Recoveries == 1;
            }
            _out.WriteLine("[추적] " + string.Join(" ", trace));
            Assert.True(released, "해제 미도달: " + string.Join(" ", trace));
            Assert.Equal(new long[] { 1, 2, 4 }, h.AttemptGapsSeconds.ToArray());       // 단절 후 1s 시도(실패) · 2s(실패) · 4s 성공
            Assert.True(trace.Count(t => t.Contains("Reconciling/up/held")) >= 2);      // REST 지연 동안 접속됐지만 대사 전 = 차단(초 단위 관측)
            Assert.Contains(trace, t => t.Contains("Reconciling/up/held"));             // ★접속됐지만 대사 전 = 차단
            Assert.Equal(2 * h.SubscriptionsPerConnect, h.Report().Resubscriptions);
        }

        // ════════════ §3 소진 ════════════

        [Fact]
        public void ReconnectExhaustion_LatchesKis_EvenAfterServerRecovers_UntilDualKey()
        {
            var h = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(h.Warmup());
            h.RunSeconds(40, autoInject: false);
            h.SetKisServerDown(5);
            h.InjectKisSilence();
            h.RunSeconds(1 + 3 + 1 + 2 + 4 + 8 + 16 + 2, autoInject: false);           // 침묵 감지 + 백오프 5회
            var r = h.Report();
            Assert.True(r.KisExhausted);
            Assert.Equal(1, r.Exhaustions);
            h.RunSeconds(120, autoInject: false);                                        // 서버 복구 → 재접속 → 대사 확정
            Assert.True(h.KisSocketUp);
            Assert.True(h.Reconciler.IsConfirmed(OpenOrderVenueKind.Kis));
            Assert.False(KisDispatchable(h));                                           // ★래치 = 발주 0
            Assert.Equal(0, h.Report().StaleDispatchViolations);
            Assert.False(h.KisSession.TryRearm(true, true));                             // 세션 재무장도 거부(복구 관리자 Held)
            Assert.True(h.KisRecovery.TryReset(true, true));
            h.RunSeconds(1, autoInject: false);
            Assert.True(KisDispatchable(h));
        }

        // ════════════ §4 유령 · 원가 불일치 ════════════

        [Fact]
        public void KisGhost_Bit22Sticky_EmergencyCancel_KisLatched_NoStaleDispatch()
        {
            var h = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(h.Warmup());
            h.InjectKisSilence();
            h.RunSeconds(6, autoInject: false);                                          // 단절 · 재접속 진행
            h.InjectKisGhost();                                                          // 재접속 직후 KIS 원장에 무인가 주문
            h.RunSeconds(35, autoInject: false);                                         // 재접속 후 조회(REST 지연 2s → 3s 재조회) 또는 30초 주기
            var r = h.Report();
            Assert.True(h.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));    // ★비트 22 sticky
            Assert.True(r.KisCancels >= 1);                                              // TTTC0803U 비상 취소
            Assert.True(h.KisQuery.DiscrepancyLatched);
            Assert.True(h.KisRecovery.Discrepancy && h.KisRecovery.Held);
            Assert.False(KisDispatchable(h));
            h.RunSeconds(300, autoInject: false);
            Assert.Equal(0, h.Report().StaleDispatchViolations);
            Assert.True(h.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));    // 유지
        }

        [Fact]
        public void KisPriceDivergence_And_KiwoomGhost_RaiseBit22_CancelOnlyGhost()
        {
            var a = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(a.Warmup());
            a.InjectKisPriceDivergence();                                                 // 원가(지정가) 괴리
            a.RunSeconds(35, autoInject: false);
            Assert.True(a.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.True(a.Reconciler.DivergenceDetected);
            Assert.Equal(0, a.Report().KisCancels);                                      // 불일치 = 취소 대상 아님(차단만)
            Assert.False(KisDispatchable(a));
            Assert.Equal(0, a.Report().StaleDispatchViolations);

            var k = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(k.Warmup());
            k.InjectKiwoomGhost();
            k.RunSeconds(35, autoInject: false);
            Assert.True(k.Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence));
            Assert.True(k.Reconciler.GhostDetected);
            Assert.True(k.Report().KiwoomCancels >= 1);                                  // 키움 원장 번호 직접 취소
            Assert.Equal(0, k.Report().StaleDispatchViolations);
        }

        // ════════════ §5 [방향 A] 포렌식 모니터 ════════════

        [Fact]
        public void Monitor_RecordsTransitionsWithDwell_DetectsBrokenActuator_ZeroAlloc()
        {
            var h = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(h.Warmup());
            long seq0 = h.Blackbox.ClaimedSeq;
            h.InjectKisSilence();
            h.RunSeconds(15, autoInject: false);
            var ev = new FlightRecordEvent[512];
            int n = h.Blackbox.SnapshotRecent(ev);
            var tr = ev.Take(n).Where(e => e.Sequence > seq0 && e.Kind == (byte)FlightEventKind.SessionRecoveryTransition && e.SymbolCode == MultiBrokerRecoverySoakHarness.KisBroker).ToArray();
            foreach (var e in tr) _out.WriteLine($"[블랙박스] {RecoveryStateDriftMonitor.PackedState(e.Arg0)}→{RecoveryStateDriftMonitor.PackedState(e.Arg1)} 체류 {e.Arg2 / 10_000}ms");
            Assert.True(tr.Length >= 3);                                                 // Connected→Backoff→Connecting→Reconciling→Connected
            Assert.Contains(tr, e => RecoveryStateDriftMonitor.PackedState(e.Arg1) == SessionRecoveryState.Backoff);
            Assert.Contains(tr, e => RecoveryStateDriftMonitor.PackedState(e.Arg0) == SessionRecoveryState.Reconciling && RecoveryStateDriftMonitor.PackedState(e.Arg1) == SessionRecoveryState.Connected);
            Assert.True(h.Monitor.MaxHeldTicks(1) >= 3 * 10_000_000L);                   // 격리 체류 ≥ 3초(모의 시계 틱)
            Assert.Equal(0, h.Monitor.Drifts);

            // 고장 난 작동기(격리 델리게이트가 한도 피드를 안 건드림) = 표류 — 2 펌프 디바운스 후 1회만
            long t = 0;
            var limits = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { null, null });
            var broken = new SessionRecoveryManager(1, isolate: _ => { }, requestReconcile: null, reconciled: null, nowTicks: () => t);
            var box = new MmfFlightRecorder(1_024, () => t);
            var mon = new RecoveryStateDriftMonitor(box, limits, () => t, broken);
            Assert.True(broken.WaitForAttemptAsync().IsCompletedSuccessfully);
            broken.OnConnected();
            broken.OnConnectionLost();                                                    // Isolated=true · 한도 피드는 그대로(고장)
            mon.Pump();
            Assert.Equal(0, mon.Drifts);                                                  // 1회 = 경합일 수 있음(디바운스)
            mon.Pump(); mon.Pump(); mon.Pump();
            Assert.Equal(1, mon.Drifts);                                                  // 지속 = 1회 기록
            Assert.Equal(1, box.TotalRecorded - mon.Transitions(0));                     // 전이 기록 + 표류 1건

            // δ=0: 모니터 펌프 · 전이 기록
            var h2 = new MultiBrokerRecoverySoakHarness { KisSilenceEverySeconds = 0, KisOutageEverySeconds = 0, KiwoomBreakEverySeconds = 0 };
            Assert.True(h2.Warmup());
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 5_000; i++) h2.Monitor.Pump(); }));
            Assert.Equal(0, MinAllocated(() => { h2.InjectKisSilence(); h2.RunSeconds(30, autoInject: false); }));   // 한 번의 단절→복구→대사 전 구간
        }

        // ════════════ §6 결선 · 오중 빌드 ════════════

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_Wiring_MonitorPumpedAfterManagers_EventCodesUnique_FiveBuilds()
        {
            string root = RepoRoot();
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("new StockManager.Adapters.RecoveryStateDriftMonitor(blackbox, limitFeed, null, kiwoomRecovery, kisRecovery)", form);
            int mgr = form.IndexOf("try { kisRecovery?.Pump(); } catch { }", StringComparison.Ordinal), mon = form.IndexOf("try { recoveryMonitor.Pump(); } catch { }", StringComparison.Ordinal);
            Assert.True(mgr > 0 && mon > mgr, "모니터는 관리자 펌프 뒤");
            Assert.Equal(100, (int)FlightEventKind.SessionRecoveryTransition);
            Assert.Equal(101, (int)FlightEventKind.SessionRecoveryDrift);
            var values = Enum.GetValues<FlightEventKind>().Select(v => (int)v).ToArray();
            Assert.Equal(values.Length, values.Distinct().Count());
            string sln = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", System.IO.File.ReadAllText(System.IO.Path.Combine(root, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }

    internal static class RecoverySoakReportExtensions
    {
        public static long KiwoomPipeRestoresOrZero(this RecoverySoakReport r) => r.KiwoomRestores;
    }
}

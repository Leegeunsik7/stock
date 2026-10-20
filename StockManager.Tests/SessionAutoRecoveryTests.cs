// ============================================================================
// SessionAutoRecoveryTests — [세션 자동 복구 · 추천 A] SessionRecoveryManager · KisRealtimeSession 결속 · 대사 결속 · 소진 래치
//
//  §1 상태기계: 백오프 1·2·4·8·16s 상한 · 단절 즉시 격리(한도 0 · 블랙리스트) · 펌프 허가 · 재접속 → 대사 '먼저' 철회 후 Reconciling ·
//     확정 전 격리 유지 · 최초 미연결 실패 = 격리/소진 없음 · 플래핑 = 소진 수렴 · 침묵 = 강제 종료 · 수동 모드(키움) = 재접속 0.
//  §2 [방향 B] 소진 래치: 재접속 연속 5회 실패 = 1회 통지 · 대사 확정돼도 격리 유지 · 이중키 리셋.
//  §3 ★해제 ↔ 단절 경합: 단절 보고가 반환된 '직후' 항상 격리(설계 검토 중 실측 결함 — 락 밖 작동이면 해제가 뒤늦게 적용) · 스트레스.
//  §4 [방향 A] δ=0: 펌프(격리 재주장) · 단절→허가→실패 사이클 · 동기 허가 · 백오프 계산 · 침묵 판정 · 한도 피드 격리 · 대사 요청.
//  §5 통합: half-open 소켓(수신 무한 대기) → 장중 3초 침묵 → 강제 종료 → 1s 백오프 → 재접속 · 재구독 40건(체결통보 · 심박 · 호가 38) 동일 →
//     단절 '전' 대사 확정 철회 → 확정 전 KIS 한도 0 · 블랙리스트 · 재무장 거부 → KIS 대사 패스 확정 → 격리 해제 · 재무장 허용.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class SessionAutoRecoveryTests
    {
        private readonly ITestOutputHelper _out;
        public SessionAutoRecoveryTests(ITestOutputHelper output) => _out = output;

        private static readonly long Sec = TimeSpan.TicksPerSecond;

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

        // ── 리그 ──

        private sealed class BoundView : IBrokerAccountView
        {
            public bool Bound => true;
            public bool SharesGateReservation => false;
            public bool TryGetBuyHeadroomWon(out long won) { won = 1_000_000_000; return true; }
            public bool TryGetSellableShares(long symbolCode, out long shares) { shares = 1_000; return true; }
        }

        private sealed class Rig
        {
            public long Now = 1_000 * Sec;
            public readonly LockFreeBrokerSwitchboard Board = new(2);
            public readonly BrokerLimitFeedProvider Limits = new(new IBrokerAccountView?[] { new BoundView(), new BoundView() });
            public readonly BrokerSplitCap[] Caps = new BrokerSplitCap[2];
            public int ReconcileRequests, Exhausted, Aborts;
            public bool Reconciled, Silent, Link;
            public SessionRecoveryState StateAtReconcileRequest = (SessionRecoveryState)255;
            public SessionRecoveryManager M = null!;
            public void Advance(double seconds) => Now += (long)(seconds * Sec);
            public bool KisOpen()
            {
                Limits.Fill(Caps, 5930, OrderSide.Buy, 70_000L * 100_000_000, 0);
                return Caps[1].Available;
            }
        }

        private static Rig MakeRig(bool auto = true, int maxFailures = 5, int broker = 1)
        {
            var r = new Rig();
            r.M = new SessionRecoveryManager(broker,
                isolate: on => { r.Limits.SetSessionIsolated(broker, on); if (on) r.Board.Blacklist(broker); },
                requestReconcile: () => { r.ReconcileRequests++; r.StateAtReconcileRequest = r.M.State; r.Reconciled = false; },
                reconciled: () => r.Reconciled,
                silent: () => r.Silent,
                linkProbe: auto ? null : () => r.Link,
                onExhausted: () => r.Exhausted++,
                autoReconnect: auto, maxFailures: maxFailures, nowTicks: () => r.Now);
            r.M.BindAbort(() => r.Aborts++);
            return r;
        }

        /// <summary>허가가 '동기로' 났는지(펌프가 이미 기한을 처리) — 동기 아니면 false.</summary>
        private static bool Permit(SessionRecoveryManager m)
        {
            var vt = m.WaitForAttemptAsync();
            return vt.IsCompletedSuccessfully && vt.Result;
        }

        /// <summary>최초 접속 성공까지(허가 동기 통과 → OnConnected).</summary>
        private static void ConnectInitially(Rig r)
        {
            var vt = r.M.WaitForAttemptAsync();
            Assert.True(vt.IsCompletedSuccessfully && vt.Result);
            r.M.OnConnected();
            Assert.Equal(SessionRecoveryState.Connected, r.M.State);
        }

        // ════════════ §1 상태기계 ════════════

        [Fact]
        public void Backoff_Sequence_1_2_4_8_16_Capped()
        {
            var r = MakeRig();
            long[] expect = { 1, 2, 4, 8, 16, 16, 16 };
            for (int i = 0; i < expect.Length; i++) Assert.Equal(expect[i] * Sec, r.M.BackoffTicksFor(i));
            Assert.Equal(16 * Sec, r.M.BackoffTicksFor(int.MaxValue));                 // 시프트 오버플로 = 상한
        }

        [Fact]
        public async Task Loss_IsolatesImmediately_PumpGrantsAttemptOnlyAfterBackoff()
        {
            var r = MakeRig();
            ConnectInitially(r);
            Assert.True(r.KisOpen());
            Assert.False(r.Board.IsBlacklisted(1));

            r.M.OnConnectionLost();
            Assert.Equal(SessionRecoveryState.Backoff, r.M.State);
            Assert.True(r.M.Held && r.M.Isolated);
            Assert.False(r.KisOpen());                                                  // ★단절 즉시 한도 0
            Assert.True(r.Board.IsBlacklisted(1));
            var vt = r.M.WaitForAttemptAsync();
            Assert.False(vt.IsCompleted);                                               // 기한 전 = 대기
            r.Advance(0.999); r.M.Pump();
            Assert.False(vt.IsCompleted);
            r.Advance(0.001); r.M.Pump();                                               // 1s 도래 = 허가
            Assert.True(SpinWait.SpinUntil(() => vt.IsCompleted, 5_000));
            Assert.True(await vt);
            Assert.Equal(SessionRecoveryState.Connecting, r.M.State);
            Assert.False(r.KisOpen());                                                  // 재접속 시도 중에도 격리
        }

        [Fact]
        public void Reconnect_RevokesConfirmBeforeReconciling_HeldUntilFreshConfirm()
        {
            var r = MakeRig();
            ConnectInitially(r);
            Assert.Equal(0, r.ReconcileRequests);                                       // 최초 접속 = 대사 요청 없음(기존 최초 대사 경로가 담당)
            r.Reconciled = true;                                                        // 단절 '전' 확정 상태
            r.M.OnConnectionLost();
            r.Advance(1); r.M.Pump();
            Assert.True(Permit(r.M));
            r.M.OnConnected();
            Assert.Equal(1, r.ReconcileRequests);
            Assert.Equal(SessionRecoveryState.Connecting, r.StateAtReconcileRequest);  // ★철회가 Reconciling 전이보다 먼저(펌프가 낡은 확정으로 못 푼다)
            Assert.Equal(SessionRecoveryState.Reconciling, r.M.State);
            for (int i = 0; i < 20; i++) { r.Advance(0.05); r.M.Pump(); }
            Assert.True(r.M.Held);
            Assert.False(r.KisOpen());                                                  // 확정 전 = 한도 0 유지
            r.Reconciled = true;
            r.M.Pump();
            Assert.Equal(SessionRecoveryState.Connected, r.M.State);
            Assert.False(r.M.Held || r.M.Isolated);
            Assert.True(r.KisOpen());
            Assert.Equal(1, r.M.Recoveries);
            Assert.Equal(0, r.M.ConsecutiveFailures);
        }

        [Fact]
        public void NoReconcileMeans_HeldForever_FailClosed()
        {
            var r = new Rig();
            var m = new SessionRecoveryManager(1, on => r.Limits.SetSessionIsolated(1, on), requestReconcile: null, reconciled: null, nowTicks: () => r.Now);
            Assert.True(Permit(m));
            m.OnConnected();
            m.OnConnectionLost();
            r.Advance(1); m.Pump();
            Assert.True(Permit(m));
            m.OnConnected();
            for (int i = 0; i < 100; i++) { r.Advance(1); m.Pump(); }
            Assert.Equal(SessionRecoveryState.Reconciling, m.State);                    // 대사 수단 없음(KIS TTTC8001R 미결선과 같은 조건) = 격리 유지
            Assert.False(r.KisOpen());
        }

        [Fact]
        public void InitialConnectFailures_NeverIsolate_NeverExhaust()
        {
            var r = MakeRig();
            long[] gaps = { 1, 2, 4, 8, 16, 16, 16, 16, 16, 16, 16, 16 };
            for (int i = 0; i < gaps.Length; i++)
            {
                Assert.True(Permit(r.M));                                                // 첫 시도 즉시 · 이후 펌프 허가
                r.M.OnAttemptFailed();
                Assert.Equal(gaps[i] * Sec, r.M.NextAttemptTicks - r.Now);              // 최초 실패도 1s 부터 배증 · 16s 상한
                r.Advance(gaps[i]); r.M.Pump();
            }
            Assert.False(r.M.Exhausted);
            Assert.Equal(0, r.Exhausted);
            Assert.False(r.M.Isolated);
            Assert.False(r.Board.IsBlacklisted(1));                                     // 장 전 미접속 브로커를 영구 블랙리스트에 올리지 않는다
        }

        [Fact]
        public void Silence_WhileConnected_AbortsDeadSocket_OnlyWhenConnected()
        {
            var r = MakeRig();
            r.Silent = true;
            r.M.Pump();
            Assert.Equal(0, r.Aborts);                                                  // 미연결 = 강제 종료 대상 아님
            ConnectInitially(r);
            r.M.Pump();
            Assert.Equal(1, r.Aborts);
            Assert.Equal(1, r.M.SilenceAborts);
            r.M.OnConnectionLost();                                                     // 세션 finally 가 보고
            r.M.Pump();
            Assert.Equal(1, r.Aborts);                                                  // Backoff 에선 재종료 없음
        }

        [Fact]
        public void ManualMode_KiwoomLinkLoss_IsolatesOnly_NoAttempts_ReconcileOnReturn()
        {
            var r = MakeRig(auto: false, broker: 0);
            r.M.Pump();
            Assert.False(r.M.Isolated);                                                 // 링크 수립 전 = 격리 없음
            r.Link = true; r.M.Pump();
            Assert.Equal(SessionRecoveryState.Connected, r.M.State);
            r.Link = false; r.M.Pump();
            Assert.True(r.M.Isolated);
            Assert.True(r.Board.IsBlacklisted(0));
            for (int i = 0; i < 50; i++) { r.Advance(20); r.M.Pump(); }
            Assert.Equal(0, r.M.Attempts);                                              // ★자동 재기동 없음(브리지 1회용 · 운용자 몫)
            Assert.Equal(0, r.ReconcileRequests);
            r.Link = true; r.M.Pump();                                                  // 운용자 재기동 → 링크 복귀
            Assert.Equal(1, r.ReconcileRequests);
            Assert.Equal(SessionRecoveryState.Reconciling, r.M.State);
            Assert.True(r.M.Isolated);
            r.Reconciled = true; r.M.Pump();
            Assert.Equal(SessionRecoveryState.Connected, r.M.State);
            Assert.False(r.M.Isolated);
            Assert.Throws<ArgumentException>(() => new SessionRecoveryManager(0, null, null, null, autoReconnect: false));
        }

        // ════════════ §2 [방향 B] 소진 래치 ════════════

        [Fact]
        public void Exhaustion_FiveConsecutiveReconnectFailures_LatchesIsolation_DualKeyReset()
        {
            var r = MakeRig();
            ConnectInitially(r);
            r.M.OnConnectionLost();
            Assert.Equal(1 * Sec, r.M.NextAttemptTicks - r.Now);
            long[] gaps = { 2, 4, 8, 16, 16 };
            for (int i = 0; i < 5; i++)
            {
                r.Advance((r.M.NextAttemptTicks - r.Now) / (double)Sec);
                r.M.Pump();
                Assert.True(Permit(r.M));
                r.M.OnAttemptFailed();
                Assert.Equal(gaps[i] * Sec, r.M.NextAttemptTicks - r.Now);
                Assert.Equal(i == 4, r.M.Exhausted);
            }
            Assert.Equal(1, r.Exhausted);                                               // 통지 1회
            Assert.Equal(1, r.M.Exhaustions);
            // 이후 성공 · 대사 확정이어도 격리 유지(래치)
            r.Advance(16); r.M.Pump();
            Assert.True(Permit(r.M));
            r.M.OnConnected();
            r.Reconciled = true; r.M.Pump();
            Assert.Equal(SessionRecoveryState.Connected, r.M.State);
            Assert.True(r.M.Held && r.M.Isolated);
            Assert.False(r.KisOpen());
            Assert.False(r.M.TryReset(true, false));                                    // 단일 키 = 거부
            Assert.True(r.M.Exhausted);
            Assert.True(r.M.TryReset(true, true));
            Assert.False(r.M.Held || r.M.Isolated);
            Assert.True(r.KisOpen());
            for (int i = 0; i < 4; i++) r.M.OnAttemptFailed();
            Assert.False(r.M.Exhausted);                                                // 리셋 후 연속 실패는 0 부터 다시
            r.M.OnAttemptFailed();
            Assert.True(r.M.Exhausted);                                                 // 새 연속 5회 = 재래치 · 재통지
            Assert.Equal(2, r.Exhausted);
        }

        [Fact]
        public void Flapping_ReconnectThenDropBeforeReconcile_ConvergesToExhaustion()
        {
            var r = MakeRig();
            ConnectInitially(r);
            r.M.OnConnectionLost();
            for (int i = 0; i < 5; i++)
            {
                r.Advance(16); r.M.Pump();
                Assert.True(Permit(r.M));
                r.M.OnConnected();                                                       // 접속은 되지만
                r.M.OnConnectionLost();                                                  // 대사 전 끊김
            }
            Assert.True(r.M.Exhausted);
            Assert.Equal(1, r.Exhausted);
            Assert.False(r.KisOpen());
        }

        // ════════════ §3 ★해제 ↔ 단절 경합 ════════════

        [Fact]
        public async Task ReleaseVsLoss_Race_LossAlwaysLeavesBrokerIsolated_Stress()
        {
            var r = new Rig();
            long clock = 0;
            int reconciled = 1;
            var m = new SessionRecoveryManager(1,
                isolate: on => { r.Limits.SetSessionIsolated(1, on); },
                requestReconcile: () => { }, reconciled: () => Volatile.Read(ref reconciled) == 1,
                maxFailures: int.MaxValue, baseBackoff: TimeSpan.FromTicks(1), maxBackoff: TimeSpan.FromTicks(1),
                nowTicks: () => Interlocked.Increment(ref clock));
            Assert.True(Permit(m));
            m.OnConnected();
            using var stop = new CancellationTokenSource();
            var pumper = Task.Run(() => { while (!stop.IsCancellationRequested) m.Pump(); });
            int violations = 0, cycles = 0, releasedObserved = 0;
            try
            {
                for (int i = 0; i < 20_000; i++)
                {
                    m.OnConnectionLost();
                    if ((r.Limits.SessionIsolatedMask & 2) == 0) violations++;            // ★단절 반환 직후 = 반드시 격리
                    try { if (!await m.WaitForAttemptAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))) break; }
                    catch (TimeoutException) { break; }
                    m.OnConnected();
                    if ((r.Limits.SessionIsolatedMask & 2) == 0) releasedObserved++;      // 펌프가 대사 확정으로 풀었을 수 있음(경합 유발)
                    cycles++;
                }
            }
            finally { stop.Cancel(); await pumper; }
            _out.WriteLine($"[경합] 사이클 {cycles} · 해제 관측 {releasedObserved} · 위반 {violations} · 격리 {m.Isolations} · 해제 {m.Releases}");
            Assert.Equal(20_000, cycles);
            Assert.Equal(0, violations);
        }

        // ════════════ §4 [방향 A] δ=0 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc_Pump_Cycle_SyncPermit_Backoff_Silence_LimitFeed_Reconcile()
        {
            var r = MakeRig(maxFailures: int.MaxValue);
            ConnectInitially(r);
            r.M.OnConnectionLost();
            // 워밍업
            for (int i = 0; i < 64; i++) Cycle(r);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) r.M.Pump(); }));                 // 격리 재주장 펌프
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) Cycle(r); }));                   // 단절→허가(동기)→실패
            Assert.Equal(0, MinAllocated(() => { long s = 0; for (int i = 0; i < 10_000; i++) s += r.M.BackoffTicksFor(i & 31); GC.KeepAlive(s); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) r.KisOpen(); }));                // 한도 피드 세션 격리 판독

            var session = new KisSessionInterlock(null, 1, () => r.Now / 10, silenceMicros: 3_000_000);
            session.MarkConnected();
            bool silent = false;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) silent |= session.SilentNow(); }));

            var exec = OpenOrderRig.MakeExec();
            var recon = new BrokerOpenOrderReconciler(exec.Feed, null, null, requireKiwoom: false, requireKis: false, capacity: 64);
            recon.RequestReconcile(OpenOrderVenueKind.Kis);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) recon.RequestReconcile(OpenOrderVenueKind.Kis); }));
            GC.KeepAlive(silent);
        }

        private static void Cycle(Rig r)
        {
            r.Advance(16); r.M.Pump();                                                  // 기한 도래 = 허가(대기자 없음 → 다음 대기 동기 통과)
            var vt = r.M.WaitForAttemptAsync();
            if (!vt.IsCompletedSuccessfully || !vt.Result) throw new InvalidOperationException("동기 허가 아님");
            r.M.OnAttemptFailed();
        }

        // ════════════ §5 통합: half-open → 침묵 강제 종료 → 백오프 재접속 · 재구독 → 대사 결속 ════════════

        private sealed class HalfOpenTransport : IKisWsTransport
        {
            public readonly List<string> Sent = new();
            public int Connects;
            public Task ConnectAsync(Uri uri, CancellationToken ct) { Connects++; return Task.CompletedTask; }
            public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) { lock (Sent) Sent.Add(Encoding.UTF8.GetString(payload.Span)); return Task.CompletedTask; }
            public async ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);          // ★half-open: 서버가 사라져도 아무것도 오지 않는다
                return (0, true, true);
            }
            public int SentCount { get { lock (Sent) return Sent.Count; } }
            public void Dispose() { }
        }

        private static List<string> Subscriptions(HalfOpenTransport t)
        {
            var list = new List<string>();
            lock (t.Sent)
                foreach (var s in t.Sent)
                {
                    using var d = JsonDocument.Parse(s);
                    var input = d.RootElement.GetProperty("body").GetProperty("input");
                    list.Add(input.GetProperty("tr_id").GetString() + ":" + input.GetProperty("tr_key").GetString());
                }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        [Fact]
        public async Task Integration_HalfOpenSilence_Abort_Backoff_Resubscribe38_ReconcileGate_NoStaleArming()
        {
            long micros = 5_000_000_000;
            var interlock = new KisSessionInterlock(null, 1, () => Volatile.Read(ref micros), silenceMicros: 3_000_000);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisSession);
            interlock.BindInterlock(mask, ArmingInterlockSource.KisSession);
            using var adapter = new KisOpenApiAdapter(interlock);
            var board = new LockFreeBrokerSwitchboard(2);
            var limits = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { new BoundView(), new BoundView() });
            var caps = new BrokerSplitCap[2];
            bool KisOpen() { limits.Fill(caps, 5930, OrderSide.Buy, 70_000L * 100_000_000, 0); return caps[1].Available; }
            var exec = OpenOrderRig.MakeExec();
            var recon = new BrokerOpenOrderReconciler(exec.Feed, null, null, requireKiwoom: false, requireKis: false, capacity: 64);
            byte[] emptyPage = Encoding.UTF8.GetBytes("{\"output1\":[],\"rt_cd\":\"0\"}");
            void KisPass() { recon.BeginSnapshot(OpenOrderVenueKind.Kis); recon.OnKisPage(emptyPage, last: true); }
            KisPass();
            Assert.True(recon.IsConfirmed(OpenOrderVenueKind.Kis));                     // 단절 '전' 확정(낡은 확정 위험의 전제)

            var m = new SessionRecoveryManager(1,
                isolate: on => { limits.SetSessionIsolated(1, on); if (on) board.Blacklist(1); },
                requestReconcile: () => recon.RequestReconcile(OpenOrderVenueKind.Kis),
                reconciled: () => recon.IsConfirmed(OpenOrderVenueKind.Kis),
                silent: interlock.SilentNow,
                nowTicks: () => Volatile.Read(ref micros) * 10);
            interlock.SetRearmHold(() => m.Held);
            var books = Enumerable.Range(1, KisRealtimeSession.MaxBookSymbols).Select(i => i.ToString("D6")).ToArray();
            var transports = new List<HalfOpenTransport>();
            var rt = new KisRealtimeSession(adapter, interlock, () => { var t = new HalfOpenTransport(); lock (transports) transports.Add(t); return t; },
                                            () => "approval-key", "htsuser", vts: false, bookSymbols: books, recovery: m);
            m.BindAbort(rt.AbortConnection);
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => rt.RunAsync(cts.Token));
            try
            {
                HalfOpenTransport T(int i) { lock (transports) return transports[i]; }
                int Count() { lock (transports) return transports.Count; }
                Assert.True(SpinWait.SpinUntil(() => Count() == 1 && T(0).SentCount == rt.SubscriptionCount && m.State == SessionRecoveryState.Connected, 10_000));
                Assert.Equal(40, rt.SubscriptionCount);                                 // 체결통보 + 심박 + 호가 38
                Assert.True(KisOpen());
                Assert.False(mask.IsRaised(ArmingInterlockSource.KisSession));

                // ① half-open: 3초 넘게 아무것도 오지 않음 → 침묵 → 펌프가 죽은 소켓 강제 종료
                Interlocked.Add(ref micros, 3_500_000);
                interlock.Evaluate();
                m.Pump();
                Assert.True(SpinWait.SpinUntil(() => m.State == SessionRecoveryState.Backoff, 10_000));
                Assert.True(rt.Aborts >= 1);
                Assert.True(interlock.Tripped);                                         // 비트 19 sticky
                Assert.False(KisOpen());                                                // ★한도 0
                Assert.True(board.IsBlacklisted(1));
                Assert.False(interlock.TryRearm(true, true));

                // ② 1s 백오프 → 재접속 · 재구독(같은 40건) → 대사 확정 철회 → Reconciling
                Interlocked.Add(ref micros, 1_000_000);
                m.Pump();
                Assert.True(SpinWait.SpinUntil(() => Count() == 2 && T(1).SentCount == rt.SubscriptionCount && m.State == SessionRecoveryState.Reconciling, 10_000));
                Assert.Equal(Subscriptions(T(0)), Subscriptions(T(1)));
                Assert.Equal(1, recon.ReconcileRequests);
                Assert.False(recon.IsConfirmed(OpenOrderVenueKind.Kis));                // ★단절 전 확정 철회
                for (int i = 0; i < 40; i++) { Interlocked.Add(ref micros, 50_000); interlock.OnActivity(false); m.Pump(); }
                Assert.Equal(SessionRecoveryState.Reconciling, m.State);
                Assert.False(KisOpen());                                                // ★대사 전 한도 0 유지(Zero Stale Order)
                Assert.True(interlock.Connected);
                Assert.False(interlock.TryRearm(true, true));                           // ★연결됐어도 대사 전 재무장 거부

                // ③ KIS 미체결 대사 패스 확정 → 격리 해제 → 재무장 허용
                KisPass();
                m.Pump();
                Assert.Equal(SessionRecoveryState.Connected, m.State);
                Assert.True(KisOpen());
                Assert.True(interlock.TryRearm(true, true));
                Assert.False(mask.IsRaised(ArmingInterlockSource.KisSession));
                Assert.Equal(1, m.Recoveries);
                _out.WriteLine($"[통합] 접속 {rt.Connects} · 단절 {rt.Disconnects} · 강제 종료 {rt.Aborts} · 재구독 {T(1).SentCount} · 대사 요청 {recon.ReconcileRequests}");
            }
            finally
            {
                cts.Cancel();
                await run;
            }
            Assert.Equal(2, rt.Connects);
        }

        [Fact]
        public async Task Integration_ApprovalOutage_ExhaustsAfterFiveReconnects_SessionKeepsTryingAtCap()
        {
            long micros = 1_000_000_000;
            var interlock = new KisSessionInterlock(null, 1, () => Volatile.Read(ref micros), silenceMicros: 3_000_000);
            using var adapter = new KisOpenApiAdapter(interlock);
            int exhausted = 0, keyCalls = 0;
            bool keyOk = true;
            var m = new SessionRecoveryManager(1, isolate: null, requestReconcile: null, reconciled: null, silent: interlock.SilentNow,
                                               onExhausted: () => Interlocked.Increment(ref exhausted), nowTicks: () => Volatile.Read(ref micros) * 10);
            var transports = new List<HalfOpenTransport>();
            var rt = new KisRealtimeSession(adapter, interlock, () => { var t = new HalfOpenTransport(); lock (transports) transports.Add(t); return t; },
                                            () => { Interlocked.Increment(ref keyCalls); return Volatile.Read(ref keyOk) ? "k" : null; }, "htsuser", vts: false, recovery: m,
                                            approvalBackoff: new KisApprovalBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4)));   // 발급 백오프 축소(펌프 간격 검증)
            m.BindAbort(rt.AbortConnection);
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => rt.RunAsync(cts.Token));
            try
            {
                Assert.True(SpinWait.SpinUntil(() => m.State == SessionRecoveryState.Connected, 10_000));
                Volatile.Write(ref keyOk, false);                                        // 접속키 발급 장애
                rt.AbortConnection();
                Assert.True(SpinWait.SpinUntil(() => m.State == SessionRecoveryState.Backoff, 10_000));
                for (int i = 0; i < 6; i++)
                {
                    long before = rt.ApprovalFailures;
                    Interlocked.Add(ref micros, 16_000_000);
                    m.Pump();
                    Assert.True(SpinWait.SpinUntil(() => rt.ApprovalFailures > before && m.State == SessionRecoveryState.Backoff, 10_000));
                }
                Assert.Equal(1, Volatile.Read(ref exhausted));
                Assert.True(m.Exhausted);
                Assert.True(rt.ApprovalFailures >= 6);                                   // 소진 후에도 16s 간격 재시도(가시성) — 격리는 래치
            }
            finally
            {
                cts.Cancel();
                await run;
            }
        }

        // ════════════ §6 호스트 결선 · 오중 빌드 회귀 ════════════

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_Wiring_RecoveryPumped_RearmHeld_AbortBound_KiwoomManual_FiveBuilds()
        {
            string root = RepoRoot();
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("try { kisRecovery?.Pump(); } catch { }", form);                                    // 50ms 평가 타이머 펌프
            Assert.Contains("try { kiwoomRecovery?.Pump(); } catch { }", form);
            Assert.Contains("kisSession.SetRearmHold(() => kisRecoveryRef.Held);", form);                        // 대사 전 재무장 거부
            Assert.Contains("kisRecovery.BindAbort(kisRealtime.AbortConnection);", form);                        // 침묵 강제 종료
            Assert.Contains("silent: kisSessionRef.SilentNow,", form);
            Assert.Contains("requestReconcile: () => openOrderRecon.RequestReconcile(MS2E.Infrastructure.OpenOrderVenueKind.Kis),", form);
            Assert.Contains("try { childMatrix.IsolateBroker(1); } catch { }", form);                             // 소진 = KIS 슬롯 격리
            Assert.Contains("linkProbe: () => bridge.IsProductionProxy, autoReconnect: false)", form);             // 키움 = 수동(자동 재기동 없음)
            Assert.Contains("Volatile.Write(ref limitRef, limitFeed);", form);
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

    /// <summary>미체결 대사기 테스트용 실행 원장 리그(OpenOrderReconciliationTests 와 같은 구성).</summary>
    internal static class OpenOrderRig
    {
        internal sealed class Exec
        {
            public long Now = 5_000_000;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Feed = null!;
        }

        internal static Exec MakeExec()
        {
            var e = new Exec();
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            e.Router = new ResilientOrderRouter(board, new BrokerFailoverSentinel(board, pendingOrders: pending), pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            e.Feed = new OrderExecutionFeedAdapter(e.Router, 64, 120_000_000, () => Volatile.Read(ref e.Now));
            return e;
        }
    }
}

// ============================================================================
// HotSwapTests — S19 #3: 원자 핫스왑 게이트웨이 검증 (23개)
//
//  §1 StrategySlot (11)     — 원자 스왑·세대·그레이스 권한 계층·동시성 파편화 0·Clear
//  §2 PromotionGateway (12) — FSM 전이·자율배포 도어락(서명)·변조 서명 경보·동결 차단
//                              ·잘못된 상태 거부·전체 생애주기·감사 추적
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class HotSwapTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;

        public HotSwapTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-promo.db");
            _db     = new LocalDatabase(_dbPath);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }

        // ── 빌더 ──────────────────────────────────────────────────────────────────
        private static StrategySpec Spec(string id) => new StrategySpec(
            id, id,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, 14), SpecOp.Lt, true, 30, null) },
            SpecLogic.And, new List<SpecCond>(), SpecLogic.And, 5, 10);

        // ════════════════════════ §1 StrategySlot ════════════════════════════════

        [Fact]
        public void Swap_IntoEmpty_ReturnsNull_NoGrace()
        {
            var slot = new StrategySlot();
            var s1   = Spec("s1");
            var prev = slot.Swap(s1);

            Assert.Null(prev);
            Assert.Same(s1, slot.Active);
            Assert.False(slot.InGraceWindow);
            Assert.Equal(1, slot.Generation);
        }

        [Fact]
        public void Swap_OverExisting_ReturnsPrev_EntersGrace()
        {
            var slot = new StrategySlot();
            var s1 = Spec("s1"); var s2 = Spec("s2");
            slot.Swap(s1);
            var prev = slot.Swap(s2);

            Assert.Same(s1, prev);
            Assert.Same(s2, slot.Active);
            Assert.Same(s1, slot.Draining);
            Assert.True(slot.InGraceWindow);
            Assert.Equal(2, slot.Generation);
        }

        [Fact]
        public void Generation_IsMonotonic()
        {
            var slot = new StrategySlot();
            slot.Swap(Spec("a")); slot.Swap(Spec("b")); slot.Swap(Spec("c"));
            Assert.Equal(3, slot.Generation);
        }

        [Fact]
        public void CompleteHandover_ClearsDraining_EndsGrace()
        {
            var slot = new StrategySlot();
            slot.Swap(Spec("s1"));
            slot.Swap(Spec("s2"));
            Assert.True(slot.InGraceWindow);

            slot.CompleteHandover();
            Assert.Null(slot.Draining);
            Assert.False(slot.InGraceWindow);
        }

        [Fact]
        public void Permission_ActiveInGrace_IsOpenOnly()
        {
            var slot = new StrategySlot();
            var s1 = Spec("s1"); var s2 = Spec("s2");
            slot.Swap(s1); slot.Swap(s2);

            Assert.Equal(SlotPermission.OpenOnly, slot.PermissionOf(s2));
            Assert.True(slot.CanEnter(s2));
            Assert.True(slot.CanExit(s2));
        }

        [Fact]
        public void Permission_DrainingInGrace_IsCloseOnly()
        {
            var slot = new StrategySlot();
            var s1 = Spec("s1"); var s2 = Spec("s2");
            slot.Swap(s1); slot.Swap(s2);

            Assert.Equal(SlotPermission.CloseOnly, slot.PermissionOf(s1));
            Assert.False(slot.CanEnter(s1));   // 구 전략은 신규 진입 차단
            Assert.True(slot.CanExit(s1));     // 잔여 포지션 청산만 허용
        }

        [Fact]
        public void Permission_AfterHandover_ActiveFull_DrainingDisabled()
        {
            var slot = new StrategySlot();
            var s1 = Spec("s1"); var s2 = Spec("s2");
            slot.Swap(s1); slot.Swap(s2);
            slot.CompleteHandover();

            Assert.Equal(SlotPermission.Full,     slot.PermissionOf(s2));
            Assert.Equal(SlotPermission.Disabled, slot.PermissionOf(s1));
        }

        [Fact]
        public void Permission_FirstSwap_IsFull_NoGrace()
        {
            var slot = new StrategySlot();
            var s1 = Spec("s1");
            slot.Swap(s1);
            Assert.Equal(SlotPermission.Full, slot.PermissionOf(s1));
        }

        [Fact]
        public void Permission_UnknownSpec_IsDisabled()
        {
            var slot = new StrategySlot();
            slot.Swap(Spec("active"));
            Assert.Equal(SlotPermission.Disabled, slot.PermissionOf(Spec("stranger")));
            Assert.Equal(SlotPermission.Disabled, slot.PermissionOf(null));
        }

        [Fact]
        public void Clear_EmptiesSlot()
        {
            var slot = new StrategySlot();
            slot.Swap(Spec("s1")); slot.Swap(Spec("s2"));
            slot.Clear();
            Assert.Null(slot.Active);
            Assert.Null(slot.Draining);
            Assert.False(slot.InGraceWindow);
        }

        [Fact]
        public void Concurrency_ReadDuringSwap_NeverTorn()
        {
            var slot  = new StrategySlot();
            var specs = new[] { Spec("s0"), Spec("s1"), Spec("s2"), Spec("s3") };
            var known = new HashSet<StrategySpec>(specs);
            slot.Swap(specs[0]);   // 초기 — 이후 Active 는 절대 null 아님

            var errors = new ConcurrentQueue<Exception>();
            var cts    = new CancellationTokenSource();
            var threads = new List<Thread>();

            // 독출 스레드 3개 — 매 읽기마다 완결된 알려진 참조여야 함
            for (int t = 0; t < 3; t++)
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var a = slot.Active;
                            if (a is null || !known.Contains(a))
                                throw new Exception("torn/unknown reference 관측");
                        }
                    }
                    catch (Exception e) { errors.Enqueue(e); }
                }));

            // 스왑 스레드 1개
            threads.Add(new Thread(() =>
            {
                try
                {
                    int i = 1;
                    while (!cts.IsCancellationRequested)
                    {
                        slot.Swap(specs[i % specs.Length]);
                        i++;
                    }
                }
                catch (Exception e) { errors.Enqueue(e); }
            }));

            foreach (var th in threads) th.Start();
            Thread.Sleep(150);
            cts.Cancel();
            foreach (var th in threads) th.Join(2000);

            Assert.Empty(errors);
            Assert.True(slot.Generation > 1);
            Assert.Contains(slot.Active!, known);
        }

        // ════════════════════════ §2 PromotionGateway ════════════════════════════

        [Fact]
        public void Submit_AtOrAboveThreshold_GoesPendingApproval()
        {
            var gw = new PromotionGateway();   // ChampionThreshold=0
            var st = gw.Submit(Spec("champ"), fitness: 0.8);
            Assert.Equal(PromotionState.PendingApproval, st);
        }

        [Fact]
        public void Submit_BelowThreshold_StaysCandidate()
        {
            var gw = new PromotionGateway { ChampionThreshold = 0.5 };
            var st = gw.Submit(Spec("weak"), fitness: 0.3);
            Assert.Equal(PromotionState.Candidate, st);
        }

        [Fact]
        public void Approve_ValidSignature_TransitionsToApproved()
        {
            var gw = new PromotionGateway(signatureValidator: s => s == "CMDR-7F3A");
            gw.Submit(Spec("c"), 0.9);
            bool ok = gw.Approve("CMDR-7F3A", "지휘관 승인");
            Assert.True(ok);
            Assert.Equal(PromotionState.Approved, gw.State);
        }

        [Fact]
        public void Approve_TamperedSignature_RejectedAndComplianceAlertRaised()
        {
            var gw = new PromotionGateway(_db, signatureValidator: s => s == "CMDR-7F3A");
            gw.Submit(Spec("c"), 0.9);

            bool ok = gw.Approve("FORGED");
            Assert.False(ok);
            Assert.Equal(PromotionState.PendingApproval, gw.State);   // 전이 거부 — 후보 보존

            // 컴플라이언스 경보 자율 발행 (PROMO-GATE)
            var alerts = _db.GetFraudAlerts(10);
            Assert.Contains(alerts, a => a.TransactionType == "PROMO-GATE");
        }

        [Fact]
        public void Approve_EmptySignature_Denied()
        {
            var gw = new PromotionGateway(_db);   // 기본: 비어있지 않은 서명 요구
            gw.Submit(Spec("c"), 0.9);
            Assert.False(gw.Approve("   "));
            Assert.Equal(PromotionState.PendingApproval, gw.State);
            Assert.Contains(_db.GetFraudAlerts(10), a => a.TransactionType == "PROMO-GATE");
        }

        [Fact]
        public void Approve_DuringEmergencyFreeze_Blocked()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("테스트 동결");
            var gw = new PromotionGateway(attestation: att, signatureValidator: s => s == "OK");
            gw.Submit(Spec("c"), 0.9);

            Assert.False(gw.Approve("OK"));        // fail-closed
            Assert.Equal(PromotionState.PendingApproval, gw.State);
        }

        [Fact]
        public void Approve_WrongState_Fails()
        {
            var gw = new PromotionGateway { ChampionThreshold = 0.5 };
            gw.Submit(Spec("weak"), 0.2);          // Candidate (임계 미달)
            Assert.False(gw.Approve("anything"));  // PendingApproval 아님
            Assert.Equal(PromotionState.Candidate, gw.State);
        }

        [Fact]
        public void Activate_AfterApproved_SwapsSlot_StateActive()
        {
            var slot = new StrategySlot();
            var gw   = new PromotionGateway(signatureValidator: s => s == "OK");
            var spec = Spec("winner");
            gw.Submit(spec, 0.9);
            gw.Approve("OK");

            Assert.True(gw.Activate(slot));
            Assert.Equal(PromotionState.Active, gw.State);
            Assert.Same(spec, slot.Active);
        }

        [Fact]
        public void Activate_WithoutApproval_Fails()
        {
            var slot = new StrategySlot();
            var gw   = new PromotionGateway();
            gw.Submit(Spec("c"), 0.9);             // PendingApproval (미승인)
            Assert.False(gw.Activate(slot));
            Assert.Null(slot.Active);
        }

        [Fact]
        public void Reject_FromPending_TransitionsToRejected()
        {
            var gw = new PromotionGateway();
            gw.Submit(Spec("c"), 0.9);
            gw.Reject("적응도 재검 실패");
            Assert.Equal(PromotionState.Rejected, gw.State);
            Assert.Null(gw.Current);
        }

        [Fact]
        public void Sunset_FromActive_TransitionsToSunsetted()
        {
            var slot = new StrategySlot();
            var gw   = new PromotionGateway(signatureValidator: s => s == "OK");
            gw.Submit(Spec("c"), 0.9);
            gw.Approve("OK");
            gw.Activate(slot);

            Assert.True(gw.Sunset("레짐 붕괴 — 안락사"));
            Assert.Equal(PromotionState.Sunsetted, gw.State);
        }

        [Fact]
        public void FullLifecycle_E2E_WithAuditTrail()
        {
            var slot = new StrategySlot();
            var gw   = new PromotionGateway(signatureValidator: s => s == "OK");
            var spec = Spec("lifecycle");

            Assert.Equal(PromotionState.PendingApproval, gw.Submit(spec, 0.95));
            Assert.True(gw.Approve("OK"));
            Assert.True(gw.Activate(slot));
            Assert.Same(spec, slot.Active);
            Assert.True(gw.Sunset("정상 일몰"));
            Assert.Equal(PromotionState.Sunsetted, gw.State);

            // 감사 추적: Submit·Nominate·Approve·Activate·Sunset 기록
            var actions = new HashSet<string>();
            foreach (var e in gw.AuditLog) actions.Add(e.Action);
            Assert.Contains("Approve",  actions);
            Assert.Contains("Activate", actions);
            Assert.Contains("Sunset",   actions);
        }
    }
}

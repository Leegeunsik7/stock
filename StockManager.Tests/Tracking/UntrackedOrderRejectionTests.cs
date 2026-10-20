// ============================================================================
// UntrackedOrderRejectionTests — [v12.18.40-DEV] 미추적 단일 주문 비동기 거부 = 비트 23(ChildSlotRejection) sticky 래치
//
//   §1 KIS: 실 KisOpenApiAdapter H0STCNI0 거부 통보(AES 암호문 · 거부 플래그 1) → 호스트와 같은 onFill 결선 → 그 프레임 처리 안에서 비트 23(0x00800000) Raise
//   §2 키움: 브리지 OrderRejected 페이로드(56B 신 · 48B 구 프레임 모두) → BridgeFillReader → 매트릭스 OnFill → 즉시 Raise
//   §3 판별: 미추적 접수/취소확인/체결/Legacy(모호) = 래치 0(freeze-storm 회피) · 추적 분할 주문 거부 경로 무변경(회수 + 래치) · 미추적 거부 ≠ 추적 슬롯 오염
//   §4 재무장: 물리 이중키(한쪽 = 거부) · 래치 중 거부도 계수 · 재무장 후 다음 거부 = 재래치
//   §5 δ=0: 미추적 거부 10만 통보(래치/재무장 순환 · 실 집계 마스크 결속) 힙 0B(3창 최솟값)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Tracking
{
    public sealed class UntrackedOrderRejectionTests
    {
        private const long Bit23Mask = 0x00800000L;
        private readonly ITestOutputHelper _out;

        public UntrackedOrderRejectionTests(ITestOutputHelper output) => _out = output;

        private static (ArmingInterlockBitmask mask, ChildSlotTrackingMatrix m) Matrix()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            var m = new ChildSlotTrackingMatrix(16, () => 1_000_000);
            m.BindInterlock(mask, ArmingInterlockSource.ChildSlotRejection);
            Assert.True(mask.Seal(out _));
            return (mask, m);
        }

        private static FillPayload Notice(long orderId, BridgeFillStatus status, long qty = 0, double px = 0)
            => new() { OrderId = orderId, SymbolCode = 5930, FillQty = qty, FillPrice = px, Side = 1, TimestampTicks = 7, Status = BridgeFillStatusCodec.Encode(status, false) };

        [Fact]
        public void Bit23_Is_The_Child_Slot_Rejection_Definition()
        {
            Assert.Equal(Bit23Mask, 1L << (int)ArmingInterlockSource.ChildSlotRejection);
            const long Defined = unchecked((long)0x800000001FFFFFFFUL);
            Assert.Equal(Bit23Mask, Defined & Bit23Mask);                                       // 30종 정의 내 재사용(정의 불변)
        }

        // ════════════ §1 KIS ════════════

        private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

        // H0STCNI0 평문(0 고객 · 1 계좌 · 2 주문번호 · 3 원주문 · 4 매도매수 · 5 정정구분 · 6 · 7 · 8 종목 · 9 체결량 · 10 체결가 · 11 시각 · 12 거부여부 · 13 체결여부 · 14 · 15 · 16 주문수량)
        private static string KisNotice(long odno, int refuse, int cntgYn)
            => string.Join('^', new[] { "cust01", "12345678", odno.ToString("D10"), "", "02", "0", "00", "0", "005930", "0", "0", "093001", refuse.ToString(), cntgYn.ToString(), "1", "91252", "10", "홍길동", "삼성전자" });

        private static byte[] KisFrame(string plain, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return U8("1|H0STCNI0|001|" + Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), iv, PaddingMode.PKCS7)));
        }

        [Fact]
        public void Kis_Async_Reject_Notice_For_Single_Order_Latches_Bit23_Within_The_Frame()
        {
            var (mask, m) = Matrix();
            byte[] key = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz012345"), iv = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP");
            var fills = new List<FillPayload>();
            using var kis = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: f => { fills.Add(f); m.OnFill(f); }, nowTicks: () => 7);
            kis.SetNoticeKeys(key, iv);

            Assert.True(kis.OnFrame(KisFrame(KisNotice(117_057, refuse: 0, cntgYn: 1), key, iv)));   // 접수 통보 = 래치 아님
            Assert.False(m.Tripped);
            Assert.Equal(0, mask.Mask & Bit23Mask);

            Assert.True(kis.OnFrame(KisFrame(KisNotice(117_058, refuse: 1, cntgYn: 1), key, iv)));   // ★단일 주문 비동기 거부(분할 아님 · 추적 슬롯 없음)
            Assert.Equal(BridgeFillStatus.Rejected, BridgeFillStatusCodec.Decode(fills[^1].Status, out _));
            Assert.Equal(KisOrderIdSpace.Encode(117_058), fills[^1].OrderId);
            Assert.True(m.Tripped);                                                             // 같은 프레임 처리 안에서 래치(1틱)
            Assert.Equal(Bit23Mask, mask.Mask & Bit23Mask);
            Assert.Equal(1, m.UntrackedRejects);
            Assert.Equal(1, m.RejectTrips);
        }

        [Fact]
        public void Host_Routes_Kis_And_Kiwoom_Notices_Into_The_Matrix()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("childMatrix.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.ChildSlotRejection);", src);
            Assert.Contains("Action<FillPayload> onChildFill = childMatrix.OnFill;", src);
            Assert.Contains("bridge.FillReceived += onChildFill;", src);                         // 키움 브리지(OrderRejected 포함) 통보
            Assert.Contains("onFill: f => { execFeed.OnFill(f); childMatrix.OnFill(f);", src);   // KIS 체결통보
        }

        // ════════════ §2 키움 ════════════

        private static byte[] Wire(in FillPayload f)
        {
            var b = new byte[Marshal.SizeOf<FillPayload>()];
            MemoryMarshal.Write(b, in f);
            return b;
        }

        [Fact]
        public void Kiwoom_Bridge_Reject_Frames_New_And_Legacy_Length_Latch_Bit23()
        {
            foreach (int len in new[] { -1, BridgeFillReader.LegacyFillBytes })                 // 56B 신 프레임 · 48B 구 프레임
            {
                var (mask, m) = Matrix();
                byte[] wire = Wire(Notice(42, BridgeFillStatus.Rejected));
                Assert.True(BridgeFillReader.TryReadFill(len < 0 ? wire : wire.AsSpan(0, len), out var fill));   // 브리지 Dispatch 와 같은 판독
                m.OnFill(fill);
                Assert.True(m.Tripped);
                Assert.Equal(Bit23Mask, mask.Mask & Bit23Mask);
                Assert.Equal(1, m.UntrackedRejects);
            }
        }

        // ════════════ §3 판별 ════════════

        [Fact]
        public void Untracked_Non_Reject_Notices_Never_Latch()
        {
            var (mask, m) = Matrix();
            m.OnFill(Notice(1, BridgeFillStatus.Accepted));
            m.OnFill(Notice(2, BridgeFillStatus.CancelConfirmed));
            m.OnFill(Notice(3, BridgeFillStatus.Filled, qty: 10, px: 70_000));
            m.OnFill(new FillPayload { OrderId = 4, SymbolCode = 5930, Status = 0 });           // Legacy(매직 없음 · 체결량 0 모호 통보)
            m.OnFill(new FillPayload { OrderId = 5, SymbolCode = 5930, Status = 3 });           // 매직 없는 '3' = Legacy(거부로 단정 안 함)
            Assert.False(m.Tripped);
            Assert.Equal(0, mask.Mask & Bit23Mask);
            Assert.Equal(5, m.UnknownFills);
            Assert.Equal(0, m.UntrackedRejects);
        }

        [Fact]
        public void Tracked_Split_Reject_Path_Unchanged_And_Untracked_Reject_Does_Not_Touch_Slots()
        {
            var (mask, m) = Matrix();
            long parent = 7;
            Span<ChildOrderPlan> kids = stackalloc ChildOrderPlan[3];
            for (int k = 0; k < 3; k++) kids[k] = new ChildOrderPlan { ChildId = ChildIdSpace.Compose(parent, k), ParentId = parent, Shares = 10, LimitRaw = 70_000, Broker = 0, Index = k };
            Assert.True(m.Open(parent, kids));
            m.MarkSent(kids[0].ChildId, 1001);

            m.OnFill(Notice(99_999, BridgeFillStatus.Rejected));                               // 다른 주문(미추적) 거부
            Assert.True(m.Tripped);
            Assert.True(m.TryGetParent(parent, out var snap));
            Assert.Equal((0, 0, ParentConvergence.Open), (snap.Rejected, snap.Reclaimed, snap.State));   // 추적 슬롯 무오염

            Assert.True(m.TryRearm(true, true));
            m.OnFill(Notice(1001, BridgeFillStatus.Rejected));                                 // 추적 자식 거부 = 기존 경로(형제 회수 + 래치)
            Assert.True(m.Tripped);
            Assert.True(m.TryGetParent(parent, out snap));
            Assert.Equal((1, 2), (snap.Rejected, snap.Reclaimed));
            Assert.Equal(1, m.UntrackedRejects);
            Assert.Equal(2, m.RejectTrips);
            Assert.Equal(Bit23Mask, mask.Mask & Bit23Mask);
        }

        // ════════════ §4 재무장 ════════════

        [Fact]
        public void Rearm_Requires_Both_Physical_Keys_And_Next_Reject_Relatches()
        {
            var (mask, m) = Matrix();
            m.OnFill(Notice(1, BridgeFillStatus.Rejected));
            m.OnFill(Notice(2, BridgeFillStatus.Rejected));                                    // 래치 중 거부도 계수
            Assert.Equal(2, m.UntrackedRejects);
            Assert.False(m.TryRearm(true, false));
            Assert.False(m.TryRearm(false, true));
            Assert.False(m.TryRearm(false, false));
            Assert.True(m.Tripped);
            Assert.Equal(Bit23Mask, mask.Mask & Bit23Mask);
            Assert.True(m.TryRearm(true, true));
            Assert.False(m.Tripped);
            Assert.Equal(0, mask.Mask & Bit23Mask);
            m.OnFill(Notice(3, BridgeFillStatus.Rejected));                                    // 재무장 후 다음 거부 = 즉시 재래치
            Assert.True(m.Tripped);
            Assert.Equal(Bit23Mask, mask.Mask & Bit23Mask);
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void HundredThousand_Untracked_Rejects_Allocate_Zero_Bytes()
        {
            var (mask, m) = Matrix();
            var notices = new FillPayload[4];
            for (int i = 0; i < notices.Length; i++) notices[i] = Notice(10_000 + i, i == 3 ? BridgeFillStatus.Accepted : BridgeFillStatus.Rejected);
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    m.OnFill(notices[i & 3]);
                    if ((i & 1023) == 0) m.TryRearm(true, true);
                }
            }
            Run();                                                                              // 예열(JIT)
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Run();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            _out.WriteLine($"[δ] 미추적 거부 10만 통보 × 3창 최소 = {min}B · 미추적 거부 {m.UntrackedRejects} · 래치 {m.RejectTrips}");
            Assert.Equal(0, min);
            Assert.Equal(4 * 75_000, m.UntrackedRejects);
            Assert.True(m.Tripped);
            Assert.Equal(Bit23Mask, mask.Mask & Bit23Mask);
        }
    }
}

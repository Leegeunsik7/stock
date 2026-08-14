// ============================================================================
// Phase110GenesisFleetTests — v11.0.0 Genesis Fleet Production 적대적 검증(100% 단언)
//   🔒 ★liveArmed:false 100% veto — 이중 물리 키 미결속이면 실물 발주 0비트(brokerSend 절대 미호출)
//   🚀 이중 키 결속 시 게이트 개통 실증(brokerSend 호출·스텁)
//   🧾 InFlightOrderReconciliationLedger(Think-Beyond): 중복 발주/phantom 미체결 격리·δ=0
//   ⚡ ExecutionTokenZeroizationGuard: 토큰 0x00 소멸·힙 오염 탐지·δ=0
//   📡 LiveExecutionTelemetryBridge: 체결 통보 δ=0 파싱·슬리피지/RTT
// ============================================================================
using System;
using System.Text;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase110GenesisFleetTests
    {
        // ══ 🔒 ★liveArmed:false — 실물 발주 100% VETO(캠페인 핵심 불변) ════
        [Fact]
        public void Execution_LiveArmedFalse_100Percent_Veto_NoRealDispatch()
        {
            // ★이중 물리 키 미결속(valve/arming false) → 모든 발주 veto·brokerSend 절대 미호출
            var unarmed = new GenesisLiveIgnitionGateway(valveWired: () => false, liveArmingWired: () => false, defensesPass: () => true);
            var ledger = new InFlightOrderReconciliationLedger();
            int sendCalls = 0;
            BrokerOrderSend send = (BrokerId b, in SealedOrderToken t) => sendCalls++;
            var adapter = new UniversalOrderExecutionAdapter(BrokerId.Toss, unarmed, ledger, send);

            for (int i = 0; i < 1000; i++)
            {
                var r = adapter.SendOrder(new ExecOrderRequest(BrokerId.Toss, 5930 + i, OrderSide.Buy, ExecOrderKind.Limit, 70000, 10));
                Assert.False(r.Accepted);          // ★모든 발주 거부
                Assert.NotEqual(0L, r.VetoCode);   // veto 사유 각인
            }
            Assert.Equal(0, sendCalls);            // ★★brokerSend(실 HTTP 발주) 단 1회도 미호출 — 실물 0비트
            Assert.Equal(0L, adapter.Dispatched);
            Assert.Equal(1000L, adapter.Vetoed);
        }

        [Fact]
        public void Execution_DualKeyArmed_Dispatches_Through_Gate()
        {
            // 이중 물리 키 결속 + 방어선 PASS + 키펄스 생존 → Emit 이 onSend 호출(게이트 개통 실증·brokerSend=스텁)
            var armed = new GenesisLiveIgnitionGateway(valveWired: () => true, liveArmingWired: () => true, defensesPass: () => true, keyPulseAlive: () => true);
            var ledger = new InFlightOrderReconciliationLedger();
            int sendCalls = 0;
            BrokerOrderSend send = (BrokerId b, in SealedOrderToken t) => sendCalls++;
            var adapter = new UniversalOrderExecutionAdapter(BrokerId.Toss, armed, ledger, send);

            var r = adapter.SendOrder(new ExecOrderRequest(BrokerId.Toss, 5930, OrderSide.Buy, ExecOrderKind.Limit, 70000, 10));
            Assert.True(r.Accepted);               // 무장 → 개통
            Assert.Equal(1, sendCalls);            // brokerSend 호출(게이트 하류)
            Assert.Equal(1L, adapter.Dispatched);
            Assert.True(r.ClOrdId > 0L);
        }

        [Fact]
        public void Execution_SendOrder_Veto_ZeroAllocation()
        {
            var unarmed = new GenesisLiveIgnitionGateway(() => false, () => false, () => true);
            var ledger = new InFlightOrderReconciliationLedger();
            var adapter = new UniversalOrderExecutionAdapter(BrokerId.Toss, unarmed, ledger, null);
            var req = new ExecOrderRequest(BrokerId.Toss, 5930, OrderSide.Buy, ExecOrderKind.Limit, 70000, 10);
            for (int i = 0; i < 50_000; i++) adapter.SendOrder(req);
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) adapter.SendOrder(req);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★veto 경로 핫패스 힙 0(ledger begin/abort·Emit veto·캐시 델리게이트)
        }

        // ══ 🧾 InFlightOrderReconciliationLedger (Think-Beyond) ════════════
        [Fact]
        public void Ledger_Dedup_Phantom_Reconcile()
        {
            var ledger = new InFlightOrderReconciliationLedger();
            long id1 = ledger.TryBeginOrder(5930, 10);
            Assert.True(id1 > 0L);                             // 첫 주문 → 전역 유니크 id
            Assert.Equal(-1L, ledger.TryBeginOrder(5930, 10)); // ★동일 종목 미해소 → -1(타임아웃 중복 방지)
            Assert.True(ledger.HasUnresolved(5930));
            Assert.True(ledger.TryBeginOrder(660, 20) > 0L);   // 다른 종목은 허용

            ledger.RecordFill(id1, 4);                         // 부분 체결
            ledger.NotePhantom(id1);                           // ★단선 → 잔여 phantom(불확정)
            Assert.Equal(-1L, ledger.TryBeginOrder(5930, 10)); // ★phantom 미해소 → 여전히 거부(유령 재발주 봉쇄)
            Assert.True(ledger.Phantoms >= 1L);

            Assert.True(ledger.Reconcile(id1));                // 확증 정산 → 해제
            Assert.False(ledger.HasUnresolved(5930));
            Assert.True(ledger.TryBeginOrder(5930, 10) > 0L);  // 해소 후 신규 허용
        }

        [Fact]
        public void Ledger_GlobalUnique_Ids_No_CrossAdapter_Mismatch()
        {
            // ★#1 교정: 원장이 전역 유니크 id 발급 → 다중 어댑터가 공유해도 슬롯 오매칭(살아있는 pending 오소거·중복발주) 없음.
            var ledger = new InFlightOrderReconciliationLedger();
            long a = ledger.TryBeginOrder(5930, 10);   // 어댑터A 종목 5930
            long b = ledger.TryBeginOrder(660, 20);    // 어댑터B 종목 660
            Assert.NotEqual(a, b);                     // ★전역 유니크(어댑터 인스턴스 무관)
            ledger.AbortOrder(b);                      // B veto → B 슬롯만 해제
            Assert.True(ledger.HasUnresolved(5930));   // ★A(5930) pending 유지(오소거 없음·중복발주 원천 봉쇄)
            Assert.False(ledger.HasUnresolved(660));
        }

        [Fact]
        public void Ledger_TryBegin_ZeroAllocation()
        {
            var ledger = new InFlightOrderReconciliationLedger();
            for (int i = 0; i < 10_000; i++) { long id = ledger.TryBeginOrder(5930, 10); ledger.Reconcile(id); }
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { long id = ledger.TryBeginOrder(5930, 10); ledger.Reconcile(id); }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★begin/reconcile 핫패스 힙 0(lock+고정 배열)
        }

        // ══ ⚡ ExecutionTokenZeroizationGuard ══════════════════════════════
        [Fact]
        public void ZeroGuard_LoadBearer_Zeroize_And_Contamination()
        {
            var g = new ExecutionTokenZeroizationGuard();
            Span<char> buf = stackalloc char[64];
            int n = g.LoadBearer("secret-token-abc", buf);
            Assert.True(n > 7);
            Assert.Equal('B', buf[0]);                     // "Bearer "
            Assert.Equal('r', buf[5]);
            g.ZeroizeAfterSend(buf);
            Assert.Equal((char)0, buf[0]);                 // ★0x00 소멸
            Assert.False(g.HeapContaminated);

            // ★소멸 없이 연속 로드 → 힙 오염 탐지
            g.LoadBearer("t1", buf);
            g.LoadBearer("t2", buf);
            Assert.True(g.HeapContaminated);               // loads(3) - zeroizations(1) = 2 > 1
        }

        [Fact]
        public void ZeroGuard_ZeroAllocation()
        {
            var g = new ExecutionTokenZeroizationGuard();
            Span<char> warm = stackalloc char[64];
            for (int i = 0; i < 10_000; i++) { g.LoadBearer("tok", warm); g.ZeroizeAfterSend(warm); }
            long Measure()
            {
                Span<char> buf = stackalloc char[64];
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { g.LoadBearer("secret-token", buf); g.ZeroizeAfterSend(buf); }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★토큰 주입/소멸 핫패스 힙 0(string 미생성)
        }

        // ══ 📡 LiveExecutionTelemetryBridge ════════════════════════════════
        [Fact]
        public void Bridge_Ingest_Slippage_And_Rtt()
        {
            var bridge = new LiveExecutionTelemetryBridge();
            // 주문가 70000, 체결가 70140 → 슬리피지 20bps, RTT = 150-100 = 50
            byte[] rpt = Encoding.UTF8.GetBytes("{\"clOrdId\":1,\"fillPrice\":70140,\"fillQty\":10,\"ts\":100,\"status\":\"filled\"}");
            Assert.True(bridge.IngestExecutionReport(rpt, orderPriceRaw: 70000, nowMicros: 150));
            Assert.Equal(1L, bridge.ExecReports);
            Assert.Equal(50L, bridge.LastRttMicros);
            Assert.Equal(20L, bridge.LastSlippageBps);     // (70140-70000)*10000/70000 = 20

            byte[] bad = Encoding.UTF8.GetBytes("{not json");
            Assert.False(bridge.IngestExecutionReport(bad, 70000, 0));
            Assert.True(bridge.ParseErrors >= 1L);
        }

        [Fact]
        public void Bridge_Ingest_ZeroAllocation()
        {
            var bridge = new LiveExecutionTelemetryBridge();
            byte[] rpt = Encoding.UTF8.GetBytes("{\"clOrdId\":1,\"fillPrice\":70140,\"fillQty\":10,\"ts\":100}");
            for (int i = 0; i < 10_000; i++) bridge.IngestExecutionReport(rpt, 70000, 150);
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) bridge.IngestExecutionReport(rpt, 70000, 150);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★체결 통보 파싱 핫패스 힙 0
        }
    }
}

// ============================================================================
// Phase103UniversalBrokerTests — v10.2.0 범용 다중 증권사 메쉬 적대적 검증(100% 단언)
//   🗺️ UniversalPortfolioMapper: 스키마 독립 δ=0 매핑(Toss/KIS 상이 스키마)·fail-closed
//   🧩 OpenApiPortfolioAdapter: read-only fail-closed veto·다중 스키마
//   🌐 UniversalBrokerGatewayMesh: 등록/조회/SyncAll/Seal·fail-closed·발주 veto
//   🔀 CrossBrokerFallbackSentinel: Primary→Secondary 원자 스위치·전소스 다운 fail-closed·δ=0
//   ⏱️ CrossBrokerMonotonicTimeFence(Think-Beyond): 융합 단조 보장·torn drift 탐지·δ=0
// ============================================================================
using System;
using System.Text;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase103UniversalBrokerTests
    {
        private static readonly byte[] TossJson = Encoding.UTF8.GetBytes(
            "{\"account\":{\"totalEval\":12345600,\"totalPnl\":234500,\"deposit\":5000000,\"buyingPower\":4800000}," +
            "\"holdings\":[{\"symbol\":\"005930\",\"qty\":10,\"avgPrice\":70000,\"curPrice\":72000,\"pnl\":20000,\"overseas\":false}]}");

        private static readonly byte[] KisJson = Encoding.UTF8.GetBytes(
            "{\"output2\":{\"tot_evlu_amt\":9999900,\"evlu_pfls_smtl_amt\":123400,\"dnca_tot_amt\":3000000,\"nrcvb_buy_amt\":2900000}," +
            "\"output1\":[{\"pdno\":\"005930\",\"hldg_qty\":10,\"pchs_avg_pric\":70000,\"prpr\":72000,\"evlu_pfls_amt\":20000,\"ovrs_yn\":\"N\"}," +
            "{\"pdno\":\"AAPL\",\"hldg_qty\":5,\"pchs_avg_pric\":180,\"prpr\":195,\"evlu_pfls_amt\":75,\"ovrs_yn\":\"Y\"}]}");

        // ══ 🗺️ UniversalPortfolioMapper — 스키마 독립 ══════════════════════
        [Fact]
        public void Mapper_Toss_And_Kis_Schemas()
        {
            Span<TossHolding> h = stackalloc TossHolding[16];
            Assert.True(UniversalPortfolioMapper.TryMap(TossJson, BrokerSchema.Toss(), 1L, out var toss, h, out int tn));
            Assert.Equal(BrokerId.Toss, toss.Broker);
            Assert.Equal(12345600L, toss.TotalEvalRaw);
            Assert.Equal(1, tn);
            Assert.False(h[0].IsOverseas);

            // ★동일 코드가 완전히 다른 KIS 스키마도 매핑(스키마 독립)
            Assert.True(UniversalPortfolioMapper.TryMap(KisJson, BrokerSchema.Kis(), 2L, out var kis, h, out int kn));
            Assert.Equal(BrokerId.Kis, kis.Broker);
            Assert.Equal(9999900L, kis.TotalEvalRaw);
            Assert.Equal(123400L, kis.TotalPnlRaw);
            Assert.Equal(2, kn);
            Assert.False(h[0].IsOverseas);   // ovrs_yn "N"
            Assert.True(h[1].IsOverseas);    // ovrs_yn "Y"
        }

        [Fact]
        public void Mapper_Malformed_FailClosed()
        {
            byte[] bad = Encoding.UTF8.GetBytes("{\"account\":{\"totalEval\":12,");
            Span<TossHolding> h = stackalloc TossHolding[16];
            Assert.False(UniversalPortfolioMapper.TryMap(bad, BrokerSchema.Toss(), 0L, out _, h, out int n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void Mapper_ZeroAllocation()
        {
            var schema = BrokerSchema.Kis();
            Span<TossHolding> warm = stackalloc TossHolding[16];
            for (int i = 0; i < 10_000; i++) UniversalPortfolioMapper.TryMap(KisJson, schema, 0L, out _, warm, out _);
            long Measure()
            {
                Span<TossHolding> hh = stackalloc TossHolding[16];
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) UniversalPortfolioMapper.TryMap(KisJson, schema, 0L, out _, hh, out _);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★스키마 독립 매핑 핫패스 힙 0
        }

        // ══ 🧩 OpenApiPortfolioAdapter ═════════════════════════════════════
        [Fact]
        public void Adapter_MultiSchema_ReadOnly_FailClosed()
        {
            var toss = new OpenApiPortfolioAdapter(BrokerSchema.Toss(), () => "tok", _ => TossJson, readOnlyKey: true);
            Assert.True(toss.SyncNow());
            Assert.True(toss.TryGetSummary(out var s));
            Assert.Equal(BrokerId.Toss, s.Broker);
            Assert.Equal(12345600L, s.TotalEvalRaw);

            var kis = new OpenApiPortfolioAdapter(BrokerSchema.Kis(), () => "tok", _ => KisJson, readOnlyKey: true);
            Assert.True(kis.SyncNow());
            Assert.True(kis.TryGetSummary(out var ks));
            Assert.Equal(BrokerId.Kis, ks.Broker);
            Assert.Equal(9999900L, ks.TotalEvalRaw);

            // read-only fail-closed + 발주 veto(liveArmed:false)
            var wr = new OpenApiPortfolioAdapter(BrokerSchema.Toss(), () => "tok", _ => TossJson, readOnlyKey: false);
            Assert.False(wr.SyncNow());
            Assert.True(wr.OrderVetoes >= 1L);
            Assert.False(toss.RejectOrderAttempt(0xBEEFL));
            Assert.True(toss.OrderVetoes >= 1L);
        }

        // ══ 🌐 UniversalBrokerGatewayMesh ══════════════════════════════════
        [Fact]
        public void Mesh_Register_SyncAll_Seal_FailClosed()
        {
            var mesh = new UniversalBrokerGatewayMesh();
            var toss = new OpenApiPortfolioAdapter(BrokerSchema.Toss(), () => "tok", _ => TossJson, true);
            var kis  = new OpenApiPortfolioAdapter(BrokerSchema.Kis(), () => "tok", _ => KisJson, true);
            mesh.Register(BrokerId.Toss, toss);
            mesh.Register(BrokerId.Kis, kis);
            Assert.Equal(2, mesh.Count);
            Assert.Same(toss, mesh.Get(BrokerId.Toss));
            Assert.Null(mesh.Get(BrokerId.Ls));                    // 미등록 → null(fail-closed)
            Assert.Equal(2, mesh.SyncAll());                      // 둘 다 동기화 성공
            mesh.Seal();
            Assert.Throws<InvalidOperationException>(() => mesh.Register(BrokerId.Ls, toss));   // 봉인 후 거부
            Assert.False(mesh.RejectOrderAttempt(BrokerId.Toss, 0L));   // ★발주 시도 하드 veto
            Assert.True(mesh.OrderVetoes >= 1L);
        }

        // ══ 🔀 CrossBrokerFallbackSentinel ═════════════════════════════════
        [Fact]
        public void Fallback_Switch_AllDown_FailClosed_Promote()
        {
            var s = new CrossBrokerFallbackSentinel(new[] { BrokerId.Toss, BrokerId.Kis });
            Assert.Equal(BrokerId.Toss, s.ActivePrimary);          // 첫 우선순위

            s.NoteHealth(BrokerId.Toss, false);                    // ★Primary 다운 → Kis 원자 스위치
            Assert.Equal(BrokerId.Kis, s.ActivePrimary);
            Assert.Equal(1L, s.Fallbacks);

            s.NoteRateLimit(BrokerId.Kis);                         // 둘 다 다운
            Assert.Equal(BrokerId.Kis, s.ActivePrimary);          // ★건강한 대안 없음 → 활성 유지(fail-closed·날조 안 함)

            s.NoteHealth(BrokerId.Toss, true);                     // Toss 복구 → ★자동 재조정으로 죽은 Kis 에서 이탈(#3 복구 에지)
            Assert.Equal(BrokerId.Toss, s.ActivePrimary);         // ★복구 에지 자동 스위치(죽은 primary 고착 방지)
            Assert.True(s.PromoteToPrimary(BrokerId.Toss));       // 멱등 승격
            Assert.Equal(BrokerId.Toss, s.ActivePrimary);
        }

        [Fact]
        public void Fence_Concurrent_StrictlyMonotonic_NoDuplicates()
        {
            var f = new CrossBrokerMonotonicTimeFence();
            const int threads = 4, per = 15_000;
            var all = new System.Collections.Concurrent.ConcurrentQueue<long>();
            System.Threading.Tasks.Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < per; i++)
                    all.Enqueue(f.Fence((BrokerId)((t & 1) + 1), i, (i * 13 + t) % 500));   // 역행 심한 recv
            });
            Assert.Equal(threads * per, all.Count);
            var set = new System.Collections.Generic.HashSet<long>(all);
            Assert.Equal(all.Count, set.Count);   // ★모든 unified 유일(중복/역행 emit 0 — CAS 단조 봉인·#1)
            Assert.Equal((long)(threads * per), f.Emitted);
        }

        [Fact]
        public void Fallback_NoteHealth_ZeroAllocation()
        {
            var s = new CrossBrokerFallbackSentinel(new[] { BrokerId.Toss, BrokerId.Kis, BrokerId.Ls });
            for (int i = 0; i < 10_000; i++) s.NoteHealth(BrokerId.Toss, (i & 1) == 0);
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 300_000; i++) s.NoteHealth(BrokerId.Toss, (i & 1) == 0);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★건강 갱신/스위치 핫패스 힙 0
        }

        // ══ ⏱️ CrossBrokerMonotonicTimeFence (Think-Beyond) ════════════════
        [Fact]
        public void Fence_Monotonic_And_TornDrift()
        {
            var f = new CrossBrokerMonotonicTimeFence(tornThresholdMicros: 1000);
            Assert.Equal(1000L, f.Fence(BrokerId.Toss, 100L, 1000L));
            Assert.Equal(1005L, f.Fence(BrokerId.Toss, 200L, 1005L));
            // ★다른 브로커 recv 가 역행(1002<1005) → 단조 보정(절대 역행 안 함)
            long t3 = f.Fence(BrokerId.Kis, 50L, 1002L);
            Assert.True(t3 > 1005L);          // 단조 유지
            Assert.Equal(1006L, t3);
            Assert.True(f.Corrections >= 1L);

            // ★브로커 거래소 시각 과도 역행 → torn drift 탐지
            var f2 = new CrossBrokerMonotonicTimeFence(tornThresholdMicros: 100);
            f2.Fence(BrokerId.Toss, 10000L, 1000L);
            f2.Fence(BrokerId.Toss, 5000L, 1001L);   // exchange 10000→5000(역행 5000 > 100)
            Assert.True(f2.TornDrifts >= 1L);
        }

        [Fact]
        public void Fence_ZeroAllocation_And_StrictMonotonic()
        {
            var f = new CrossBrokerMonotonicTimeFence();
            long recv = 0;
            for (int i = 0; i < 10_000; i++) f.Fence(BrokerId.Toss, i, recv++);
            long Measure()
            {
                long r = 100_000_000L;
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 300_000; i++)
                    f.Fence((BrokerId)((i & 1) + 1), i, (i % 3 == 0) ? r - 5L : r++);   // 의도적 역행 섞음
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★타임펜스 핫패스 힙 0(부동소수 0)
            // 엄격 단조: 연속 발행이 절대 역행 안 함(recv 역행에도)
            long a = f.Fence(BrokerId.Toss, 0L, 5L);
            long b2 = f.Fence(BrokerId.Kis, 0L, 3L);
            Assert.True(b2 > a);
        }
    }
}

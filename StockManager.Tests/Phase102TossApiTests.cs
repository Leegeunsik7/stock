// ============================================================================
// Phase102TossApiTests — v10.2.0 토스증권 Open API 연동 적대적 검증(100% 단언)
//   💠 TossJsonParser: δ=0 무할당 파싱·값 정확·fail-closed
//   💳 TossPortfolioSyncAdapter: read-only fail-closed veto·미인증 거부·발주 하드 거부(liveArmed:false)
//   📡 TossWebSocketMarketDataGateway: δ=0 IngestPacket·RTT·파싱실패 계수
//   🧵 SequenceContinuityGuardian(Think-Beyond): 연속/중복/갭/Stale·잠정 워터마크·확증 복구·δ=0
// ============================================================================
using System;
using System.Text;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase102TossApiTests
    {
        private static readonly byte[] AccountJson = Encoding.UTF8.GetBytes(
            "{\"account\":{\"totalEval\":12345600,\"totalPnl\":234500,\"deposit\":5000000,\"buyingPower\":4800000}," +
            "\"holdings\":[" +
            "{\"symbol\":\"005930\",\"qty\":10,\"avgPrice\":70000,\"curPrice\":72000,\"pnl\":20000,\"overseas\":false}," +
            "{\"symbol\":\"AAPL\",\"qty\":5,\"avgPrice\":180,\"curPrice\":195,\"pnl\":75,\"overseas\":true}]}");

        // ══ 💠 TossJsonParser ══════════════════════════════════════════════
        [Fact]
        public void Parser_Parses_Account_And_Holdings()
        {
            Span<TossHolding> h = stackalloc TossHolding[16];
            Assert.True(TossJsonParser.TryParseAccount(AccountJson, 999L, out var acc, h, out int n));
            Assert.Equal(12345600L, acc.TotalEvalRaw);
            Assert.Equal(234500L, acc.TotalPnlRaw);
            Assert.Equal(5000000L, acc.DepositRaw);
            Assert.Equal(4800000L, acc.BuyingPowerRaw);
            Assert.Equal(2, n);
            Assert.Equal(2, acc.HoldingCount);
            Assert.Equal(999L, acc.AsOfMicros);
            Assert.Equal(10L, h[0].Quantity);
            Assert.Equal(72000L, h[0].CurPriceRaw);
            Assert.False(h[0].IsOverseas);
            Assert.True(h[1].IsOverseas);            // AAPL 해외
            Assert.NotEqual(h[0].SymbolHash, h[1].SymbolHash);   // 종목코드 해시 구분
        }

        [Fact]
        public void Parser_Malformed_FailClosed()
        {
            byte[] bad = Encoding.UTF8.GetBytes("{\"account\":{\"totalEval\":12,");   // 절단
            Span<TossHolding> h = stackalloc TossHolding[16];
            Assert.False(TossJsonParser.TryParseAccount(bad, 0L, out _, h, out int n));   // ★파싱 실패 → false
            Assert.Equal(0, n);
        }

        [Fact]
        public void Parser_Handles_String_And_Decimal_Numbers()
        {
            // ★한국 증권사 REST 관례: 숫자를 문자열/소수로 반환 — GetInt64 예외 대신 정상 파싱(fail-closed 붕괴 방지·적대검증 #1)
            byte[] json = Encoding.UTF8.GetBytes(
                "{\"account\":{\"totalEval\":\"12345600\",\"totalPnl\":234500,\"deposit\":5000000,\"buyingPower\":4800000}," +
                "\"holdings\":[{\"symbol\":\"AAPL\",\"qty\":5,\"avgPrice\":180.50,\"curPrice\":\"195\",\"pnl\":72,\"overseas\":true}]}");
            Span<TossHolding> h = stackalloc TossHolding[16];
            Assert.True(TossJsonParser.TryParseAccount(json, 0L, out var acc, h, out int n));   // ★예외 없이 파싱
            Assert.Equal(12345600L, acc.TotalEvalRaw);   // 문자열 "12345600" → 12345600
            Assert.Equal(1, n);
            Assert.Equal(180L, h[0].AvgPriceRaw);        // 소수 180.50 → 절단 180
            Assert.Equal(195L, h[0].CurPriceRaw);        // 문자열 "195" → 195
            Assert.True(h[0].IsOverseas);
        }

        [Fact]
        public void Parser_ZeroAllocation()
        {
            Span<TossHolding> warm = stackalloc TossHolding[16];
            for (int i = 0; i < 10_000; i++) TossJsonParser.TryParseAccount(AccountJson, 0L, out _, warm, out _);
            long Measure()
            {
                Span<TossHolding> h = stackalloc TossHolding[16];
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) TossJsonParser.TryParseAccount(AccountJson, 0L, out _, h, out _);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★REST JSON 파싱 핫패스 힙 0(string 미생성·Utf8JsonReader)
        }

        // ══ 💳 TossPortfolioSyncAdapter (read-only fail-closed) ════════════
        [Fact]
        public void Adapter_Sync_ReadOnly_FailClosed_And_OrderVeto()
        {
            var adapter = new TossPortfolioSyncAdapter(() => "tok", _ => AccountJson, readOnlyKey: true);
            Assert.False(adapter.TryGetAccount(out _));           // 미동기화 → false(fail-closed)
            Assert.True(adapter.SyncNow());
            Assert.True(adapter.TryGetAccount(out var acc));
            Assert.Equal(12345600L, acc.TotalEvalRaw);
            Assert.True(adapter.Connected);
            Span<TossHolding> hs = stackalloc TossHolding[16];
            Assert.Equal(2, adapter.GetHoldings(hs));

            // ★조회 전용 키 아님 → 동기화 거부 + veto
            var writable = new TossPortfolioSyncAdapter(() => "tok", _ => AccountJson, readOnlyKey: false);
            Assert.False(writable.SyncNow());
            Assert.True(writable.OrderVetoes >= 1L);

            // 미인증(토큰 "") → 거부
            var noTok = new TossPortfolioSyncAdapter(() => "", _ => AccountJson, readOnlyKey: true);
            Assert.False(noTok.SyncNow());

            // 취득 실패(fetch null) → fail-closed
            var noFetch = new TossPortfolioSyncAdapter(() => "tok", _ => null, readOnlyKey: true);
            Assert.False(noFetch.SyncNow());

            // ★비인가 발주 시도 → 항상 하드 거부(liveArmed:false)
            long before = adapter.OrderVetoes;
            Assert.False(adapter.RejectOrderAttempt(0xDEADBEEFL));
            Assert.Equal(before + 1L, adapter.OrderVetoes);
        }

        // ══ 📡 TossWebSocketMarketDataGateway ══════════════════════════════
        [Fact]
        public void Gateway_Ingest_Tick_And_Rtt()
        {
            var guardian = new SequenceContinuityGuardian();
            var gw = new TossWebSocketMarketDataGateway(guardian);
            byte[] tick = Encoding.UTF8.GetBytes("{\"seq\":0,\"symbol\":\"005930\",\"price\":72000,\"qty\":10,\"ts\":100}");
            Assert.True(gw.IngestPacket(tick, nowMicros: 150));
            Assert.Equal(1L, gw.TicksIngested);
            Assert.Equal(72000L, gw.LatestPriceRaw);
            Assert.Equal(0L, gw.LatestSeq);
            Assert.Equal(50L, gw.LastRttMicros);          // 150 - 100

            byte[] bad = Encoding.UTF8.GetBytes("{not json");
            Assert.False(gw.IngestPacket(bad, 0));         // 파싱 실패 → false
            Assert.True(gw.ParseErrors >= 1L);
        }

        [Fact]
        public void Gateway_IngestPacket_ZeroAllocation()
        {
            var gw = new TossWebSocketMarketDataGateway(new SequenceContinuityGuardian());
            byte[] tick = Encoding.UTF8.GetBytes("{\"seq\":5,\"symbol\":\"005930\",\"price\":72000,\"qty\":10,\"ts\":100}");
            for (int i = 0; i < 10_000; i++) gw.IngestPacket(tick, 200);
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) gw.IngestPacket(tick, 200);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★WS 패킷 인제스트 핫패스 힙 0
        }

        // ══ 🧵 SequenceContinuityGuardian (Think-Beyond) ═══════════════════
        [Fact]
        public void Guardian_Continuity_Gap_Stale_Backfill()
        {
            var g = new SequenceContinuityGuardian(maxGap: 100);
            Assert.Equal(SeqObservation.Contiguous, g.Observe(0));   // 첫 관측=기준점
            Assert.Equal(SeqObservation.Contiguous, g.Observe(1));
            Assert.Equal(SeqObservation.Contiguous, g.Observe(2));
            Assert.True(g.IsContinuous);
            Assert.Equal(SeqObservation.Duplicate, g.Observe(1));    // 구식 → 멱등

            Assert.Equal(SeqObservation.RecoverableGap, g.Observe(10));   // expected=3, gap 7 ≤ 100, frontier=10
            Assert.True(g.IsProvisional);
            Assert.False(g.IsContinuous);                            // ★잠정 = 연속 아님(fail-closed)
            Assert.Equal(1L, g.HolesDetected);

            Assert.False(g.ConfirmBackfill(9));                      // ★frontier(10) 미달 → 잠정 유지(#3 부분 backfill 미해제)
            Assert.True(g.IsProvisional);
            Assert.True(g.ConfirmBackfill(10));                      // frontier 도달 → 확증 복구
            Assert.False(g.IsProvisional);
            Assert.Equal(1L, g.HolesRecovered);

            // ★초대형 갭 → Stale(복구 불가·날조 금지)
            var g2 = new SequenceContinuityGuardian(maxGap: 10);
            g2.Observe(0);
            Assert.Equal(SeqObservation.StaleGap, g2.Observe(1000)); // gap 999 > 10
            Assert.True(g2.IsStale);
            Assert.False(g2.ConfirmBackfill(500));                   // ★Stale 은 backfill 로 해제 불가(fail-closed)
        }

        [Fact]
        public void Guardian_Observe_ZeroAllocation()
        {
            var g = new SequenceContinuityGuardian();
            long seq = 0;
            for (int i = 0; i < 10_000; i++) g.Observe(seq++);
            long acc = 0;
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) if (g.Observe(seq++) == SeqObservation.Contiguous) acc++;
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★시퀀스 관측 핫패스 힙 0
            Assert.True(acc > 0);
        }
    }
}

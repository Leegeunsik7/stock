// ============================================================================
// ShadowMetricsBridgeTests — S21 #2: 그림자 추적기 + 단방향 밸브 검증 (12개)
//
//  §1 틱 미러링 (4)   — 링버퍼 미러·틱카운트·기준가추적·틱레이트
//  §2 실측 마찰 (2)   — 실 체결→센티넬 / 미지 심볼 무피드
//  §3 단방향 밸브 (4) — 물리차단 ack·실브로커 미호출·차단카운트·IsLive=false
//  §4 통합 (2)        — 라이브 게이트 논리완수+밸브차단 / Attach·Connected
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ShadowMetricsBridgeTests
    {
        private static TickPayload Tick(long sym, double price) => new TickPayload
        { SymbolCode = sym, Price = price, Volume = 100, ChangeRate = 0.0, TimestampTicks = 1 };

        private static FillPayload Fill(long sym, double price) => new FillPayload
        { OrderId = 1, SymbolCode = sym, FillPrice = price, FillQty = 10, Side = 1, Pad = 0, TimestampTicks = 1 };

        private static StrategySpec Spec(string id) => new StrategySpec(
            id, id,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, 14), SpecOp.Lt, true, 30, null) },
            SpecLogic.And, new List<SpecCond>(), SpecLogic.And, 5, 10);

        private static (LocalState, BrokerState) SyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal>()),
                new BrokerState(1_000_000m, new Dictionary<string, decimal>()));

        // ════════════════════════ §1 틱 미러링 ════════════════════════════════════

        [Fact]
        public void Ingest_MirrorsToRingBuffer()
        {
            var ring = new RingBufferCore<TickPayload>(64);
            var sm   = new ShadowMetricsBridge(ring);
            var c    = ring.CreateConsumer();

            sm.Ingest(Tick(5930, 70000));
            sm.Ingest(Tick(5930, 70100));
            sm.Ingest(Tick(660, 12000));

            Assert.True(c.TryRead(out var t0)); Assert.Equal(70000.0, t0.Price);
            Assert.True(c.TryRead(out var t1)); Assert.Equal(70100.0, t1.Price);
            Assert.True(c.TryRead(out var t2)); Assert.Equal(660, t2.SymbolCode);
            Assert.False(c.TryRead(out _));
        }

        [Fact]
        public void Ingest_CountsTicks()
        {
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64));
            for (int i = 0; i < 5; i++) sm.Ingest(Tick(5930, 70000 + i));
            Assert.Equal(5, sm.TicksReceived);
        }

        [Fact]
        public void Ingest_TracksLastPrice_ForFriction()
        {
            var sentinel = new SlippageSentinel();
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64), sentinel);
            sm.Ingest(Tick(5930, 70000));        // 기준가 등록
            sm.IngestFill(Fill(5930, 70140));    // 체결 vs 직전 시장가 → 실측 슬리피지
            Assert.Equal(1, sentinel.SampleCount);
        }

        [Fact]
        public void TicksPerSecond_PositiveAfterElapsed()
        {
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64));
            sm.Ingest(Tick(5930, 70000));
            Thread.Sleep(15);
            sm.Ingest(Tick(5930, 70010));
            sm.Ingest(Tick(5930, 70020));
            Assert.True(sm.TicksPerSecond > 0.0, $"rate={sm.TicksPerSecond}");
        }

        // ════════════════════════ §2 실측 마찰 ════════════════════════════════════

        [Fact]
        public void IngestFill_KnownSymbol_FeedsSentinel()
        {
            var sentinel = new SlippageSentinel();
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64), sentinel);
            sm.Ingest(Tick(5930, 70000));
            sm.IngestFill(Fill(5930, 70200));
            sm.IngestFill(Fill(5930, 69900));
            Assert.Equal(2, sentinel.SampleCount);
        }

        [Fact]
        public void IngestFill_UnknownSymbol_NoFeed()
        {
            var sentinel = new SlippageSentinel();
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64), sentinel);
            sm.IngestFill(Fill(9999, 70000));    // 직전 틱 없음 → 기준가 미상 → 미적재
            Assert.Equal(0, sentinel.SampleCount);
        }

        // ════════════════════════ §3 단방향 밸브 ══════════════════════════════════

        [Fact]
        public void Place_BlocksPhysicalOrder_ReturnsShadowAck()
        {
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64));
            var (ok, msg) = sm.Place(PaperAsset.Stock, "A005930", "삼성", PaperSide.Buy, PaperKind.Market, 100m, 0m, 70000m);
            Assert.True(ok);                       // 논리상 ack
            Assert.Contains("SHADOW", msg);        // 와이어 송신 0
            Assert.Equal(1, sm.BlockedOrders);
        }

        [Fact]
        public void Place_NeverCallsRealBroker()
        {
            var real = new RecBroker();
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64), null, real);
            sm.Place(PaperAsset.Stock, "A005930", "삼성", PaperSide.Buy, PaperKind.Market, 100m, 0m, 70000m);
            Assert.Equal(0, real.PlaceCount);      // 밸브가 위임 전에 차단
        }

        [Fact]
        public void Place_CountsBlockedOrders()
        {
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64));
            for (int i = 0; i < 3; i++)
                sm.Place(PaperAsset.Stock, "A005930", "삼성", PaperSide.Buy, PaperKind.Market, 10m, 0m, 70000m);
            Assert.Equal(3, sm.BlockedOrders);
        }

        [Fact]
        public void IsLive_IsFalse()
            => Assert.False(new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64)).IsLive);

        // ════════════════════════ §4 통합 ═════════════════════════════════════════

        [Fact]
        public void AsGateBroker_LiveMode_LogicCompletes_ButValveBlocksWire()
        {
            var spec = Spec("s");
            var slot = new StrategySlot(); slot.Swap(spec);
            var att  = new OrderAttestationService();
            var real = new RecBroker();
            var sm   = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64), null, real);
            // 게이트를 라이브 논리(IsDryRun=false)로 가동하되 브로커는 섀도우 밸브
            var gate = new AlgoExecutionGate(slot, att, sm) { IsDryRun = false };
            var (l, b) = SyncBooks();

            var v = gate.Submit(new OrderIntent(spec, "A005930", "삼성", PaperSide.Buy, PaperKind.Market, 100m, 70000m, true),
                                l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.Executed, v.Outcome);   // 전 논리 완수(실전처럼)
            Assert.Equal(1, sm.BlockedOrders);               // 그러나 밸브에서 와이어 차단
            Assert.Equal(0, real.PlaceCount);                // 실 브로커 미송신
            Assert.Equal(GatingMode.Normal, att.Mode);       // 정상(예외 아님)
        }

        [Fact]
        public void Attach_Detach_AndConnected_ReflectsProxy()
        {
            var sm = new ShadowMetricsBridge(new RingBufferCore<TickPayload>(64));
            var bridge = new KiwoomBridgeClient();   // 미연결

            var ex = Record.Exception(() => { sm.Attach(bridge); sm.Detach(); });
            Assert.Null(ex);
            sm.Attach(bridge);
            Assert.False(sm.Connected);              // 프록시 미연결 → 미연동
        }
    }

    file sealed class RecBroker : IBroker
    {
        public int PlaceCount;
        public string Name => "Rec"; public bool IsLive => false;
        public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide sd, PaperKind k, decimal q, decimal lp, decimal cp)
        { PlaceCount++; return (true, "ok"); }
        public void Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;
    }
}

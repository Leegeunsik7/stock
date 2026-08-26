// ============================================================================
// LiveBindingTests — S19 #5: 라이브 결속 + 3중 강철 안전 게이트 검증 (17개)
//
//  §1 실시간 마찰 피드 (2)  — SlippageSentinel.LastZ 가 적응도를 실시간으로 깎는지
//  §2 핫패스 스트레스 (1)   — 스왑 순간 1000+ 모의 틱 독출 정합성(토른 0)
//  §3 3중 강철 가드 (14)    — 드라이런·크기 클램프·디싱크 동결·권한·동결 전파
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class LiveBindingTests
    {
        // ── 빌더 ──────────────────────────────────────────────────────────────────
        private static StrategySpec Spec(string id) => new StrategySpec(
            id, id,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, 14), SpecOp.Lt, true, 30, null) },
            SpecLogic.And, new List<SpecCond>(), SpecLogic.And, 5, 10);

        private static List<StrategySpec> Seeds() => new()
        {
            new StrategySpec("seed", "씨앗",
                new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, 20)) },
                SpecLogic.And,
                new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossBelow, false, 0, new SpecTerm(SpecKind.Sma, 20)) },
                SpecLogic.And, 5, 10),
        };

        // 뚜렷한 진동파 — SMA5/SMA20 다중 교차 보장 → 다수 거래 발생
        private static List<Candle> Candles(int n = 80)
        {
            var list = new List<Candle>(n);
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 11000m + (decimal)(Math.Sin(i * 0.45) * 2000.0);
                list.Add(new Candle
                {
                    Date = start.AddDays(i),
                    Open = close, High = close * 1.01m, Low = close * 0.99m,
                    Close = close, Volume = 1000,
                });
            }
            return list;
        }

        private static FillPayload Fill(double price) => new FillPayload
        {
            OrderId = 1L, SymbolCode = 5930L, FillPrice = price,
            FillQty = 10L, Side = 1, TimestampTicks = 1L,
        };

        private static SlippageSentinel HighFrictionSentinel()
        {
            var s = new SlippageSentinel { AlertThresholdSigma = 2.0, ConsecutiveAlertCount = 1 };
            for (int i = 0; i < 6; i++) s.RecordFill(Fill(100000.0 + i), 100000.0);
            s.RecordFill(Fill(500000.0), 100000.0);   // 극단 이상치 → LastZ 매우 높음
            return s;
        }

        private static OrderIntent Order(StrategySpec spec, PaperSide side, decimal qty, decimal price, bool entry)
            => new OrderIntent(spec, "A005930", "삼성전자", side, PaperKind.Market, qty, price, entry);

        private static (LocalState, BrokerState) SyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal>()),
                new BrokerState(1_000_000m, new Dictionary<string, decimal>()));

        private static (LocalState, BrokerState) DesyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal> { ["A005930"] = 10m }),
                new BrokerState(1_000_000m, new Dictionary<string, decimal> { ["A005930"] = 9m }));

        // ════════════════════════ §1 실시간 마찰 피드 ═════════════════════════════

        [Fact]
        public void Sentinel_HighFriction_LowersChampionFitness_VsProxy()
        {
            // 거래하는 챔피언의 적응도가 표본수·낙폭 게이트로 0이 되지 않도록 경계 완화
            var bounds = new FitnessBounds { TradeFloor = 1, MddTolPct = 100 };

            var ghProxy = new GreenhouseService(new PromotionGateway(), fitBounds: bounds);
            ghProxy.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            ghProxy.RunCycleOnce();
            double fProxy = ghProxy.Probe().BestFitness;
            Assert.True(fProxy > 0, $"무마찰 챔피언 적응도가 양수여야 함: {fProxy}");

            var ghFric = new GreenhouseService(new PromotionGateway(), fitBounds: bounds)
            { Sentinel = HighFrictionSentinel() };
            ghFric.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            ghFric.RunCycleOnce();
            double fFric = ghFric.Probe().BestFitness;

            Assert.True(fFric < fProxy, $"실거래 마찰이 적응도를 깎아야 함: fFric={fFric} fProxy={fProxy}");
        }

        [Fact]
        public void NoSentinel_ProxyPath_IsDeterministic()
        {
            var gh1 = new GreenhouseService(new PromotionGateway());
            gh1.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            gh1.RunCycleOnce();

            var gh2 = new GreenhouseService(new PromotionGateway());
            gh2.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            gh2.RunCycleOnce();

            Assert.Equal(gh1.Probe().BestFitness, gh2.Probe().BestFitness, 9);
        }

        // ════════════════════════ §2 핫패스 스트레스 ══════════════════════════════

        [Fact]
        public void HotPath_1000TicksDuringSwap_NoTornRead()
        {
            var slot  = new StrategySlot();
            var specs = new[] { Spec("s0"), Spec("s1"), Spec("s2"), Spec("s3") };
            var known = new HashSet<StrategySpec>(specs);
            slot.Swap(specs[0]);   // 이후 Active 는 절대 null 아님

            var errors = new ConcurrentQueue<Exception>();
            var cts    = new CancellationTokenSource();

            // 스왑 스레드 — 인간 승인 순간의 Interlocked.Exchange 포인터 교체 모사
            var swapper = new Thread(() =>
            {
                try { int i = 1; while (!cts.IsCancellationRequested) { slot.Swap(specs[i % specs.Length]); i++; } }
                catch (Exception e) { errors.Enqueue(e); }
            });

            // 독출 스레드 4개 — 각 1000개 모의 틱(= 총 4000) Volatile 독출 정합성 검증
            var readers = new List<Thread>();
            for (int t = 0; t < 4; t++)
                readers.Add(new Thread(() =>
                {
                    try
                    {
                        for (int tick = 0; tick < 1000; tick++)
                        {
                            var a = slot.Active;
                            if (a is null || !known.Contains(a))
                                throw new Exception("torn/unknown reference 관측");
                        }
                    }
                    catch (Exception e) { errors.Enqueue(e); }
                }));

            swapper.Start();
            foreach (var r in readers) r.Start();
            foreach (var r in readers) r.Join(4000);
            cts.Cancel();
            swapper.Join(4000);

            Assert.Empty(errors);
            Assert.True(slot.Generation > 1);
            Assert.Contains(slot.Active!, known);
        }

        // ════════════════════════ §3 3중 강철 가드 ════════════════════════════════

        [Fact]
        public void Gate1_DryRunDefault_RecordsVirtual_NoBrokerCall()
        {
            var slot   = Slot(out var spec);
            var broker = new RecordingBroker();
            var gate   = new AlgoExecutionGate(slot, new OrderAttestationService(), broker);   // IsDryRun 기본 true
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 100m, 10000m, true), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.DryRunLogged, v.Outcome);
            Assert.Equal(0, broker.PlaceCount);          // 실 브로커 미발주
            Assert.Single(gate.VirtualLedger);
        }

        [Fact]
        public void Gate1_LiveMode_CallsBroker()
        {
            var slot   = Slot(out var spec);
            var broker = new RecordingBroker();
            var gate   = new AlgoExecutionGate(slot, new OrderAttestationService(), broker) { IsDryRun = false };
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 100m, 10000m, true), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.Executed, v.Outcome);
            Assert.Equal(1, broker.PlaceCount);
            Assert.Equal(100m, broker.LastQty);
        }

        [Fact]
        public void Gate2_ClampByAbsoluteCap()
        {
            var slot   = Slot(out var spec);
            var gate   = new AlgoExecutionGate(slot, new OrderAttestationService()) { MaxOrderAbsQty = 1000 };
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 5000m, 10000m, true), l, b, 1_000_000_000m);
            Assert.Equal(1000m, v.ApprovedQty);   // 5000 → 절대 하드캡 1000
        }

        [Fact]
        public void Gate2_ClampByAssetPercent()
        {
            var slot   = Slot(out var spec);
            var gate   = new AlgoExecutionGate(slot, new OrderAttestationService()) { MaxOrderPct = 0.10m };
            var (l, b) = SyncBooks();

            // 자산 10000 × 10% / 가격 1000 = 1주
            var v = gate.Submit(Order(spec, PaperSide.Buy, 1000m, 1000m, true), l, b, 10000m);
            Assert.Equal(1m, v.ApprovedQty);
        }

        [Fact]
        public void Gate2_ClampToZero_NoFill()
        {
            var slot   = Slot(out var spec);
            var gate   = new AlgoExecutionGate(slot, new OrderAttestationService());
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 1000m, 1000m, true), l, b, 100m);
            Assert.Equal(ExecOutcome.DryRunLogged, v.Outcome);
            Assert.Equal(0m, v.ApprovedQty);
            Assert.Empty(gate.VirtualLedger);     // 0주 → 기록 없음
        }

        [Fact]
        public void Gate3_Desync_FreezesAndRejects()
        {
            var slot   = Slot(out var spec);
            var att    = new OrderAttestationService();
            var gate   = new AlgoExecutionGate(slot, att);
            var (l, b) = DesyncBooks();   // 보유 10 vs 9 (1주 불일치)

            var v = gate.Submit(Order(spec, PaperSide.Buy, 100m, 10000m, true), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.RejectedDesync, v.Outcome);
            Assert.Equal(AlgoGateState.Frozen, gate.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 시스템까지 비상 동결 전파
        }

        [Fact]
        public void Gate3_InSync_Passes()
        {
            var slot   = Slot(out var spec);
            var att    = new OrderAttestationService();
            var gate   = new AlgoExecutionGate(slot, att);
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 100m, 10000m, true), l, b, 100_000_000m);

            Assert.NotEqual(ExecOutcome.RejectedDesync, v.Outcome);
            Assert.Equal(AlgoGateState.Armed, gate.State);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Gate0_AlreadyFrozen_RejectsImmediately()
        {
            var slot   = Slot(out var spec);
            var gate   = new AlgoExecutionGate(slot, new OrderAttestationService());
            gate.Freeze("선동결");
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 100m, 10000m, true), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedFrozen, v.Outcome);
        }

        [Fact]
        public void Permission_DrainingStrategy_CannotEnter()
        {
            var s1 = Spec("s1"); var s2 = Spec("s2");
            var slot = new StrategySlot();
            slot.Swap(s1); slot.Swap(s2);   // 그레이스 — s1=청산전용, s2=진입전용
            var gate = new AlgoExecutionGate(slot, new OrderAttestationService());
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(s1, PaperSide.Buy, 100m, 10000m, entry: true), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedUnauthorized, v.Outcome);   // 청산 전용은 진입 불가
        }

        [Fact]
        public void Permission_DrainingStrategy_CanExit()
        {
            var s1 = Spec("s1"); var s2 = Spec("s2");
            var slot = new StrategySlot();
            slot.Swap(s1); slot.Swap(s2);
            var gate = new AlgoExecutionGate(slot, new OrderAttestationService());
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(s1, PaperSide.Sell, 100m, 10000m, entry: false), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.DryRunLogged, v.Outcome);   // 청산 신호는 허용 → 가드 통과
        }

        [Fact]
        public void Freeze_PropagatesToSystemAttestation()
        {
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(Slot(out _), att);
            gate.Freeze("디싱크 모의");
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void State_ReflectsExternalAttestationFreeze()
        {
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(Slot(out _), att);
            Assert.Equal(AlgoGateState.Armed, gate.State);

            att.ForceFreeze("외부 동결");
            Assert.Equal(AlgoGateState.Frozen, gate.State);   // 게이트 자체 래치 없이도 시스템 동결 반영
        }

        [Fact]
        public void Submit_DuringExternalFreeze_Rejects()
        {
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(Slot(out var spec), att);
            att.ForceFreeze("외부 동결");
            var (l, b) = SyncBooks();

            var v = gate.Submit(Order(spec, PaperSide.Buy, 100m, 10000m, true), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedFrozen, v.Outcome);
        }

        [Fact]
        public void ClampOrderSize_PureFunction_Boundaries()
        {
            var gate = new AlgoExecutionGate(Slot(out _), new OrderAttestationService())
            { MaxOrderAbsQty = 1000, MaxOrderPct = 0.10m };

            Assert.Equal(1000m, gate.ClampOrderSize(5000m, 10000m, 1_000_000_000m)); // 절대캡
            Assert.Equal(1m,    gate.ClampOrderSize(1000m, 1000m, 10000m));          // 비율캡
            Assert.Equal(0m,    gate.ClampOrderSize(1000m, 1000m, 100m));            // 0 클램프
            Assert.Equal(50m,   gate.ClampOrderSize(50m,   0m,    0m));              // 가격 0 → 절대캡만
        }

        [Fact]
        public void Submit_NullArgs_Throws()
        {
            var gate = new AlgoExecutionGate(Slot(out _), new OrderAttestationService());
            var (l, b) = SyncBooks();
            Assert.Throws<ArgumentNullException>(() => gate.Submit(null!, l, b, 1m));
        }

        // ── 헬퍼: 활성 전략이 채워진 슬롯 ──────────────────────────────────────────
        private static StrategySlot Slot(out StrategySpec active)
        {
            active = Spec("active");
            var s = new StrategySlot();
            s.Swap(active);
            return s;
        }
    }

    // ── 발주 기록 테스트용 브로커 ──────────────────────────────────────────────────
    file sealed class RecordingBroker : IBroker
    {
        public int     PlaceCount;
        public decimal LastQty;
        public string  Name   => "Rec";
        public bool    IsLive => false;
        public (bool ok, string msg) Place(
            PaperAsset a, string s, string n, PaperSide sd, PaperKind k,
            decimal q, decimal lp, decimal cp)
        { PlaceCount++; LastQty = q; return (true, "ok"); }
        public void    Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;
    }
}

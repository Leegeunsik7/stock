// ============================================================================
// PlutoCognitiveTensorTests — v12.18.86-DEV: 36슬롯 인지 텐서 매트릭스 · 기동 즉시 웜스타트 회고 학습기 · 자산군 추천 투영 · 브리핑 매트릭스
//
// ① 셀 배치(36 국면 슬롯 + 3 자산 기준선) · 슬롯 독립 보정(국면 × 자산군 표본 격리) · 해석 순서(슬롯 → 기준선 → 전역) · 활성 국면 포인터
// ② 스웜 결선: 자산군 태그 아레나 회고 → (자산군 · 진입 시 국면) 슬롯 관측 · 태그 없는 아레나 = 텐서 무관측
// ③ 영속: (RegimeId, AssetClass) 복합 키 왕복 · 전역 레이크 격리 · 슬롯 손상/키 변조 롤백 · 슬롯당 8행 · v12.18.85 스키마 마이그레이션
// ④ 웜스타트: 로컬 캔들 → 자산군당 리플레이 100회 · 1초 내 · 섀도우 게이트 핫스왑 · 결정성 · NoData/Failed 정직 · 실 에피소드 무오염 · Candles 어댑터
// ⑤ 추천 엔진 자산군 독립 투영 · CLI 매트릭스 표(판독 전용 · 레거시 DB) · 호스트 결선
// ⑥ δ=0: 국면 전환 · 셀 핫스왑 중 Resolve 10만 회 = 0 B · KRX 정산(텐서 결선) 10만 틱 = 0 B
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Regime;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Cognitive.Valuation;
using StockManager.Tools;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoCognitiveTensorTests : IDisposable
    {
        private static readonly long T0 = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly long Sec = TimeSpan.TicksPerSecond;
        private const long Samsung = 5930;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-plutotensor-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "portfolio.db");

        public PlutoCognitiveTensorTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static PlutoPostMortem Loss(long seq) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange, Theses = CounterThesis.Overheat },
                ArenaTrigger.StopLoss, MarketRegime.ChoppyRange, -1000, true, Sec * 60, 0);

        private static PlutoPostMortem TrendWin(long seq) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryCost = 100_000, Regime = MarketRegime.TrendingBull },
                ArenaTrigger.TakeProfit, MarketRegime.TrendingBull, 1000, true, Sec * 60, 0);

        private static void Feed(PlutoCognitiveTensorLake t, PlutoAssetClass asset, int regime, int n, bool losses)
        {
            for (int i = 1; i <= n; i++)
                Assert.True(t.Observe(asset, regime, losses ? Loss(i) : TrendWin(i), 80, 0));
        }

        // ── ① 셀 배치 · 독립 보정 · 해석 ────────────────────────────────────────

        [Fact]
        public void Layout_Is_36_Regime_Slots_Plus_3_Asset_Baselines()
        {
            Assert.Equal(36, PlutoCognitiveTensorLake.Slots);
            Assert.Equal(39, PlutoCognitiveTensorLake.Cells);
            var seen = new HashSet<int>();
            for (int r = 1; r <= 12; r++)
                for (int a = 0; a < 3; a++)
                {
                    int cell = PlutoCognitiveTensorLake.CellOf((PlutoAssetClass)a, r);
                    Assert.True(seen.Add(cell));
                    Assert.Equal((r, (PlutoAssetClass)a), PlutoCognitiveTensorLake.KeyOf(cell));
                }
            for (int a = 0; a < 3; a++)
            {
                int b = PlutoCognitiveTensorLake.CellOf((PlutoAssetClass)a, 0);
                Assert.True(seen.Add(b));
                Assert.Equal((PlutoCognitiveTensorLake.BaselineRegime, (PlutoAssetClass)a), PlutoCognitiveTensorLake.KeyOf(b));
                Assert.Equal(b, PlutoCognitiveTensorLake.CellOf((PlutoAssetClass)a, -1));
                Assert.Equal(b, PlutoCognitiveTensorLake.CellOf((PlutoAssetClass)a, 13));    // 범위 밖 = 기준선
            }
            Assert.Equal(39, seen.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => PlutoCognitiveTensorLake.KeyOf(39));
        }

        [Fact]
        public void Slots_Calibrate_Independently_And_Resolve_Slot_Then_Baseline_Then_Global()
        {
            var t = new PlutoCognitiveTensorLake();
            Feed(t, PlutoAssetClass.Krx, 2, 40, losses: true);                   // 2008 금융위기 × KRX = 반대 논리 패착 → 엄격도 ↑
            Feed(t, PlutoAssetClass.Us, 5, 40, losses: false);                   // 2017 골디락스 × US = 추세 순응 승리 → 모멘텀 ↑ · 엄격도 ↓
            Assert.Equal(4, t.Recalibrate());                                    // 슬롯 2 + 기준선 2(KRX · US)
            Assert.Equal(0, t.Recalibrate());                                    // 새 표본 0 = 재보정 0

            Assert.True(t.TryRead(PlutoAssetClass.Krx, 2, out var krx2));
            Assert.Equal((1L, 0, 2), (krx2.Generation, krx2.MomentumTiltPct, krx2.DevilStrictnessPts));
            Assert.True(t.TryRead(PlutoAssetClass.Us, 5, out var us5));
            Assert.Equal((1L, 5, -2), (us5.Generation, us5.MomentumTiltPct, us5.DevilStrictnessPts));
            for (int r = 1; r <= 12; r++)
                for (int a = 0; a < 3; a++)
                {
                    if ((r, a) is (2, 0) or (5, 1)) continue;
                    Assert.True(t.TryRead((PlutoAssetClass)a, r, out var other));
                    Assert.Equal(0, other.Generation);                            // 나머지 34슬롯 = 무간섭
                }
            Assert.Equal(2, t.CalibratedSlots);

            var global = new PlutoCognitiveWeights(7, -20, 9);
            var w = global;
            Assert.Equal(PlutoTensorSource.Regime, t.Resolve(PlutoAssetClass.Krx, 2, ref w));
            Assert.Equal(2, w.DevilStrictnessPts);
            w = global;
            Assert.Equal(PlutoTensorSource.Baseline, t.Resolve(PlutoAssetClass.Krx, 7, ref w));   // 국면 7 슬롯 미보정 → KRX 기준선
            Assert.Equal((1L, 2), (w.Generation, w.DevilStrictnessPts));
            w = global;
            Assert.Equal(PlutoTensorSource.Global, t.Resolve(PlutoAssetClass.Upbit, 2, ref w));   // Upbit 학습 0 → 전역 그대로
            Assert.Equal(global.DevilStrictnessPts, w.DevilStrictnessPts);
            Assert.Equal(global.MomentumTiltPct, w.MomentumTiltPct);

            var c = t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 2));
            Assert.Equal((PlutoSlotMaturity.Calibrated, 40L), (c.Maturity, c.LifetimeEpisodes));
            Assert.Equal(PlutoSlotMaturity.Untrained, t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Upbit, 1)).Maturity);
        }

        [Fact]
        public void Maturity_Stages_And_Shadow_Gate_Rejection_Keep_Slot_Neutral()
        {
            var t = new PlutoCognitiveTensorLake();
            Feed(t, PlutoAssetClass.Upbit, 3, 7, losses: true);
            t.Recalibrate();
            Assert.Equal(PlutoSlotMaturity.Accumulating, t.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Upbit, 3)).Maturity);
            // 확신도 31 · 후보 엄격도 +2 → 홀드아웃 전량 '건너뛴 거래' → InsufficientHoldout = 핫스왑 거부
            for (int i = 1; i <= 33; i++) Assert.True(t.Observe(PlutoAssetClass.Upbit, 3, Loss(100 + i), 31, 0));
            t.Recalibrate();
            var cell = PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Upbit, 3);
            Assert.Equal(PlutoShadowReason.InsufficientHoldout, t.LastVerdict(cell).Reason);
            Assert.Equal(PlutoSlotMaturity.Calibratable, t.Cell(cell).Maturity);
            Assert.True(t.RejectedSwaps > 0);
            Assert.False(t.Observe(PlutoAssetClass.Krx, 1, default, 50, 0));     // 회고 없음 = 관측 0
        }

        [Fact]
        public void Activate_Publishes_Active_Regime_Pointer_And_ResolveActive_Follows()
        {
            var t = new PlutoCognitiveTensorLake();
            Feed(t, PlutoAssetClass.Us, 9, 40, losses: false);
            t.Recalibrate();
            Assert.Equal(0, t.ActiveRegime);
            t.Activate(9);
            t.Activate(9);
            Assert.Equal((9, 1L), (t.ActiveRegime, t.Activations));
            var w = default(PlutoCognitiveWeights);
            Assert.Equal(PlutoTensorSource.Regime, t.ResolveActive(PlutoAssetClass.Us, ref w));
            t.Activate(99);
            Assert.Equal(0, t.ActiveRegime);                                      // 범위 밖 = 판정 전
            w = default;
            Assert.Equal(PlutoTensorSource.Baseline, t.ResolveActive(PlutoAssetClass.Us, ref w));
        }

        // ── ② 스웜 결선 ──────────────────────────────────────────────────────

        private sealed class FakeArena
        {
            public readonly List<AiTradeRationale> Log = new();
            public long Seq;

            public int Copy(Span<AiTradeRationale> dst)
            {
                lock (Log)
                {
                    int n = Math.Min(Math.Min(Log.Count, 128), dst.Length);
                    for (int k = 0; k < n; k++) dst[k] = Log[Log.Count - n + k];
                    return n;
                }
            }

            public long Entry(int confidence)
            {
                var regime = new PlutoRegimeState { Regime = MarketRegime.ChoppyRange, Seen = 100 };
                var v = PlutoDevilsAdvocateFilter.Assess(confidence, 50, 10000, 1000, 1000, 0, 0);
                var g = PlutoGovernanceTag.ForEntry(in regime, in v, confidence * 100, PlutoDynamicSizer.MinWeightBp, 0);
                long s = ++Seq;
                lock (Log) Log.Add(new AiTradeRationale(s, Samsung, 1, 1, 70_000, Kst10.Ticks, ArenaTrigger.OrderbookImbalance, 0, confidence, 71_000, 69_000, 200, 0, 0, 0, 0, 0,
                    governance: in g));
                return s;
            }

            public void Exit(in PlutoPostMortem pm)
            {
                var regime = new PlutoRegimeState { Regime = MarketRegime.ChoppyRange, Seen = 100 };
                var g = PlutoGovernanceTag.ForExit(in regime, 0, in pm);
                long s = ++Seq;
                lock (Log) Log.Add(new AiTradeRationale(s, Samsung, -1, 1, 70_000, Kst10.Ticks, pm.ExitTrigger, 0, 0, 0, 0, 0, 0, 0, 0, 0, pm.TradePnl, governance: in g));
            }
        }

        [Fact]
        public void Swarm_Routes_Asset_Tagged_PostMortems_To_Regime_Slot_And_Baseline()
        {
            var weights = new PlutoCognitiveWeightSeqlock();
            var ing = new PlutoGlobalDataIngestor(() => T0);
            var mem = new PlutoEpisodicMemoryStore();
            mem.SeedDefaults();
            var regime = new PlutoRegimeMatcher();
            var tensor = new PlutoCognitiveTensorLake();
            var swarm = new PlutoContinuousLearningSwarm(ing, mem, new PlutoWeightCalibrator(), weights, () => T0, regime, tensor);
            var krx = new FakeArena();
            var legacy = new FakeArena();
            swarm.AddArena("krx", krx.Copy, PlutoAssetClass.Krx);
            swarm.AddArena("legacy", legacy.Copy);                                 // 자산군 태그 없음 = 전역 보정기만
            Assert.Same(tensor, swarm.Tensor);

            Assert.True(ing.Ingest("""{"vix":14,"us10yChangeBp":5,"dxyChangeBp":50,"usdKrwChangeBp":100,"equityTrendBp":600,"sentiment":50}"""u8));
            swarm.Step();                                                         // 국면 게시 → 활성 포인터
            Assert.True(regime.TryRead(out var m) && m.Found);
            Assert.Equal(m.CycleId, tensor.ActiveRegime);

            for (int i = 0; i < 12; i++) { krx.Exit(Loss(krx.Entry(80))); legacy.Exit(Loss(legacy.Entry(80))); }
            swarm.Step();
            var slot = tensor.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, m.CycleId));
            var baseline = tensor.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 0));
            Assert.Equal(12L, slot.SessionSamples);                               // 진입 시 국면 슬롯
            Assert.Equal(12L, baseline.SessionSamples);                           // 자산 기준선(전 표본)
            Assert.Equal(0L, tensor.Cell(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Us, m.CycleId)).SessionSamples);
            Assert.Equal(24L, swarm.Calibrator.Observed);                         // 전역 = 두 아레나 합
        }

        // ── ③ 영속 ───────────────────────────────────────────────────────────

        private static PlutoCheckpointManager Manager(IPlutoLearningStore store, PlutoCognitiveWeightSeqlock sl, PlutoCognitiveTensorLake? tensor, long now = 0) =>
            new(store, sl, () => 0, () => 3, () => now == 0 ? T0 : now, tensor);

        [Fact]
        public void Slots_Persist_By_Composite_Key_And_Restore_Independently_Without_Touching_Global_Lake()
        {
            using (var db = new LocalDatabase(DbPath))
            {
                var store = new PlutoLearningStore(db);
                var sl = new PlutoCognitiveWeightSeqlock();
                var t = new PlutoCognitiveTensorLake();
                using var m = Manager(store, sl, t);
                Assert.Equal(PlutoPersistState.Fresh, m.Restore());
                Feed(t, PlutoAssetClass.Krx, 2, 40, losses: true);
                Feed(t, PlutoAssetClass.Upbit, 11, 40, losses: false);
                Assert.True(t.Recalibrate() > 0);
                Assert.True(sl.TrySwap(new PlutoCognitiveWeights(0, 15, 4, 6000, 300, 20), true));
                Assert.True(m.Checkpoint());
                Assert.Equal(4, m.SlotSaves);                                     // 슬롯 2 + 기준선 2
                Assert.False(m.Checkpoint());                                     // 무변화 = 기록 0
                Assert.Single(store.LoadRecent(64));                          // 전역 레이크 = 슬롯 행 제외
                var slots = store.LoadSlots(8);
                Assert.Equal(4, slots.Count);
                Assert.All(slots, s => Assert.True(s.IsIntact && s.IsSlot));
                Assert.Contains(slots, s => s.RegimeId == 2 && s.AssetClass == (int)PlutoAssetClass.Krx && s.Episodes == 40);
                Assert.Contains(slots, s => s.RegimeId == PlutoCognitiveTensorLake.BaselineRegime && s.AssetClass == (int)PlutoAssetClass.Upbit);
            }
            SqliteConnection.ClearAllPools();
            using (var db = new LocalDatabase(DbPath))
            {
                var sl = new PlutoCognitiveWeightSeqlock();
                var t = new PlutoCognitiveTensorLake();
                using var m = Manager(new PlutoLearningStore(db), sl, t);
                Assert.Equal(PlutoPersistState.Restored, m.Restore());
                Assert.Equal(4, m.SlotsRestored);
                Assert.True(sl.TryRead(out var g) && g.MomentumTiltPct == 15);    // 전역 복원 불변
                Assert.True(t.TryRead(PlutoAssetClass.Krx, 2, out var k2));
                Assert.Equal((1L, 2), (k2.Generation, k2.DevilStrictnessPts));
                Assert.True(t.TryRead(PlutoAssetClass.Upbit, 11, out var u11));
                Assert.Equal((1L, 5), (u11.Generation, u11.MomentumTiltPct));
                Assert.Equal(40, t.LifetimeEpisodes(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 2)));
                Assert.True(t.TryRead(PlutoAssetClass.Us, 2, out var us2) && us2.Generation == 0);
                Assert.False(m.Checkpoint());                                     // 복원 직후 = 기록 0
            }
        }

        [Theory]
        [InlineData("MomentumTiltPct = 3")]                  // 체크섬 불일치
        [InlineData("AssetClass = 1")]                       // 슬롯 키 변조(자산군 체크섬 결속)
        [InlineData("Episodes = 'x'")]                       // 문자열 오염
        public void Corrupt_Latest_Slot_Row_Rolls_Back_To_Previous_Row_Of_Same_Slot(string tamper)
        {
            using var db = new LocalDatabase(DbPath);
            var store = new PlutoLearningStore(db);
            var w1 = new PlutoCognitiveWeights(1, 5, 0, 6000, 100, 20);
            var w2 = new PlutoCognitiveWeights(2, 10, 0, 6100, 100, 20);
            store.AppendSlot(PlutoLearningCheckpoint.SealSlot(4, PlutoAssetClass.Krx, 20, in w1, T0), 8);
            store.AppendSlot(PlutoLearningCheckpoint.SealSlot(4, PlutoAssetClass.Krx, 30, in w2, T0 + Sec), 8);
            long latest = db.QueryScalar<long>($"SELECT MAX(Id) FROM {PlutoLearningStore.Table}");
            db.Execute($"UPDATE {PlutoLearningStore.Table} SET {tamper} WHERE Id = @id", new() { ["@id"] = latest });
            var t = new PlutoCognitiveTensorLake();
            using var m = Manager(store, new PlutoCognitiveWeightSeqlock(), t);
            m.Restore();
            Assert.True(m.CorruptSkipped >= 1);
            Assert.True(t.TryRead(PlutoAssetClass.Krx, 4, out var w));
            Assert.Equal((1L, 5), (w.Generation, w.MomentumTiltPct));             // 같은 슬롯 이전 골든
            Assert.True(t.TryRead(PlutoAssetClass.Us, 4, out var us) && us.Generation == 0);
        }

        [Fact]
        public void Slot_Rows_Trim_Per_Slot_And_Global_Trim_Never_Deletes_Slot_Rows()
        {
            using var db = new LocalDatabase(DbPath);
            var store = new PlutoLearningStore(db);
            for (int i = 1; i <= 12; i++)
            {
                var w = new PlutoCognitiveWeights(i, 0, 0);
                store.AppendSlot(PlutoLearningCheckpoint.SealSlot(6, PlutoAssetClass.Us, i, in w, T0 + i), PlutoCheckpointManager.SlotKeep);
                store.AppendSlot(PlutoLearningCheckpoint.SealSlot(7, PlutoAssetClass.Us, i, in w, T0 + i), PlutoCheckpointManager.SlotKeep);
            }
            for (int i = 1; i <= 70; i++) store.Append(PlutoLearningCheckpoint.Seal(1, i, new PlutoCognitiveWeights(i, 0, 0), T0 + i), PlutoCheckpointManager.LakeCapacity);
            Assert.Equal(64L, db.QueryScalar<long>($"SELECT COUNT(*) FROM {PlutoLearningStore.Table} WHERE AssetClass = -1"));
            Assert.Equal(16L, db.QueryScalar<long>($"SELECT COUNT(*) FROM {PlutoLearningStore.Table} WHERE AssetClass >= 0"));
            var slots = store.LoadSlots(1);
            Assert.Equal(2, slots.Count);
            Assert.All(slots, s => Assert.Equal(12, s.Episodes));
            Assert.Throws<ArgumentException>(() => store.AppendSlot(PlutoLearningCheckpoint.Seal(1, 1, default, T0), 8));
        }

        [Fact]
        public void Legacy_Schema_Is_Migrated_And_Old_Global_Rows_Still_Restore()
        {
            var old = PlutoLearningCheckpoint.Seal(5, 77, new PlutoCognitiveWeights(3, 20, 4, 6000, 200, 30), T0);
            using (var conn = new SqliteConnection($"Data Source={DbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"CREATE TABLE {PlutoLearningStore.Table} (Id INTEGER PRIMARY KEY AUTOINCREMENT, RegimeId INTEGER NOT NULL, Episodes INTEGER NOT NULL,
Generation INTEGER NOT NULL, MomentumTiltPct INTEGER NOT NULL, DevilStrictnessPts INTEGER NOT NULL, ShadowWinRateBp INTEGER NOT NULL, ShadowMddBp INTEGER NOT NULL,
ShadowSamples INTEGER NOT NULL, CalibratedUtcTicks INTEGER NOT NULL, Checksum INTEGER NOT NULL);
INSERT INTO {PlutoLearningStore.Table} (RegimeId, Episodes, Generation, MomentumTiltPct, DevilStrictnessPts, ShadowWinRateBp, ShadowMddBp, ShadowSamples, CalibratedUtcTicks, Checksum)
VALUES (5, 77, 3, 20, 4, 6000, 200, 30, {T0}, {old.Checksum});";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var legacy = PlutoBriefingCli.LoadReadOnly(DbPath, out var noSlots);   // 판독 전용 = 레거시 SQL · 마이그레이션 0
            Assert.Single(legacy);
            Assert.True(legacy[0].IsIntact);
            Assert.Empty(noSlots);
            SqliteConnection.ClearAllPools();
            using var db = new LocalDatabase(DbPath);
            var sl = new PlutoCognitiveWeightSeqlock();
            using var m = Manager(new PlutoLearningStore(db), sl, new PlutoCognitiveTensorLake());
            Assert.Equal(PlutoPersistState.Restored, m.Restore());
            Assert.True(sl.TryRead(out var w));
            Assert.Equal((3L, 20, 4), (w.Generation, w.MomentumTiltPct, w.DevilStrictnessPts));
            Assert.Equal(-1L, db.QueryScalar<long>($"SELECT AssetClass FROM {PlutoLearningStore.Table} LIMIT 1"));
        }

        [Fact]
        public void Tensor_Requires_Tensor_Store_And_Slot_Load_Failure_Isolates_Tensor()
        {
            var plain = new PlainStore();
            Assert.Throws<ArgumentException>(() => new PlutoCheckpointManager(plain, new PlutoCognitiveWeightSeqlock(), () => 0, tensor: new PlutoCognitiveTensorLake()));
            var failing = new FailingSlotStore();
            var t = new PlutoCognitiveTensorLake();
            using var m = new PlutoCheckpointManager(failing, new PlutoCognitiveWeightSeqlock(), () => 0, () => 1, () => T0, t);
            Assert.Equal(PlutoPersistState.Fresh, m.Restore());                    // 전역 정상
            Assert.True(m.TensorStoreFailed);
            Feed(t, PlutoAssetClass.Krx, 1, 40, losses: true);
            t.Recalibrate();
            m.Checkpoint();
            Assert.Equal(0, failing.SlotAppends);                                 // 슬롯 판독 장애 기동 = 슬롯 기록 0
        }

        private class PlainStore : IPlutoLearningStore
        {
            public IReadOnlyList<PlutoLearningCheckpoint> LoadRecent(int max) => Array.Empty<PlutoLearningCheckpoint>();
            public void Append(in PlutoLearningCheckpoint checkpoint, int keep) { }
        }

        private sealed class FailingSlotStore : PlainStore, IPlutoTensorStore
        {
            public int SlotAppends;
            public IReadOnlyList<PlutoLearningCheckpoint> LoadSlots(int perSlot) => throw new IOException("slots");
            public void AppendSlot(in PlutoLearningCheckpoint checkpoint, int keepPerSlot) => SlotAppends++;
        }

        // ── ④ 웜스타트 ───────────────────────────────────────────────────────

        private sealed class MemoryCandles : IPlutoCandleSource
        {
            public readonly List<PlutoCandleSeries> Series = new();
            public bool Fail;
            public IReadOnlyList<PlutoCandleSeries> LoadRecent(int maxSymbolsPerAsset, int bars) => Fail ? throw new IOException("candles") : Series;
        }

        private static double[] Trend(int n, double start, double stepPct)
        {
            var c = new double[n];
            for (int i = 0; i < n; i++) c[i] = start * Math.Pow(1 + stepPct / 100, i);
            return c;
        }

        [Fact]
        public void WarmStart_Replays_100_Per_Asset_Within_1s_And_HotSwaps_Baseline_Without_Real_Episodes()
        {
            var t = new PlutoCognitiveTensorLake();
            var src = new MemoryCandles();
            src.Series.Add(new PlutoCandleSeries("005930", PlutoAssetClass.Krx, Trend(120, 70_000, 0.8)));
            src.Series.Add(new PlutoCandleSeries("000660", PlutoAssetClass.Krx, Trend(90, 150_000, 0.6)));
            src.Series.Add(new PlutoCandleSeries("AAPL", PlutoAssetClass.Us, Trend(120, 200, 0.5)));
            src.Series.Add(new PlutoCandleSeries("SHORT", PlutoAssetClass.Us, Trend(10, 50, 1)));   // MinBars 미만 = 제외
            var warm = new PlutoReplayWarmStarter(t, src);
            var sw = Stopwatch.StartNew();
            var r = warm.Run();
            sw.Stop();
            Assert.Equal(PlutoWarmStartState.Completed, r.State);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"웜스타트 {sw.ElapsedMilliseconds}ms");
            Assert.Equal(200, r.Simulations);                                     // KRX 100 + US 100 (Upbit 캔들 0 = 레인 없음)
            Assert.Equal(2, r.Lanes.Count);
            var krxLane = r.Lanes.Single(l => l.Asset == PlutoAssetClass.Krx);
            Assert.Equal((2, 100, 100), (krxLane.Series, krxLane.Simulations, krxLane.Wins));
            Assert.Equal(PlutoShadowReason.Passed, krxLane.Verdict.Reason);
            Assert.Equal(1, r.Lanes.Single(l => l.Asset == PlutoAssetClass.Us).Series);
            Assert.Equal(2, r.SwappedCells);

            var w = default(PlutoCognitiveWeights);
            Assert.Equal(PlutoTensorSource.Baseline, t.Resolve(PlutoAssetClass.Krx, 4, ref w));   // 20표본 대기 없이 즉시 웜 가중치
            Assert.Equal((1L, 5, -2), (w.Generation, w.MomentumTiltPct, w.DevilStrictnessPts));   // 상승 추세 리플레이 = 모멘텀 ↑ · 엄격도 ↓
            int baseCell = PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 0);
            Assert.Equal(0, t.LifetimeEpisodes(baseCell));                        // 합성 표본 = 실 에피소드 무오염
            Assert.Equal(1, t.Cell(baseCell).WarmStarts);
            Assert.Same(r, warm.Run());                                           // 1회만
            Assert.Same(r, warm.Report);
        }

        [Fact]
        public void WarmStart_Binds_Judged_Regime_Slot_Too()
        {
            var t = new PlutoCognitiveTensorLake();
            var src = new MemoryCandles();
            src.Series.Add(new PlutoCandleSeries("KRW-BTC", PlutoAssetClass.Upbit, Trend(120, 90_000_000, 1.0)));
            var r = new PlutoReplayWarmStarter(t, src, () => 12).Run();
            Assert.Equal((PlutoWarmStartState.Completed, 12, 2), (r.State, r.RegimeId, r.SwappedCells));
            Assert.True(t.TryRead(PlutoAssetClass.Upbit, 12, out var slot) && slot.Generation == 1);
            Assert.True(t.TryRead(PlutoAssetClass.Upbit, 0, out var b) && b.Generation == 1);
        }

        [Fact]
        public void WarmStart_No_Candles_Is_NoData_And_Source_Fault_Is_Failed_With_Zero_Swaps()
        {
            var t = new PlutoCognitiveTensorLake();
            var none = new PlutoReplayWarmStarter(t, new MemoryCandles()).Run();
            Assert.Equal((PlutoWarmStartState.NoData, 0, 0), (none.State, none.Simulations, none.SwappedCells));
            var failed = new PlutoReplayWarmStarter(t, new MemoryCandles { Fail = true }).Run();
            Assert.Equal(PlutoWarmStartState.Failed, failed.State);
            Assert.Equal("IOException", failed.Error);
            Assert.Equal(0, t.Swaps);
            Assert.Equal(PlutoWarmStartState.NotStarted, new PlutoReplayWarmStarter(t, new MemoryCandles()).Report.State);
        }

        [Fact]
        public void Replay_Is_Deterministic_And_Lessons_Track_Price_Path()
        {
            var series = new[] { new PlutoCandleSeries("A", PlutoAssetClass.Krx, Trend(60, 100, 0.7)), new PlutoCandleSeries("B", PlutoAssetClass.Krx, Trend(40, 100, -0.9)) };
            var a = new PlutoLearningSample[100];
            var b = new PlutoLearningSample[100];
            Assert.Equal(100, PlutoReplayWarmStarter.Replay(series, 3, a));
            Assert.Equal(100, PlutoReplayWarmStarter.Replay(series, 3, b));
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal((a[i].ReturnBp, a[i].Lesson, a[i].EntryConfidence), (b[i].ReturnBp, b[i].Lesson, b[i].EntryConfidence));
                Assert.Equal(3, a[i].StrictnessAtEntry);
            }
            Assert.Contains(a, s => s.Lesson == PlutoLesson.TrendFollow && s.IsWin);
            Assert.Contains(a, s => s.Lesson == PlutoLesson.RespectCounterThesis && !s.IsWin);   // 하락 추세 = 역추세 진입 패착

            var crash = Trend(30, 100, 0.5);
            for (int i = 20; i < 30; i++) crash[i] = crash[19] * 0.9;               // 20봉 −10% 급락
            var shock = PlutoReplayWarmStarter.Simulate(crash, 18, 5, 0);
            Assert.Equal((PlutoLesson.ExogenousShock, PostMortemVerdict.Loss), (shock.Lesson, shock.Verdict));
            Assert.Equal(0, PlutoReplayWarmStarter.Replay(new[] { new PlutoCandleSeries("S", PlutoAssetClass.Us, Trend(5, 1, 1)) }, 0, a));
            Assert.Throws<ArgumentOutOfRangeException>(() => PlutoReplayWarmStarter.Simulate(crash, 3, 5, 0));
        }

        [Fact]
        public void Local_Candle_Source_Classifies_Symbols_And_Skips_Corrupt_Bars()
        {
            Assert.Equal(PlutoAssetClass.Krx, PlutoLocalCandleSource.Classify("005930"));
            Assert.Equal(PlutoAssetClass.Krx, PlutoLocalCandleSource.Classify("035720.KQ"));
            Assert.Equal(PlutoAssetClass.Us, PlutoLocalCandleSource.Classify("BRK.B"));
            Assert.Equal(PlutoAssetClass.Us, PlutoLocalCandleSource.Classify("aapl"));
            Assert.Equal(PlutoAssetClass.Upbit, PlutoLocalCandleSource.Classify("KRW-BTC"));
            Assert.Null(PlutoLocalCandleSource.Classify("KRW-"));
            Assert.Null(PlutoLocalCandleSource.Classify("12345"));
            Assert.Null(PlutoLocalCandleSource.Classify("TOOLONGX"));
            Assert.Null(PlutoLocalCandleSource.Classify(" "));

            using var db = new LocalDatabase(DbPath);
            var empty = new PlutoLocalCandleSource(db).LoadRecent(32, 120);
            Assert.Empty(empty);                                                   // 캔들 0 = 빈 목록(가짜 시계열 0)
            db.RunInTransaction(() =>
            {
                foreach (var sym in new[] { "005930", "AAPL", "KRW-ETH", "??" })
                    for (int d = 0; d < 40; d++)
                        db.Execute("INSERT INTO Candles (Symbol, Date, Open, High, Low, Close, Volume) VALUES (@s, @d, 1, 1, 1, @c, 1)",
                            new() { ["@s"] = sym, ["@d"] = new DateTime(2026, 8, 1).AddDays(d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["@c"] = d == 5 ? -1.0 : 100.0 + d });
                db.Execute("INSERT INTO Candles (Symbol, Date, Close) VALUES ('000660', '2026-09-01', 10)");   // 봉 부족 = 제외
            });
            var series = new PlutoLocalCandleSource(db).LoadRecent(32, 30);
            Assert.Equal(3, series.Count);
            var krx = series.Single(s => s.Asset == PlutoAssetClass.Krx);
            Assert.Equal(30, krx.Closes.Length);
            Assert.Equal(139.0, krx.Closes[^1]);                                  // 오래된 순 · 최신 봉 마지막
            Assert.All(series, s => Assert.All(s.Closes, c => Assert.True(c > 0)));
            var full = new PlutoLocalCandleSource(db).LoadRecent(32, 120).Single(s => s.Asset == PlutoAssetClass.Us);
            Assert.Equal(39, full.Closes.Length);                                 // 음수 종가 봉 버림
        }

        // ── ⑤ 추천 · CLI · 호스트 ────────────────────────────────────────────

        [Fact]
        public void Recommendation_Engine_Projects_Asset_Specific_Weights_Independently()
        {
            var t = new PlutoCognitiveTensorLake();
            Feed(t, PlutoAssetClass.Krx, 1, 40, losses: false);
            t.Recalibrate();
            var global = new PlutoCognitiveWeights(4, -10, 6);
            var krx = PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Krx, in global, t);
            var us = PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Us, in global, t);
            var coin = PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Crypto, in global, t);
            Assert.Equal((5, -2), (krx.MomentumTiltPct, krx.DevilStrictnessPts));   // KRX 기준선 학습분
            Assert.Equal((-10, 6), (us.MomentumTiltPct, us.DevilStrictnessPts));    // US = 전역
            Assert.Equal((-10, 6), (coin.MomentumTiltPct, coin.DevilStrictnessPts));
            var none = PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Krx, in global, null);
            Assert.Equal(global.MomentumTiltPct, none.MomentumTiltPct);
            Assert.Equal(PlutoAssetClass.Upbit, PlutoRecommendationEngine.AssetOf(PlutoScreenMarket.Crypto));
        }

        [Fact]
        public void Cli_Prints_Tensor_Maturity_Matrix_ReadOnly()
        {
            string path = Path.Combine(_dir, "brief.db");
            using (var db = new LocalDatabase(path))
            {
                var store = new PlutoLearningStore(db);
                store.Append(PlutoLearningCheckpoint.Seal(2, 50, new PlutoCognitiveWeights(2, 10, 1, 6000, 500, 20), T0), 64);
                store.AppendSlot(PlutoLearningCheckpoint.SealSlot(2, PlutoAssetClass.Krx, 44, new PlutoCognitiveWeights(3, 10, 2, 6000, 100, 20), T0), 8);
                store.AppendSlot(PlutoLearningCheckpoint.SealSlot(8, PlutoAssetClass.Us, 7, default, T0), 8);
            }
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(path);
            var sw = new StringWriter();
            Assert.Equal(0, PlutoBriefingCli.Run(new[] { "--pluto-briefing", "--db", path }, sw));
            string o = sw.ToString();
            Assert.Contains("🧮 36슬롯 인지 텐서 매트릭스 — 12대 국면 × KRX/US/Upbit · 보정 슬롯 1/36", o);
            string row2 = o.Split('\n').Single(l => l.StartsWith(" 02 ", StringComparison.Ordinal));
            Assert.Contains(PlutoRegimeVectorLake.Get(2)!.Name, row2);
            Assert.Contains("| 1/3", row2);
            Assert.Contains("● G3 +10%/+2점", row2);
            Assert.Contains("◔ 표본 7", o.Split('\n').Single(l => l.StartsWith(" 08 ", StringComparison.Ordinal)));
            Assert.Contains("자산 기준선(국면 판정 전 폴백)", o);
            Assert.Contains("○ 표본 0", o);
            Assert.Contains("범례: ○ 미학습", o);
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(path));                        // 판독 전용 = 바이트 불변
        }

        [Fact]
        public void Host_Wires_Tensor_WarmStart_And_Asset_Tagged_Arenas()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string form = File.ReadAllText(Path.Combine(dir!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("plutoSwarm.AddArena(\"krx\", krxArena.CopyRationales, StockManager.Engines.Cognitive.Learning.PlutoAssetClass.Krx);", form);
            Assert.Contains("plutoSwarm.AddArena(\"crypto\", cryptoArena.CopyRationales, StockManager.Engines.Cognitive.Learning.PlutoAssetClass.Upbit);", form);
            Assert.Contains("cryptoArena.AttachTensor(plutoTensor);", form);
            Assert.Contains("tensor: plutoTensor);", form);
            Assert.Contains("new StockManager.Adapters.PlutoLocalCandleSource(db)", form);
            Assert.Contains("tensor: plutoTensorRef);", form);
            Assert.True(form.IndexOf("plutoCheckpoints.Restore();", StringComparison.Ordinal) < form.IndexOf("plutoWarmStart.StartAsync()", StringComparison.Ordinal));
            foreach (var f in new[] { "PlutoCognitiveTensorLake.cs", "PlutoReplayWarmStarter.cs", "PlutoTensorBriefing.cs" })
            {
                string code = string.Join("\n", File.ReadAllLines(Path.Combine(dir.FullName, "StockManager", "Engines", "Cognitive", "Learning", f))
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                Assert.DoesNotContain("QuantumVault", code);
                Assert.DoesNotContain("Http", code);
            }
        }

        // ── ⑥ δ=0 ────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_Resolve_Stays_ZeroAlloc_During_Regime_Switches_And_Slot_HotSwaps()
        {
            var t = new PlutoCognitiveTensorLake();
            for (int r = 1; r <= 12; r++) Feed(t, (PlutoAssetClass)(r % 3), r, 20, losses: (r & 1) == 0);
            t.Recalibrate();
            int stop = 0;
            var learner = new Thread(() =>
            {
                int k = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    var asset = (PlutoAssetClass)(k % 3);
                    int regime = 1 + k % 12;
                    t.Observe(asset, regime, (k & 1) == 0 ? Loss(k + 1) : TrendWin(k + 1), 80, 0);
                    t.Activate(regime);
                    if ((k & 15) == 0) t.Recalibrate();
                    k++;
                    Thread.Yield();
                }
            }) { IsBackground = true };
            long sink = 0;
            void Reads(int n)
            {
                var global = new PlutoCognitiveWeights(1, 3, 1);
                for (int i = 0; i < n; i++)
                {
                    var w = global;
                    sink += (int)t.Resolve((PlutoAssetClass)(i % 3), i % 13, ref w) + w.MomentumTiltPct + w.DevilStrictnessPts;
                    if (t.TryRead((PlutoAssetClass)(i % 3), t.ActiveRegime, out var a)) sink += a.DevilStrictnessPts;
                }
            }
            learner.Start();
            try
            {
                Reads(10_000);
                Assert.Equal(0, MinAllocated(() => Reads(100_000)));
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                learner.Join();
            }
            Assert.True(t.Swaps > 0);
            Assert.True(t.Activations > 1);
            Assert.NotEqual(long.MinValue, sink);
        }

        [Fact]
        public void Krx_Settle_With_Tensor_Attached_Stays_ZeroAlloc_While_Slots_HotSwap()
        {
            var e = new KrxArenaEngine();
            e.EnableTechnical(true);
            e.EnableMicrostructure(true);
            e.EnableGovernance(true);                                              // 거버너 가동 = 진입 판정마다 Govern → 텐서 Resolve
            var buffer = new PlutoValuationBuffer();
            new PlutoValuationEngine(buffer).SeedDefaults();
            e.AttachValuation(buffer);
            var lockW = new PlutoCognitiveWeightSeqlock();
            Assert.True(lockW.TrySwap(new PlutoCognitiveWeights(0, 10, 2), shadowPassed: true));
            e.AttachLearning(lockW);
            var regime = new PlutoRegimeMatcher();
            e.AttachRegimeMatcher(regime);
            var t = new PlutoCognitiveTensorLake();
            Feed(t, PlutoAssetClass.Krx, 0, 40, losses: true);
            t.Recalibrate();
            e.AttachTensor(t);
            Assert.True(e.TensorAttached);
            var kt = Kst10;
            int j = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, j++)
                {
                    kt = kt.AddTicks(Sec * 2);
                    if (kt >= Kst10.AddHours(5)) kt = Kst10;
                    long px = 68_000 + ((j >> 6) & 63) * 100 - (j & 1) * 100;
                    e.OnQuote(Samsung + (j & 3), px, px + 100, (j & 32) != 0 ? 9000 : 1000, 1000, 100 + (j & 63) * ((j & 1) + 1), kt);
                }
            }
            Settle(20_000);
            int stop = 0;
            var learner = new Thread(() =>
            {
                int k = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    t.Observe(PlutoAssetClass.Krx, 1 + k % 12, (k & 1) == 0 ? Loss(k + 1) : TrendWin(k + 1), 80, 0);
                    if ((k & 7) == 0) t.Recalibrate();
                    k++;
                    Thread.Yield();
                }
            }) { IsBackground = true };
            learner.Start();
            try
            {
                Assert.Equal(0, MinAllocated(() => Settle(100_000)));
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                learner.Join();
            }
        }
    }
}

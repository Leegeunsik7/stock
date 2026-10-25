// ============================================================================
// PlutoLearningPersistenceTests — v12.18.84-DEV: 플루토 학습 가중치 영속화 · 롤링 체크포인트 레이크 · 기동 안전 복원
//
// ① 체크포인트 봉인(체크섬) · 변조 감지 · seqlock TryRestore(학습 전만 · 세대 보존)
// ② LocalDatabase 왕복 · 재기동 정확 복원(가중치 · 세대 · 섀도우 · 평생 에피소드) · 무변화 기록 0 · 레이크 64행 유지
// ③ 손상 내성: 최신 행 변조/문자열 오염 → 직전 골든 롤백 · 전부 손상 → 중립 폴백 · 저장소 장애 → 중립 + 기록 중지 · 저장 실패 계수 후 재개 · 중복 복원 예외
// ④ 백그라운드 루프 · Dispose 최종 기록
// ⑤ δ=0: 백그라운드 SQLite 기록 중 체결 핫패스 가중치 판독 10만 회 = 0 B
// ⑥ 포트 투영 · DualArenaPage 영속화 뱃지 · 호스트 결선 · RCL 순수성
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Learning;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoLearningPersistenceTests : IDisposable
    {
        private static readonly long T0 = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc).Ticks;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-plutockp-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "portfolio.db");

        public PlutoLearningPersistenceTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private sealed class MemoryStore : IPlutoLearningStore
        {
            public readonly List<PlutoLearningCheckpoint> Rows = new();   // 최신 = 0
            public bool FailLoad, FailAppend;
            public int Appends;

            public IReadOnlyList<PlutoLearningCheckpoint> LoadRecent(int max)
            {
                if (FailLoad) throw new IOException("load");
                lock (Rows) return Rows.GetRange(0, Math.Min(max, Rows.Count));
            }

            public void Append(in PlutoLearningCheckpoint cp, int keep)
            {
                if (FailAppend) throw new IOException("append");
                lock (Rows)
                {
                    Rows.Insert(0, cp);
                    if (Rows.Count > keep) Rows.RemoveRange(keep, Rows.Count - keep);
                    Appends++;
                }
            }
        }

        private static PlutoCheckpointManager Manager(IPlutoLearningStore store, PlutoCognitiveWeightSeqlock sl, Func<long> observed, int regime = 3) =>
            new(store, sl, observed, () => regime, () => T0);

        // ── ① 봉인 · TryRestore ────────────────────────────────────────────────

        [Fact]
        public void Checkpoint_Seal_Is_Intact_And_Any_Field_Tamper_Is_Detected()
        {
            var w = new PlutoCognitiveWeights(7, 15, 4, 6200, 340, 40);
            var cp = PlutoLearningCheckpoint.Seal(5, 1234, in w, T0);
            Assert.True(cp.IsIntact);
            Assert.False(PlutoLearningCheckpoint.Corrupt.IsIntact);
            Assert.False(default(PlutoLearningCheckpoint).IsIntact);
            var tampered = new[]
            {
                new PlutoLearningCheckpoint(6, 1234, in w, T0, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1235, in w, T0, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1234, new PlutoCognitiveWeights(8, 15, 4, 6200, 340, 40), T0, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1234, new PlutoCognitiveWeights(7, 16, 4, 6200, 340, 40), T0, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1234, new PlutoCognitiveWeights(7, 15, 5, 6200, 340, 40), T0, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1234, new PlutoCognitiveWeights(7, 15, 4, 6201, 340, 40), T0, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1234, in w, T0 + 1, cp.Checksum),
                new PlutoLearningCheckpoint(5, 1234, in w, T0, cp.Checksum ^ 1),
            };
            foreach (var t in tampered) Assert.False(t.IsIntact);
            var outOfRange = new PlutoCognitiveWeights(1, 51, 0);                   // 범위 밖 = 체크섬 일치해도 불통과
            Assert.False(PlutoLearningCheckpoint.Seal(0, 1, in outOfRange, T0).IsIntact);
        }

        [Fact]
        public void Seqlock_TryRestore_Only_Before_Learning_And_Preserves_Generation()
        {
            var sl = new PlutoCognitiveWeightSeqlock();
            Assert.False(sl.TryRestore(new PlutoCognitiveWeights(3, 99, 0)));       // 무결성 위반 = 거부
            Assert.True(sl.TryRead(out var n) && n.IsNeutral && n.Generation == 0);
            Assert.True(sl.TryRestore(new PlutoCognitiveWeights(12, -20, 8, 5800, 210, 30)));
            Assert.True(sl.TryRead(out var r));
            Assert.Equal((12L, -20, 8, 5800, 210, 30), (r.Generation, r.MomentumTiltPct, r.DevilStrictnessPts, r.ShadowWinRateBp, r.ShadowMddBp, r.ShadowSamples));
            Assert.Equal(0, sl.Swaps);
            Assert.True(sl.TrySwap(new PlutoCognitiveWeights(0, -15, 8), shadowPassed: true));
            Assert.True(sl.TryRead(out var s));
            Assert.Equal(13, s.Generation);                                         // 복원 세대 +1 계승
            Assert.False(sl.TryRestore(new PlutoCognitiveWeights(2, 0, 0)));         // 학습된 라이브 덮어쓰기 0
            Assert.True(sl.TryRead(out var keep));
            Assert.Equal(-15, keep.MomentumTiltPct);
        }

        // ── ② LocalDatabase 왕복 · 재기동 ───────────────────────────────────────

        [Fact]
        public void Restart_Restores_Exact_Weights_And_Lifetime_Episodes()
        {
            long observed1 = 0;
            using (var db = new LocalDatabase(DbPath))
            {
                var sl = new PlutoCognitiveWeightSeqlock();
                using var m = Manager(new PlutoLearningStore(db), sl, () => Interlocked.Read(ref observed1));
                Assert.Equal(PlutoPersistState.Fresh, m.Restore());
                Assert.False(m.Checkpoint());                                       // 학습 전 중립 · 0 에피소드 = 기록 0
                observed1 = 42;
                Assert.True(sl.TrySwap(new PlutoCognitiveWeights(0, 25, -4, 6100, 450, 21), shadowPassed: true));
                Assert.True(sl.TrySwap(new PlutoCognitiveWeights(0, 30, -3, 6300, 400, 22), shadowPassed: true));
                Assert.True(m.Checkpoint());
                Assert.False(m.Checkpoint());                                       // 무변화 = 기록 0
                Assert.Equal(1, m.Saves);
                Assert.Equal(42, m.LifetimeEpisodes);
                Assert.Equal(1L, db.QueryScalar<long>($"SELECT COUNT(*) FROM {PlutoLearningStore.Table}"));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            long observed2 = 0;
            using (var db = new LocalDatabase(DbPath))                                 // 재기동
            {
                var sl = new PlutoCognitiveWeightSeqlock();
                using var m = Manager(new PlutoLearningStore(db), sl, () => Interlocked.Read(ref observed2));
                Assert.Equal(PlutoPersistState.Restored, m.Restore());
                Assert.True(sl.TryRead(out var w));
                Assert.Equal((2L, 30, -3, 6300, 400, 22), (w.Generation, w.MomentumTiltPct, w.DevilStrictnessPts, w.ShadowWinRateBp, w.ShadowMddBp, w.ShadowSamples));
                Assert.Equal(42, m.LifetimeEpisodes);
                observed2 = 8;
                Assert.Equal(50, m.LifetimeEpisodes);                               // 평생 누적 = 복원 기준 + 이번 기동
                Assert.True(m.Checkpoint());
                var rows = new PlutoLearningStore(db).LoadRecent(10);
                Assert.Equal(2, rows.Count);
                Assert.Equal(50, rows[0].Episodes);
                Assert.Equal(3, rows[0].RegimeId);
                Assert.True(rows[0].IsIntact);
                Assert.Throws<InvalidOperationException>(() => m.Restore());         // 중복 복원 = 예외
            }
        }

        [Fact]
        public void Lake_Keeps_Only_Latest_Capacity_Rows()
        {
            using var db = new LocalDatabase(DbPath);
            var sl = new PlutoCognitiveWeightSeqlock();
            long observed = 0;
            using var m = Manager(new PlutoLearningStore(db), sl, () => observed);
            m.Restore();
            for (int i = 1; i <= PlutoCheckpointManager.LakeCapacity + 36; i++) { observed = i; Assert.True(m.Checkpoint()); }
            Assert.Equal((long)PlutoCheckpointManager.LakeCapacity, db.QueryScalar<long>($"SELECT COUNT(*) FROM {PlutoLearningStore.Table}"));
            var rows = new PlutoLearningStore(db).LoadRecent(PlutoCheckpointManager.RestoreScan);
            Assert.Equal(PlutoCheckpointManager.LakeCapacity + 36, rows[0].Episodes);
            Assert.Equal(37, rows[^1].Episodes);
        }

        // ── ③ 손상 내성 ────────────────────────────────────────────────────────

        private long SeedTwo(LocalDatabase db)
        {
            var sl = new PlutoCognitiveWeightSeqlock();
            long observed = 10;
            using var m = Manager(new PlutoLearningStore(db), sl, () => observed);
            m.Restore();
            sl.TrySwap(new PlutoCognitiveWeights(0, 10, 2, 6000, 300, 20), true);
            Assert.True(m.Checkpoint());
            observed = 20;
            sl.TrySwap(new PlutoCognitiveWeights(0, 15, 3, 6100, 290, 25), true);
            Assert.True(m.Checkpoint());
            return db.QueryScalar<long>($"SELECT MAX(Id) FROM {PlutoLearningStore.Table}");
        }

        [Theory]
        [InlineData("MomentumTiltPct = 49")]                 // 체크섬 불일치(조용한 비트 변조)
        [InlineData("Episodes = 'garbage'")]                 // 문자열 오염(정수 아님)
        [InlineData("Checksum = 0")]                         // 체크섬 소거
        [InlineData("DevilStrictnessPts = 2.5")]             // 실수 오염
        public void Corrupt_Latest_Row_Rolls_Back_To_Previous_Golden(string tamper)
        {
            using var db = new LocalDatabase(DbPath);
            long latest = SeedTwo(db);
            db.Execute($"UPDATE {PlutoLearningStore.Table} SET {tamper} WHERE Id = @id", new() { ["@id"] = latest });
            var sl = new PlutoCognitiveWeightSeqlock();
            using var m = Manager(new PlutoLearningStore(db), sl, () => 0);
            Assert.Equal(PlutoPersistState.RolledBack, m.Restore());
            Assert.Equal(1, m.CorruptSkipped);
            Assert.True(sl.TryRead(out var w));
            Assert.Equal((1L, 10, 2), (w.Generation, w.MomentumTiltPct, w.DevilStrictnessPts));
            Assert.Equal(10, m.LifetimeEpisodes);
        }

        [Fact]
        public void All_Rows_Corrupt_Falls_Back_To_Neutral_And_Keeps_Persisting()
        {
            using var db = new LocalDatabase(DbPath);
            SeedTwo(db);
            db.Execute($"UPDATE {PlutoLearningStore.Table} SET Generation = 'x'");
            var sl = new PlutoCognitiveWeightSeqlock();
            long observed = 0;
            using var m = Manager(new PlutoLearningStore(db), sl, () => observed);
            Assert.Equal(PlutoPersistState.CorruptFallback, m.Restore());
            Assert.Equal(2, m.CorruptSkipped);
            Assert.True(sl.TryRead(out var w) && w.IsNeutral && w.Generation == 0);
            Assert.Equal(0, m.LifetimeEpisodes);
            observed = 3;
            Assert.True(m.Checkpoint());                                            // 새 골든 기록 재개(최신 무결 행 = 다음 복원 기준)
            var fresh = new PlutoCognitiveWeightSeqlock();
            using var m2 = Manager(new PlutoLearningStore(db), fresh, () => 0);
            Assert.Equal(PlutoPersistState.Restored, m2.Restore());
            Assert.Equal(3, m2.LifetimeEpisodes);
        }

        [Fact]
        public void Store_Failures_Fail_Safe()
        {
            var store = new MemoryStore { FailLoad = true };
            var sl = new PlutoCognitiveWeightSeqlock();
            long observed = 5;
            using (var m = Manager(store, sl, () => observed))
            {
                Assert.Equal(PlutoPersistState.StoreFailed, m.Restore());
                Assert.True(sl.TryRead(out var w) && w.IsNeutral);
                sl.TrySwap(new PlutoCognitiveWeights(0, 5, 1), true);
                Assert.False(m.Checkpoint());                                       // 평생 기준 미상 = 기록 0(누적 역행 방지)
            }
            Assert.Equal(0, store.Appends);                                         // Dispose 최종 기록도 0

            var store2 = new MemoryStore();
            var sl2 = new PlutoCognitiveWeightSeqlock();
            using var m2 = Manager(store2, sl2, () => observed);
            Assert.Equal(PlutoPersistState.Fresh, m2.Restore());
            store2.FailAppend = true;
            Assert.False(m2.Checkpoint());
            Assert.Equal(1, m2.SaveFailures);
            store2.FailAppend = false;
            Assert.True(m2.Checkpoint());                                           // 다음 주기 재개(자동 재시도 · 큐잉 0)
            Assert.Equal(1, store2.Appends);
        }

        [Fact]
        public void Unwritable_Database_Reports_Store_Failure_Not_Fabricated_State()
        {
            var db = new LocalDatabase(DbPath);
            var store = new PlutoLearningStore(db);
            db.Dispose();
            var sl = new PlutoCognitiveWeightSeqlock();
            using var m = Manager(store, sl, () => 0);
            Assert.Equal(PlutoPersistState.StoreFailed, m.Restore());
            Assert.True(sl.TryRead(out var w) && w.IsNeutral);
        }

        // ── ④ 백그라운드 루프 ──────────────────────────────────────────────────

        [Fact]
        public void Background_Loop_Persists_And_Dispose_Flushes_Final_State()
        {
            var store = new MemoryStore();
            var sl = new PlutoCognitiveWeightSeqlock();
            long observed = 0;
            var m = Manager(store, sl, () => Interlocked.Read(ref observed));
            m.Restore();
            m.Start(TimeSpan.FromMilliseconds(10));
            m.Start(TimeSpan.FromMilliseconds(10));                                 // 중복 기동 = 무시
            Interlocked.Exchange(ref observed, 11);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (m.Saves < 1 && DateTime.UtcNow < deadline) Thread.Sleep(5);
            Assert.True(m.Saves >= 1);
            Assert.True(m.Running);
            Interlocked.Exchange(ref observed, 12);
            sl.TrySwap(new PlutoCognitiveWeights(0, -10, 6), true);
            m.Dispose();
            Assert.False(m.Running);
            lock (store.Rows)
            {
                Assert.Equal(12, store.Rows[0].Episodes);
                Assert.Equal(-10, store.Rows[0].Weights.MomentumTiltPct);
            }
            Assert.Equal(0, m.Faulted);
            m.Dispose();                                                            // 멱등
        }

        // ── ⑤ δ=0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_Weight_Reads_During_Background_Db_Persistence_Are_Zero_Alloc()
        {
            using var db = new LocalDatabase(DbPath);
            var sl = new PlutoCognitiveWeightSeqlock();
            long observed = 0;
            using var m = Manager(new PlutoLearningStore(db), sl, () => Interlocked.Read(ref observed));
            m.Restore();
            using var stop = new ManualResetEventSlim(false);
            using var joined = new CountdownEvent(1);
            var writer = new Thread(() =>
            {
                try
                {
                    int k = 0;
                    while (!stop.IsSet)
                    {
                        int tilt = k % 51;                                          // 불변 관계: strictness = tilt % 31 − 10
                        sl.TrySwap(new PlutoCognitiveWeights(0, tilt, tilt % 31 - 10), shadowPassed: true);
                        Interlocked.Increment(ref observed);
                        m.Checkpoint();
                        k++;
                    }
                }
                finally { joined.Signal(); }
            }) { IsBackground = true };
            writer.Start();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (m.Saves < 3 && DateTime.UtcNow < deadline) Thread.SpinWait(64);
            long acc = 0, torn = 0;
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (!sl.TryRead(out var w)) continue;
                    if (w.DevilStrictnessPts != w.MomentumTiltPct % 31 - 10 && !w.IsNeutral) torn++;
                    acc += w.MomentumTiltPct;
                }
            }
            Reads(10_000);
            long bytes = MinAllocated(() => Reads(100_000));
            long savesDuring = m.Saves;
            stop.Set();
            Assert.True(joined.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, bytes);
            Assert.Equal(0, torn);
            Assert.True(savesDuring >= 3);                                         // 실제 SQLite 기록이 병행됨
            Assert.True(acc >= 0);
        }

        // ── ⑥ 포트 · UI · 호스트 ───────────────────────────────────────────────

        [Fact]
        public void Port_View_Persist_Badge_Texts()
        {
            Assert.Equal("[🧠 누적 학습 세션: 영속화 미결선]", UnwiredPlutoLearningPort.Instance.Current.PersistBadgeText);
            var v = new PlutoLearningView(true, PlutoLearningHealth.Live, null, -1, false, 2, 0, 0, 0, 0, 0, LifetimeEpisodes: 137, PersistState: PlutoLearningPersist.Active);
            Assert.Equal("[🧠 누적 학습 세션: 137회 누적 (영속화 가동 중)]", v.PersistBadgeText);
            Assert.Contains("직전 골든 복원", (v with { PersistState = PlutoLearningPersist.RolledBack }).PersistBadgeText);
            Assert.Contains("중립 가중치 폴백", (v with { PersistState = PlutoLearningPersist.NeutralFallback }).PersistBadgeText);
            Assert.Contains("저장소 장애", (v with { PersistState = PlutoLearningPersist.StoreFailed }).PersistBadgeText);
            Assert.Equal("[🧠 누적 학습 세션: 영속화 미결선]", (v with { PersistState = 99 }).PersistBadgeText);   // 미상 코드 = 미결선
            Assert.Equal(PlutoLearningPersist.Active, PlutoLearningPortAdapter.PersistCode(PlutoPersistState.Fresh));
            Assert.Equal(PlutoLearningPersist.Active, PlutoLearningPortAdapter.PersistCode(PlutoPersistState.Restored));
            Assert.Equal(PlutoLearningPersist.Off, PlutoLearningPortAdapter.PersistCode(PlutoPersistState.NotRestored));
        }

        [Fact]
        public void DualArenaPage_Renders_Persist_Badge_With_Lifetime_Episodes()
        {
            var weights = new PlutoCognitiveWeightSeqlock();
            var calibrator = new PlutoWeightCalibrator();
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), calibrator, weights, () => T0);
            var store = new MemoryStore();
            store.Rows.Add(PlutoLearningCheckpoint.Seal(-1, 251, new PlutoCognitiveWeights(4, 10, 2), T0));
            using var m = Manager(store, weights, () => calibrator.Observed);
            Assert.Equal(PlutoPersistState.Restored, m.Restore());
            swarm.Step();
            foreach (var (port, expected, css) in new (IPlutoLearningPort, string, string)[]
            {
                (new PlutoLearningPortAdapter(swarm, null, m), "[🧠 누적 학습 세션: 251회 누적 (영속화 가동 중)]", "on"),
                (new PlutoLearningPortAdapter(swarm), "[🧠 누적 학습 세션: 영속화 미결선]", "none"),
            })
            {
                using var ctx = new Bunit.TestContext();
                var krx = new KrxArenaEngine();
                ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, new KrxArenaQuoteFeeder(krx)));
                ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
                ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
                ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
                ctx.Services.AddSingleton(port);
                ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);
                var cut = ctx.RenderComponent<DualArenaPage>();
                var badge = cut.Find(".da-learn-p");
                Assert.Equal(expected, badge.TextContent.Trim());
                Assert.Equal(css, badge.GetAttribute("data-persist-learn"));
                Assert.Contains("학습 세대 4", cut.Find(".da-learn-w").TextContent);   // 복원 세대 렌더
            }
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var all = new string[parts.Length + 1];
            all[0] = dir!.FullName;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return File.ReadAllText(Path.Combine(all));
        }

        [Fact]
        public void Host_Restores_Before_Swarm_Start_And_Flushes_After_Swarm_Dispose()
        {
            string form = RepoFile("StockManager", "Blazor", "BlazorHomeForm.cs");
            int persist = form.IndexOf("cfg.GetBool(\"Pluto.LearningPersistEnabled\", true)", StringComparison.Ordinal);
            int restore = form.IndexOf("plutoCheckpoints.Restore();", StringComparison.Ordinal);
            int start = form.IndexOf("plutoSwarm.Start(TimeSpan.FromSeconds(5));", StringComparison.Ordinal);
            Assert.True(persist > 0 && restore > persist && start > restore);
            Assert.Contains("new StockManager.Adapters.PlutoLearningStore(db)", form);
            Assert.Contains("plutoSwarm.Dispose(); plutoCheckpoints?.Dispose();", form);
            Assert.DoesNotContain("QuantumVault", form.Substring(persist, restore - persist));
            foreach (var rel in new[] { new[] { "MS2E.UI.Portable", "Ports", "Trading", "PlutoLearningPort.cs" }, new[] { "MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor" } })
            {
                string src = RepoFile(rel);
                Assert.DoesNotContain("StockManager.Engines", src);
                Assert.DoesNotContain("Sqlite", src);
                Assert.DoesNotContain("LocalDatabase", src);
            }
            string engine = RepoFile("StockManager", "Engines", "Cognitive", "Learning", "PlutoCheckpointManager.cs");
            Assert.DoesNotContain("Sqlite", engine);
            Assert.DoesNotContain("QuantumVault\\", engine);                          // 헤더 불변식 문구 외 경로 참조 0
        }
    }
}

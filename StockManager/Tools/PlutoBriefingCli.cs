// ============================================================================
// PlutoBriefingCli — v12.18.85-DEV: 플루토 인지 학습 브리핑 원샷 CLI
//
//  사용(호스트 실행 파일 · UI/로그/뮤텍스 초기화 이전에 분기):
//    MS2E.StockSuite.exe --pluto-briefing [--db <portfolio.db 경로, 기본 LocalDatabase.DefaultPath>] [--top <1~5, 기본 3>]
//  종료 코드: 0 = 브리핑 출력(기록 없음 · 손상 폴백 포함 — 정직 표기) · 1 = 사용법 오류 · 2 = DB 판독 장애
//
//  ★운영 무접촉: portfolio.db 를 SQLite 판독 전용(Mode=ReadOnly)으로만 연다 — 스키마 생성 · 기록 · 마이그레이션 0(파일 부재 = 생성 안 함).
//    체크포인트 판정은 PlutoLearningStore.LoadRecentSql/ParseRows + PlutoCheckpointManager.Restore 와 동일(손상 = 직전 골든 롤백 · 전부 손상 = 중립).
//  ★정직 렌더: 원샷 = 학습 스웜 · 거시 피드 · 시세 피드 비가동 → 국면 유사도 = 직전 기록 국면명만(유사도 실관측 대기) · 추천 = '추천 엔진 미결선'(가짜 종목 0).
//  ★liveArmed:false — 네트워크 · 브로커 · 주문 경로 0 · QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Tools
{
    public static class PlutoBriefingCli
    {
        public const string Flag = "--pluto-briefing";

        public static bool IsInvocation(string[]? args) => args is { Length: > 0 } && Array.Exists(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

        public static int Run(string[] args, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            string dbPath = LocalDatabase.DefaultPath;
            int top = PlutoBriefingComposer.DefaultTopN;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--db", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length) { output.WriteLine("사용법: --pluto-briefing [--db <경로>] [--top <1~5>]"); return 1; }
                    dbPath = args[++i];
                }
                else if (string.Equals(args[i], "--top", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out top) || top is < 1 or > PlutoBriefingComposer.MaxTopN)
                    { output.WriteLine("사용법: --top 은 1~5"); return 1; }
                }
            }

            IReadOnlyList<PlutoLearningCheckpoint> rows;
            try { rows = LoadReadOnly(dbPath); }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                output.WriteLine("📊 플루토 인지 학습 브리핑 — 학습 기록 판독 장애(" + ex.GetType().Name + ") · 가짜 브리핑 0");
                return 2;
            }

            var view = Restore(rows);
            var service = new PlutoBriefingService(new SnapshotLearningPort(view), UnwiredPlutoRecommendationPort.Instance);
            output.WriteLine("원샷 CLI · 영속 학습 체크포인트 판독 전용(" + (File.Exists(dbPath) ? "기록 " + rows.Count.ToString(CultureInfo.InvariantCulture) + "행" : "DB 없음 · 첫 기동 전") + ") — 실시간 국면/추천은 앱 기동 후 DualArena [📊 인지 브리핑]");
            foreach (var line in service.Render(top)) output.WriteLine(line);
            return 0;
        }

        /// <summary>판독 전용 연결로 최신 체크포인트 레이크 판독(파일 · 테이블 부재 = 빈 목록 · 생성 0).</summary>
        public static IReadOnlyList<PlutoLearningCheckpoint> LoadReadOnly(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return Array.Empty<PlutoLearningCheckpoint>();
            var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
                probe.Parameters.AddWithValue("@t", PlutoLearningStore.Table);
                if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return Array.Empty<PlutoLearningCheckpoint>();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = PlutoLearningStore.LoadRecentSql;
            cmd.Parameters.AddWithValue("@n", PlutoCheckpointManager.RestoreScan);
            using var rdr = cmd.ExecuteReader();
            var dt = new DataTable();
            dt.Load(rdr);
            return PlutoLearningStore.ParseRows(dt);
        }

        /// <summary>체크포인트 → 브리핑 학습 상태(관리자 복원 판정 그대로 · 스웜 비가동 = Running false · 국면 = 직전 기록 국면명만).</summary>
        public static PlutoLearningView Restore(IReadOnlyList<PlutoLearningCheckpoint> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var seqlock = new PlutoCognitiveWeightSeqlock();
            var manager = new PlutoCheckpointManager(new SnapshotStore(rows), seqlock, static () => 0);
            var state = manager.Restore();   // Start/Dispose 0 → 기록 0
            if (!seqlock.TryRead(out var w)) w = default;
            string? regimeName = null;
            if (state is PlutoPersistState.Restored or PlutoPersistState.RolledBack)
                foreach (var cp in rows)
                    if (cp.IsIntact) { regimeName = PlutoRegimeVectorLake.Get(cp.RegimeId)?.Name; break; }
            return new PlutoLearningView(false, PlutoLearningHealth.NoData, null, -1, false,
                w.Generation, w.MomentumTiltPct, w.DevilStrictnessPts, 0, 0, 0,
                RegimeName: regimeName, MacroFeedText: "원샷 CLI · 거시 피드 비가동",
                LifetimeEpisodes: manager.LifetimeEpisodes, PersistState: rows.Count == 0 ? PlutoLearningPersist.Off : PlutoLearningPortAdapter.PersistCode(state),   // 기록 없음 = 영속화 미결선 표기
                Shadow: PlutoLearningPortAdapter.MapShadow(default, in w));
        }

        private sealed class SnapshotStore : IPlutoLearningStore
        {
            private readonly IReadOnlyList<PlutoLearningCheckpoint> _rows;
            public SnapshotStore(IReadOnlyList<PlutoLearningCheckpoint> rows) => _rows = rows;
            public IReadOnlyList<PlutoLearningCheckpoint> LoadRecent(int max) => _rows;
            public void Append(in PlutoLearningCheckpoint checkpoint, int keep) => throw new NotSupportedException("판독 전용 브리핑 — 기록 0");
        }

        private sealed class SnapshotLearningPort : IPlutoLearningPort
        {
            public SnapshotLearningPort(PlutoLearningView view) => Current = view;
            public bool IsWired => true;
            public PlutoLearningView Current { get; }
        }
    }
}

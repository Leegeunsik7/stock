// ============================================================================
// PlutoLearningStore — v12.18.84-DEV: IPlutoLearningStore 구현(LocalDatabase portfolio.db 전용 테이블 PlutoLearningCheckpoints · 콜드 경로 전용)
//
//   스키마(생성자 1회 · CREATE TABLE IF NOT EXISTS · 기존 테이블 무변경): Id 자동 증가(최신 판정 기준) · RegimeId · Episodes · Generation ·
//     MomentumTiltPct · DevilStrictnessPts · ShadowWinRateBp · ShadowMddBp · ShadowSamples · CalibratedUtcTicks · Checksum.
//   Append = 트랜잭션 1개(INSERT + 최근 keep 행 밖 DELETE) → 중간 중단 = 이전 레이크 그대로.
//   LoadRecent = Id 내림차순 · 정수 아닌 칸(외부 변조 · 손상) = SQL 단계 판정 열(전 칸 integer 여부) + 값 0 투영(열 타입 단일 = DataTable 추론 흔들림 0) = Corrupt 표식 → 관리자가 다음 무결 행으로 롤백.
//   [v12.18.85-DEV] LoadRecentSql · ParseRows 공개(인지 브리핑 CLI 가 판독 전용 연결로 같은 판정 재사용 · 스키마 생성/기록 0).
//   호출자 = PlutoCheckpointManager 백그라운드 루프 · 기동 복원만(체결 핫패스 · UI 스레드 호출 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using MS2E.Infrastructure;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Adapters
{
    public sealed class PlutoLearningStore : IPlutoLearningStore
    {
        public const string Table = "PlutoLearningCheckpoints";

        private static readonly string[] Columns =
        {
            "RegimeId", "Episodes", "Generation", "MomentumTiltPct", "DevilStrictnessPts", "ShadowWinRateBp", "ShadowMddBp", "ShadowSamples", "CalibratedUtcTicks", "Checksum",
        };

        private readonly LocalDatabase _db;

        public PlutoLearningStore(LocalDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _db.Execute($@"CREATE TABLE IF NOT EXISTS {Table} (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    RegimeId INTEGER NOT NULL, Episodes INTEGER NOT NULL, Generation INTEGER NOT NULL,
    MomentumTiltPct INTEGER NOT NULL, DevilStrictnessPts INTEGER NOT NULL,
    ShadowWinRateBp INTEGER NOT NULL, ShadowMddBp INTEGER NOT NULL, ShadowSamples INTEGER NOT NULL,
    CalibratedUtcTicks INTEGER NOT NULL, Checksum INTEGER NOT NULL)");
        }

        /// <summary>[v12.18.85-DEV] 최신 순 판독 SQL(파라미터 @n · 판독 전용 연결 재사용 — 인지 브리핑 CLI).</summary>
        public static readonly string LoadRecentSql =
            $"SELECT {string.Join(", ", Array.ConvertAll(Columns, c => $"CASE WHEN typeof({c}) = 'integer' THEN {c} ELSE 0 END"))}, "
            + $"({string.Join(" AND ", Array.ConvertAll(Columns, c => $"typeof({c}) = 'integer'"))}) FROM {Table} ORDER BY Id DESC LIMIT @n";

        public IReadOnlyList<PlutoLearningCheckpoint> LoadRecent(int max)
        {
            if (max <= 0) return Array.Empty<PlutoLearningCheckpoint>();
            return ParseRows(_db.Query(LoadRecentSql, new() { ["@n"] = max }));
        }

        /// <summary>[v12.18.85-DEV] LoadRecentSql 결과 → 체크포인트(해석 불가 행 = Corrupt 표식).</summary>
        public static IReadOnlyList<PlutoLearningCheckpoint> ParseRows(DataTable dt)
        {
            ArgumentNullException.ThrowIfNull(dt);
            var rows = new List<PlutoLearningCheckpoint>(dt.Rows.Count);
            foreach (DataRow r in dt.Rows) rows.Add(Parse(r));
            return rows;
        }

        private static PlutoLearningCheckpoint Parse(DataRow r)
        {
            if (!TryLong(r[Columns.Length], out long ok) || ok != 1) return PlutoLearningCheckpoint.Corrupt;
            Span<long> v = stackalloc long[10];
            for (int i = 0; i < v.Length; i++)
                if (!TryLong(r[i], out v[i])) return PlutoLearningCheckpoint.Corrupt;
            for (int i = 0; i < 8; i++)
                if (i != 1 && i != 2 && v[i] is < int.MinValue or > int.MaxValue) return PlutoLearningCheckpoint.Corrupt;
            var w = new PlutoCognitiveWeights(v[2], (int)v[3], (int)v[4], (int)v[5], (int)v[6], (int)v[7]);
            return new PlutoLearningCheckpoint((int)v[0], v[1], in w, v[8], v[9]);
        }

        private static bool TryLong(object o, out long value)
        {
            switch (o)
            {
                case long l: value = l; return true;
                case int i: value = i; return true;
                default: value = 0; return false;
            }
        }

        public void Append(in PlutoLearningCheckpoint cp, int keep)
        {
            var w = cp.Weights;
            var pars = new Dictionary<string, object>
            {
                ["@RegimeId"] = cp.RegimeId, ["@Episodes"] = cp.Episodes, ["@Generation"] = w.Generation,
                ["@MomentumTiltPct"] = w.MomentumTiltPct, ["@DevilStrictnessPts"] = w.DevilStrictnessPts,
                ["@ShadowWinRateBp"] = w.ShadowWinRateBp, ["@ShadowMddBp"] = w.ShadowMddBp, ["@ShadowSamples"] = w.ShadowSamples,
                ["@CalibratedUtcTicks"] = cp.CalibratedUtcTicks, ["@Checksum"] = cp.Checksum,
            };
            int k = Math.Max(1, keep);
            _db.RunInTransaction(() =>
            {
                _db.Execute($"INSERT INTO {Table} ({string.Join(", ", Columns)}) VALUES (@{string.Join(", @", Columns)})", pars);
                _db.Execute($"DELETE FROM {Table} WHERE Id NOT IN (SELECT Id FROM {Table} ORDER BY Id DESC LIMIT @k)", new() { ["@k"] = k });
            });
        }
    }
}

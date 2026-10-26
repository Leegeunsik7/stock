// ============================================================================
// PlutoLearningStore — v12.18.84-DEV: IPlutoLearningStore 구현(LocalDatabase portfolio.db 전용 테이블 PlutoLearningCheckpoints · 콜드 경로 전용)
//
//   스키마(생성자 1회 · CREATE TABLE IF NOT EXISTS · 기존 테이블 무변경): Id 자동 증가(최신 판정 기준) · RegimeId · Episodes · Generation ·
//     MomentumTiltPct · DevilStrictnessPts · ShadowWinRateBp · ShadowMddBp · ShadowSamples · CalibratedUtcTicks · Checksum.
//   Append = 트랜잭션 1개(INSERT + 최근 keep 행 밖 DELETE) → 중간 중단 = 이전 레이크 그대로.
//   LoadRecent = Id 내림차순 · 정수 아닌 칸(외부 변조 · 손상) = SQL 단계 판정 열(전 칸 integer 여부) + 값 0 투영(열 타입 단일 = DataTable 추론 흔들림 0) = Corrupt 표식 → 관리자가 다음 무결 행으로 롤백.
//   [v12.18.85-DEV] LoadRecentSql · ParseRows 공개(인지 브리핑 CLI 가 판독 전용 연결로 같은 판정 재사용 · 스키마 생성/기록 0).
//   [v12.18.86-DEV] 36슬롯 인지 텐서 = 같은 테이블 AssetClass 열((RegimeId, AssetClass) 복합 키 · 전역 행 = −1):
//     기존 DB = ALTER TABLE ADD COLUMN(기본 −1 → 기존 행 = 전역 · 체크섬 불변) · 슬롯 인덱스 · 전역 판독/정리 = 슬롯 행 제외(GlobalScope) ·
//     LoadSlots = 슬롯별 최신 perSlot 행(ROW_NUMBER 분할 · 자산군 칸 정수 판정 포함) · AppendSlot = INSERT + 같은 슬롯 keep 밖 DELETE(트랜잭션 1개).
//   호출자 = PlutoCheckpointManager 백그라운드 루프 · 기동 복원만(체결 핫패스 · UI 스레드 호출 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using MS2E.Infrastructure;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Adapters
{
    public sealed class PlutoLearningStore : IPlutoLearningStore, IPlutoTensorStore
    {
        public const string Table = "PlutoLearningCheckpoints";
        /// <summary>[v12.18.86-DEV] 전역 행 범위(유효 슬롯 행 = 정수 AssetClass ≥ 0 제외 · 자산군 칸 오염 행 = 전역 손상 표식으로 판독/정리).</summary>
        public const string GlobalScope = "NOT (typeof(AssetClass) = 'integer' AND AssetClass >= 0)";
        private const string SlotScope = "typeof(AssetClass) = 'integer' AND AssetClass >= 0";

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
    CalibratedUtcTicks INTEGER NOT NULL, Checksum INTEGER NOT NULL,
    AssetClass INTEGER NOT NULL DEFAULT -1)");
            if (!HasAssetClass(_db.Query($"SELECT name FROM pragma_table_info('{Table}')")))
                _db.Execute($"ALTER TABLE {Table} ADD COLUMN AssetClass INTEGER NOT NULL DEFAULT -1");   // v12.18.84~85 DB 마이그레이션(기존 행 = 전역)
            _db.Execute($"CREATE INDEX IF NOT EXISTS IX_{Table}_Slot ON {Table}(RegimeId, AssetClass, Id)");
        }

        /// <summary>[v12.18.86-DEV] pragma_table_info 결과에 AssetClass 열 존재 여부(판독 전용 CLI 와 공유).</summary>
        public static bool HasAssetClass(DataTable tableInfo)
        {
            ArgumentNullException.ThrowIfNull(tableInfo);
            foreach (DataRow r in tableInfo.Rows)
                if (string.Equals(r[0] as string, "AssetClass", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>[v12.18.85-DEV] 최신 순 판독 SQL(파라미터 @n · 판독 전용 연결 재사용 — 인지 브리핑 CLI).</summary>
        public static readonly string LoadRecentSql = SelectSql(Columns) + $" FROM {Table} WHERE {GlobalScope} ORDER BY Id DESC LIMIT @n";

        /// <summary>[v12.18.86-DEV] AssetClass 열 이전 DB(판독 전용 CLI · 마이그레이션 0) 전역 판독 SQL.</summary>
        public static readonly string LegacyLoadRecentSql = SelectSql(Columns) + $" FROM {Table} ORDER BY Id DESC LIMIT @n";

        private static readonly string[] SlotColumns = [.. Columns, "AssetClass"];

        /// <summary>[v12.18.86-DEV] 슬롯별 최신 @k 행(슬롯 묶음 · 묶음 내 최신 순 · 파라미터 @k).</summary>
        public static readonly string LoadSlotsSql =
            $"SELECT * FROM ({SelectSql(SlotColumns)}, ROW_NUMBER() OVER (PARTITION BY RegimeId, AssetClass ORDER BY Id DESC) AS Rn, Id FROM {Table} WHERE {SlotScope}) "
            + "WHERE Rn <= @k ORDER BY Id DESC";

        private static string SelectSql(string[] cols) =>
            $"SELECT {string.Join(", ", Array.ConvertAll(cols, c => $"CASE WHEN typeof({c}) = 'integer' THEN {c} ELSE 0 END"))}, "
            + $"({string.Join(" AND ", Array.ConvertAll(cols, c => $"typeof({c}) = 'integer'"))})";

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

        /// <summary>[v12.18.86-DEV] LoadSlotsSql 결과 → 슬롯 체크포인트(해석 불가 행 = Corrupt 표식).</summary>
        public static IReadOnlyList<PlutoLearningCheckpoint> ParseSlotRows(DataTable dt)
        {
            ArgumentNullException.ThrowIfNull(dt);
            var rows = new List<PlutoLearningCheckpoint>(dt.Rows.Count);
            foreach (DataRow r in dt.Rows)
            {
                var cp = Parse(r, SlotColumns.Length);
                if (cp.Checksum == 0 && cp.Episodes < 0) { rows.Add(cp); continue; }
                if (!TryLong(r[Columns.Length], out long asset) || asset is < 0 or > int.MaxValue) { rows.Add(PlutoLearningCheckpoint.Corrupt); continue; }
                rows.Add(new PlutoLearningCheckpoint(cp.RegimeId, cp.Episodes, cp.Weights, cp.CalibratedUtcTicks, cp.Checksum, (int)asset));
            }
            return rows;
        }

        private static PlutoLearningCheckpoint Parse(DataRow r) => Parse(r, Columns.Length);

        private static PlutoLearningCheckpoint Parse(DataRow r, int okColumn)
        {
            if (!TryLong(r[okColumn], out long ok) || ok != 1) return PlutoLearningCheckpoint.Corrupt;
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
                _db.Execute($"DELETE FROM {Table} WHERE {GlobalScope} AND Id NOT IN (SELECT Id FROM {Table} WHERE {GlobalScope} ORDER BY Id DESC LIMIT @k)", new() { ["@k"] = k });
            });
        }

        public IReadOnlyList<PlutoLearningCheckpoint> LoadSlots(int perSlot)
        {
            if (perSlot <= 0) return Array.Empty<PlutoLearningCheckpoint>();
            return ParseSlotRows(_db.Query(LoadSlotsSql, new() { ["@k"] = perSlot }));
        }

        public void AppendSlot(in PlutoLearningCheckpoint cp, int keepPerSlot)
        {
            if (!cp.IsSlot) throw new ArgumentException("슬롯 행 아님(AssetClass < 0)", nameof(cp));
            var w = cp.Weights;
            var pars = new Dictionary<string, object>
            {
                ["@RegimeId"] = cp.RegimeId, ["@Episodes"] = cp.Episodes, ["@Generation"] = w.Generation,
                ["@MomentumTiltPct"] = w.MomentumTiltPct, ["@DevilStrictnessPts"] = w.DevilStrictnessPts,
                ["@ShadowWinRateBp"] = w.ShadowWinRateBp, ["@ShadowMddBp"] = w.ShadowMddBp, ["@ShadowSamples"] = w.ShadowSamples,
                ["@CalibratedUtcTicks"] = cp.CalibratedUtcTicks, ["@Checksum"] = cp.Checksum, ["@AssetClass"] = cp.AssetClass,
            };
            var trim = new Dictionary<string, object> { ["@r"] = cp.RegimeId, ["@a"] = cp.AssetClass, ["@k"] = Math.Max(1, keepPerSlot) };
            _db.RunInTransaction(() =>
            {
                _db.Execute($"INSERT INTO {Table} ({string.Join(", ", SlotColumns)}) VALUES (@{string.Join(", @", SlotColumns)})", pars);
                _db.Execute($"DELETE FROM {Table} WHERE {SlotScope} AND RegimeId = @r AND AssetClass = @a AND Id NOT IN "
                    + $"(SELECT Id FROM {Table} WHERE {SlotScope} AND RegimeId = @r AND AssetClass = @a ORDER BY Id DESC LIMIT @k)", trim);
            });
        }
    }
}

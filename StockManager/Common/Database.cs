// ============================================================================
// StockManager.Common - 로컬 데이터베이스 (v3.5.0)
// [개선점]
//  · Safe.Dec/Int/Long/Str/DT 적용 (DBNull 예외 방지)
//  · 신규 인덱스 (Signals, News)
//  · 신규 테이블: Alerts, Dividends, BrokerAccounts
//  · Settings Key-Value 편의 메서드
//  · v3.5.0 동시성 방어: WAL + busy_timeout(5s) + SQLITE_BUSY/LOCKED 지수 백오프 재시도
//    (Blazor UI · WinForms 창 · AlertEngineService 백그라운드가 단일 portfolio.db 공유)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace StockManager.Common
{
    public class LocalDatabase : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly object _lock = new();

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockManager", "portfolio.db");

        public LocalDatabase(string? path = null)
        {
            var dbPath = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            _conn = new SqliteConnection($"Data Source={dbPath}");
            _conn.Open();
            InitSchema();
            Log.Info($"DB 초기화: {dbPath}");
        }

        private void InitSchema()
        {
            Execute(@"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;   -- 잠금 시 즉시 throw하지 않고 최대 5초 대기 (1차 방어)

CREATE TABLE IF NOT EXISTS Holdings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Symbol TEXT NOT NULL, Name TEXT,
    Market INTEGER, Currency INTEGER,
    Quantity INTEGER, AvgPrice REAL, CurrentPrice REAL,
    FirstBuyDate TEXT, BrokerAccount TEXT, Memo TEXT, Tags TEXT,
    UNIQUE(Symbol, BrokerAccount)
);

CREATE TABLE IF NOT EXISTS Transactions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Symbol TEXT NOT NULL, Type INTEGER NOT NULL,
    Quantity INTEGER, Price REAL,
    Commission REAL DEFAULT 0, Tax REAL DEFAULT 0,
    ExecutedAt TEXT NOT NULL, BrokerAccount TEXT, Memo TEXT
);
CREATE INDEX IF NOT EXISTS IX_Tx_Symbol ON Transactions(Symbol);
CREATE INDEX IF NOT EXISTS IX_Tx_Date ON Transactions(ExecutedAt);

CREATE TABLE IF NOT EXISTS AlertRules (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Symbol TEXT NOT NULL, Condition INTEGER,
    TargetValue REAL, IsActive INTEGER DEFAULT 1,
    IsTriggered INTEGER DEFAULT 0,
    CreatedAt TEXT, Channel INTEGER
);
CREATE INDEX IF NOT EXISTS IX_AlertRules_Symbol ON AlertRules(Symbol);

CREATE TABLE IF NOT EXISTS Candles (
    Symbol TEXT NOT NULL, Date TEXT NOT NULL,
    Open REAL, High REAL, Low REAL, Close REAL, Volume INTEGER,
    PRIMARY KEY(Symbol, Date)
);

CREATE TABLE IF NOT EXISTS Watchlist (
    Symbol TEXT NOT NULL, Name TEXT, Market INTEGER,
    GroupName TEXT DEFAULT 'Default', SortOrder INTEGER DEFAULT 0,
    PRIMARY KEY(Symbol, GroupName)
);

CREATE TABLE IF NOT EXISTS Signals (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Symbol TEXT, Name TEXT,
    Signal INTEGER, Strength INTEGER,
    Strategy TEXT, Reason TEXT,
    TargetPrice REAL, StopLoss REAL,
    Confidence REAL, GeneratedAt TEXT
);
CREATE INDEX IF NOT EXISTS IX_Signals_Date ON Signals(GeneratedAt);
CREATE INDEX IF NOT EXISTS IX_Signals_Symbol ON Signals(Symbol);

CREATE TABLE IF NOT EXISTS News (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT, Summary TEXT, Url TEXT UNIQUE,
    Source TEXT, PublishedAt TEXT,
    RelatedSymbols TEXT, SentimentScore REAL,
    Language TEXT
);
CREATE INDEX IF NOT EXISTS IX_News_Date ON News(PublishedAt);

CREATE TABLE IF NOT EXISTS Settings (
    [Key] TEXT PRIMARY KEY, Value TEXT
);

-- 신규 테이블들
CREATE TABLE IF NOT EXISTS Alerts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Time TEXT NOT NULL, Source INTEGER, Level INTEGER,
    Title TEXT, Message TEXT, Symbol TEXT,
    IsRead INTEGER DEFAULT 0, ActionUrl TEXT
);
CREATE INDEX IF NOT EXISTS IX_Alerts_Time ON Alerts(Time DESC);
CREATE INDEX IF NOT EXISTS IX_Alerts_Unread ON Alerts(IsRead, Time DESC);

CREATE TABLE IF NOT EXISTS Dividends (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Symbol TEXT NOT NULL, Name TEXT,
    ExDate TEXT, RecordDate TEXT, PayDate TEXT,
    DividendPerShare REAL, Currency INTEGER,
    YieldPct REAL,
    UNIQUE(Symbol, ExDate)
);
CREATE INDEX IF NOT EXISTS IX_Dividends_Date ON Dividends(ExDate);

CREATE TABLE IF NOT EXISTS BrokerAccounts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Broker TEXT, AccountNo TEXT, DisplayName TEXT,
    IsActive INTEGER DEFAULT 1, LastSyncAt TEXT,
    UNIQUE(Broker, AccountNo)
);

-- ⭐ v3.66.0: 부정거래 경고 (AddTransaction 검증 실패 시 자동 기록)
-- ⭐ v3.92.0: Severity(HIGH/MEDIUM/LOW) + ReviewedAt 감사 원장 확장
CREATE TABLE IF NOT EXISTS FraudAlerts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    AttemptedAt TEXT NOT NULL,
    Symbol TEXT,
    TransactionType TEXT,
    Reason TEXT,
    RawJson TEXT,
    IsReviewed INTEGER DEFAULT 0,
    Severity TEXT DEFAULT 'HIGH',
    ReviewedAt TEXT
);
CREATE INDEX IF NOT EXISTS IX_FraudAlerts_Time ON FraudAlerts(AttemptedAt DESC);
CREATE INDEX IF NOT EXISTS IX_FraudAlerts_Unreviewed ON FraudAlerts(IsReviewed, AttemptedAt DESC);
");
            // v3.92.0: 기존 설치 하위 호환 스키마 마이그레이션 (컬럼 이미 존재 시 SQLite 에러 무시)
            try { Execute("ALTER TABLE FraudAlerts ADD COLUMN Severity TEXT DEFAULT 'HIGH'");  } catch { }
            try { Execute("ALTER TABLE FraudAlerts ADD COLUMN ReviewedAt TEXT");               } catch { }
        }

        // ── DB 동시성 방어 (v3.5.0) ──────────────────────────────────────
        //  WAL + busy_timeout(5s)로 1차 방어하고, 그래도 SQLITE_BUSY(5)/LOCKED(6)가
        //  발생하면 지수 백오프로 재시도. 단일 _conn + lock으로 프로세스 내 직렬화 유지.
        //  (백오프 sleep은 lock 밖에서 수행 → 대기 중 다른 스레드를 막지 않음)
        private const int MaxRetries = 5;
        private SqliteTransaction? _activeTx;  // RunInTransaction 중 활성 트랜잭션

        private TResult WithRetry<TResult>(Func<SqliteCommand, TResult> body,
            string sql, Dictionary<string, object>? pars)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    lock (_lock)
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.CommandText = sql;
                        if (_activeTx != null) cmd.Transaction = _activeTx;
                        if (pars != null)
                            foreach (var kv in pars) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                        return body(cmd);
                    }
                }
                catch (SqliteException ex) when (
                    (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt < MaxRetries)
                {
                    attempt++;
                    Log.Warn($"[DB] 잠금 재시도 {attempt}/{MaxRetries} (code {ex.SqliteErrorCode})");
                    Thread.Sleep(40 * (1 << (attempt - 1)));   // 40·80·160·320·640ms
                }
            }
        }

        // RunInTransaction: lock 보유 중 work() 내 Execute() 재진입 — C# Monitor 재진입 허용.
        // work()에서 Execute()를 호출하면 _activeTx를 통해 동일 트랜잭션에 참여.
        public void RunInTransaction(Action work)
        {
            lock (_lock)
            {
                using var tx = _conn.BeginTransaction();
                _activeTx = tx;
                try
                {
                    work();
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
                finally
                {
                    _activeTx = null;
                }
            }
        }

        public int Execute(string sql, Dictionary<string, object>? pars = null)
            => WithRetry(cmd => cmd.ExecuteNonQuery(), sql, pars);

        public DataTable Query(string sql, Dictionary<string, object>? pars = null)
            => WithRetry(cmd =>
            {
                using var rdr = cmd.ExecuteReader();
                var dt = new DataTable();
                dt.Load(rdr);
                return dt;
            }, sql, pars);

        public T? QueryScalar<T>(string sql, Dictionary<string, object>? pars = null)
            => WithRetry<T?>(cmd =>
            {
                var v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return default;
                return (T)Convert.ChangeType(v, typeof(T));
            }, sql, pars);

        // ===== Holdings =====
        public void SaveHolding(Holding h)
        {
            var vr = StockValidator.ValidateHolding(h);
            if (!vr.IsValid)
                throw new ArgumentException($"보유 종목 검증 실패: {vr.Summary()}");

            Execute(@"INSERT INTO Holdings(Symbol,Name,Market,Currency,Quantity,AvgPrice,CurrentPrice,FirstBuyDate,BrokerAccount,Memo,Tags)
                      VALUES(@s,@n,@m,@c,@q,@a,@p,@d,@b,@me,@t)
                      ON CONFLICT(Symbol,BrokerAccount) DO UPDATE SET
                      Quantity=@q, AvgPrice=@a, CurrentPrice=@p, Memo=@me, Tags=@t",
                new() {
                    ["@s"] = h.Symbol, ["@n"] = h.Name, ["@m"] = (int)h.Market,
                    ["@c"] = (int)h.Currency, ["@q"] = (double)h.Quantity, ["@a"] = (double)h.AvgPrice,
                    ["@p"] = (double)h.CurrentPrice, ["@d"] = h.FirstBuyDate.ToString("o"),
                    ["@b"] = h.BrokerAccount, ["@me"] = h.Memo, ["@t"] = string.Join(",", h.Tags)
                });
        }

        public void DeleteHolding(string symbol, string brokerAccount = "")
        {
            Execute("DELETE FROM Holdings WHERE Symbol=@s AND BrokerAccount=@b",
                new() { ["@s"] = symbol, ["@b"] = brokerAccount });
        }

        public List<Holding> GetAllHoldings()
        {
            var list = new List<Holding>();
            var dt = Query("SELECT * FROM Holdings WHERE Quantity > 0 ORDER BY Symbol");
            foreach (DataRow r in dt.Rows)
            {
                list.Add(new Holding
                {
                    Id = Safe.Int(r["Id"]),
                    Symbol = Safe.Str(r["Symbol"]),
                    Name = Safe.Str(r["Name"]),
                    Market = (Market)Safe.Int(r["Market"]),
                    Currency = (Currency)Safe.Int(r["Currency"]),
                    Quantity = Safe.Dec(r["Quantity"]),   // v4.30.0 코인 소수점 — int 읽기 절삭 방지
                    AvgPrice = Safe.Dec(r["AvgPrice"]),
                    CurrentPrice = Safe.Dec(r["CurrentPrice"]),
                    FirstBuyDate = Safe.DT(r["FirstBuyDate"]),
                    BrokerAccount = Safe.Str(r["BrokerAccount"]),
                    Memo = Safe.Str(r["Memo"]),
                    Tags = Safe.Str(r["Tags"]).Split(',', StringSplitOptions.RemoveEmptyEntries)
                });
            }
            return list;
        }

        public void AddTransaction(Transaction t)
        {
            // ⭐ v3.66.0: 저장 전 검증 — 비정상값(NaN/음수/누락) 감지 시 FraudAlerts 기록 후 throw
            var v = StockValidator.ValidateTransaction(t);
            if (!v.IsValid)
            {
                _WriteFraudAlert(t, v.Summary());
                throw new ArgumentException($"거래 검증 실패: {v.Summary()}");
            }

            // 명시적 SQLite 트랜잭션 — 부분 쓰기 방지, BUSY/LOCKED 시 Rollback 보장
            lock (_lock)
            {
                using var tx = _conn.BeginTransaction();
                try
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO Transactions(Symbol,Type,Quantity,Price,Commission,Tax,ExecutedAt,BrokerAccount,Memo)
                                        VALUES(@s,@ty,@q,@p,@c,@tx,@e,@b,@m)";
                    cmd.Parameters.AddWithValue("@s",  t.Symbol        ?? "");
                    cmd.Parameters.AddWithValue("@ty", (int)t.Type);
                    cmd.Parameters.AddWithValue("@q",  (double)t.Quantity);   // v4.30.0 REAL 저장(소수점 코인)
                    cmd.Parameters.AddWithValue("@p",  (double)t.Price);
                    cmd.Parameters.AddWithValue("@c",  (double)t.Commission);
                    cmd.Parameters.AddWithValue("@tx", (double)t.Tax);
                    cmd.Parameters.AddWithValue("@e",  t.ExecutedAt.ToString("o"));
                    cmd.Parameters.AddWithValue("@b",  t.BrokerAccount ?? "");
                    cmd.Parameters.AddWithValue("@m",  t.Memo          ?? "");
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        // 검증 실패 거래를 FraudAlerts에 조용히 기록 (로거 실패가 원래 예외를 삼키면 안 됨)
        private void _WriteFraudAlert(Transaction t, string reason)
        {
            try
            {
                var raw = System.Text.Json.JsonSerializer.Serialize(new
                {
                    symbol        = t.Symbol,
                    type          = t.Type.ToString(),
                    quantity      = t.Quantity,
                    price         = t.Price,
                    commission    = t.Commission,
                    tax           = t.Tax,
                    executedAt    = t.ExecutedAt.ToString("o"),
                    brokerAccount = t.BrokerAccount,
                });
                Execute(
                    @"INSERT INTO FraudAlerts(AttemptedAt,Symbol,TransactionType,Reason,RawJson,Severity,IsReviewed)
                      VALUES(@at,@s,@ty,@r,@raw,'HIGH',0)",
                    new() {
                        ["@at"]  = DateTime.UtcNow.ToString("o"),
                        ["@s"]   = t.Symbol        ?? "",
                        ["@ty"]  = t.Type.ToString(),
                        ["@r"]   = reason,
                        ["@raw"] = raw,
                    });
                Log.Warn($"[FraudAlert] 부정거래 감지: {t.Symbol} {t.Type} — {reason}");
                AuditClient.PostFraudAlert(t, reason); // 중앙 감사 서버 fire-and-forget
            }
            catch (Exception ex) { Log.Warn($"[FraudAlert] 기록 실패: {ex.Message}"); }
        }

        // ===== FraudAlerts =====
        public List<FraudAlert> GetFraudAlerts(int limit = 200)
        {
            var dt = Query("SELECT * FROM FraudAlerts ORDER BY AttemptedAt DESC LIMIT @n",
                new() { ["@n"] = limit });
            return dt.Rows.Cast<DataRow>().Select(r => new FraudAlert
            {
                Id              = Safe.Int(r["Id"]),
                AttemptedAt     = Safe.DT(r["AttemptedAt"]),
                Symbol          = Safe.Str(r["Symbol"]),
                TransactionType = Safe.Str(r["TransactionType"]),
                Reason          = Safe.Str(r["Reason"]),
                RawJson         = Safe.Str(r["RawJson"]),
                IsReviewed      = Safe.Int(r["IsReviewed"]) == 1,
                Severity        = Safe.Str(r["Severity"]) is { Length: > 0 } sev ? sev : "HIGH",
                ReviewedAt      = Safe.Str(r["ReviewedAt"]) is { Length: > 0 } rvStr
                                  && DateTime.TryParse(rvStr, out var rvDt) ? rvDt : (DateTime?)null,
            }).ToList();
        }

        public int GetUnreviewedFraudCount() =>
            (int)QueryScalar<long>("SELECT COUNT(*) FROM FraudAlerts WHERE IsReviewed=0");

        public void MarkFraudReviewed(int id, DateTime? reviewedAt = null) =>
            Execute("UPDATE FraudAlerts SET IsReviewed=1, ReviewedAt=@rv WHERE Id=@id",
                new() { ["@id"] = id, ["@rv"] = (reviewedAt ?? DateTime.UtcNow).ToString("o") });

        public void MarkAllFraudReviewed(DateTime? reviewedAt = null) =>
            Execute("UPDATE FraudAlerts SET IsReviewed=1, ReviewedAt=@rv WHERE IsReviewed=0",
                new() { ["@rv"] = (reviewedAt ?? DateTime.UtcNow).ToString("o") });

        // 외부 경보 소스(SlippageSentinel, ComplianceBridge 등)에서 직접 호출
        // _WriteFraudAlert(internal, 트랜잭션 검증 실패)와는 별도 경로
        public void AddFraudAlert(string symbol, string source, string message,
            string severity = "HIGH")
        {
            try
            {
                Execute(
                    @"INSERT INTO FraudAlerts(AttemptedAt,Symbol,TransactionType,Reason,Severity,IsReviewed)
                      VALUES(@at,@s,@src,@msg,@sev,0)",
                    new() {
                        ["@at"]  = DateTime.UtcNow.ToString("o"),
                        ["@s"]   = symbol   ?? "",
                        ["@src"] = source   ?? "",
                        ["@msg"] = message  ?? "",
                        ["@sev"] = severity ?? "HIGH",
                    });
            }
            catch (Exception ex) { Log.Warn($"[AddFraudAlert] 기록 실패: {ex.Message}"); }
        }

        // ===== Settings (Key-Value) =====
        public string? GetSetting(string key) =>
            QueryScalar<string>("SELECT Value FROM Settings WHERE [Key]=@k", new() { ["@k"] = key });

        public void SetSetting(string key, string value)
        {
            Execute(@"INSERT INTO Settings([Key], Value) VALUES(@k,@v)
                      ON CONFLICT([Key]) DO UPDATE SET Value=@v",
                new() { ["@k"] = key, ["@v"] = value });
        }

        // ===== Settings (민감값 · DPAPI 암호화) =====
        public string? GetSecret(string key) => Secret.Unprotect(GetSetting(key));

        public void SetSecret(string key, string value) => SetSetting(key, Secret.Protect(value));

        /// <summary>지정한 비밀 키들 중 평문(레거시 또는 DPAPI 폴백)으로 남은 값을 DPAPI로 재암호화(at-rest).
        /// 이미 암호화됐거나 비어 있으면 건너뜀. 반환값=재암호화한 건수. 앱 시작 시 1회 호출 권장.</summary>
        public int MigrateSecretsAtRest(IEnumerable<string> keys)
        {
            int migrated = 0;
            foreach (var key in keys)
            {
                var raw = GetSetting(key);
                if (string.IsNullOrEmpty(raw) || Secret.IsProtected(raw)) continue;
                SetSecret(key, raw);   // Protect → enc:v1:
                migrated++;
            }
            if (migrated > 0) Log.Info($"[Security] 평문 비밀 {migrated}건 DPAPI 재암호화 완료");
            return migrated;
        }

        // ===== Alerts =====
        public void AddAlert(AlertItem a)
        {
            Execute(@"INSERT INTO Alerts(Time,Source,Level,Title,Message,Symbol,IsRead,ActionUrl)
                      VALUES(@t,@s,@l,@ti,@m,@sy,@r,@u)",
                new() {
                    ["@t"] = a.Time.ToString("o"), ["@s"] = (int)a.Source,
                    ["@l"] = (int)a.Level, ["@ti"] = a.Title, ["@m"] = a.Message,
                    ["@sy"] = a.Symbol, ["@r"] = a.IsRead ? 1 : 0, ["@u"] = a.ActionUrl
                });
        }

        public List<AlertItem> GetAlerts(int limit = 100, bool unreadOnly = false)
        {
            var sql = unreadOnly
                ? "SELECT * FROM Alerts WHERE IsRead=0 ORDER BY Time DESC LIMIT @n"
                : "SELECT * FROM Alerts ORDER BY Time DESC LIMIT @n";
            var dt = Query(sql, new() { ["@n"] = limit });
            return dt.Rows.Cast<DataRow>().Select(r => new AlertItem
            {
                Time = Safe.DT(r["Time"]),
                Source = (AlertSource)Safe.Int(r["Source"]),
                Level = (AlertLevel)Safe.Int(r["Level"]),
                Title = Safe.Str(r["Title"]),
                Message = Safe.Str(r["Message"]),
                Symbol = Safe.Str(r["Symbol"]),
                IsRead = Safe.Int(r["IsRead"]) == 1,
                ActionUrl = Safe.Str(r["ActionUrl"])
            }).ToList();
        }

        public int GetUnreadAlertCount()
        {
            var v = QueryScalar<long>("SELECT COUNT(*) FROM Alerts WHERE IsRead=0");
            return (int)v;
        }

        public void MarkAllAlertsRead() =>
            Execute("UPDATE Alerts SET IsRead=1");

        // ===== Dividends =====
        public void SaveDividend(DividendEvent d)
        {
            Execute(@"INSERT INTO Dividends(Symbol,Name,ExDate,RecordDate,PayDate,DividendPerShare,Currency,YieldPct)
                      VALUES(@s,@n,@ex,@rec,@pay,@dps,@c,@y)
                      ON CONFLICT(Symbol,ExDate) DO UPDATE SET
                      DividendPerShare=@dps, YieldPct=@y, PayDate=@pay",
                new() {
                    ["@s"] = d.Symbol, ["@n"] = d.Name,
                    ["@ex"] = d.ExDate.ToString("yyyy-MM-dd"),
                    ["@rec"] = d.RecordDate.ToString("yyyy-MM-dd"),
                    ["@pay"] = d.PayDate.ToString("yyyy-MM-dd"),
                    ["@dps"] = (double)d.DividendPerShare,
                    ["@c"] = (int)d.Currency, ["@y"] = (double)d.YieldPct
                });
        }

        public List<DividendEvent> GetUpcomingDividends(int days = 60)
        {
            var from = DateTime.Today.ToString("yyyy-MM-dd");
            var to = DateTime.Today.AddDays(days).ToString("yyyy-MM-dd");
            var dt = Query("SELECT * FROM Dividends WHERE ExDate BETWEEN @f AND @t ORDER BY ExDate",
                new() { ["@f"] = from, ["@t"] = to });
            return dt.Rows.Cast<DataRow>().Select(r => new DividendEvent
            {
                Symbol = Safe.Str(r["Symbol"]),
                Name = Safe.Str(r["Name"]),
                ExDate = Safe.DT(r["ExDate"]),
                RecordDate = Safe.DT(r["RecordDate"]),
                PayDate = Safe.DT(r["PayDate"]),
                DividendPerShare = Safe.Dec(r["DividendPerShare"]),
                Currency = (Currency)Safe.Int(r["Currency"]),
                YieldPct = Safe.Dec(r["YieldPct"])
            }).ToList();
        }

        public void Dispose()
        {
            _conn.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // =========================================================================
    // 공통 포맷터 (상승=빨강, 하락=파랑 한국 관례)
    // =========================================================================
    public static class Formatter
    {
        public static string Money(decimal v, Currency c) => c == Currency.KRW
            ? $"{v:N0}원"
            : $"${v:N2}";

        public static string Pct(decimal v) => $"{(v >= 0 ? "+" : "")}{v:N2}%";

        public static System.Drawing.Color ColorForChange(decimal v)
        {
            if (v > 0) return System.Drawing.Color.FromArgb(220, 50, 50);
            if (v < 0) return System.Drawing.Color.FromArgb(50, 90, 200);
            return System.Drawing.Color.Gray;
        }

        public static string HumanizeNumber(decimal v)
        {
            if (v >= 1_000_000_000_000m) return $"{v / 1_000_000_000_000m:N2}조";
            if (v >= 100_000_000m) return $"{v / 100_000_000m:N0}억";
            if (v >= 10_000m) return $"{v / 10_000m:N0}만";
            return v.ToString("N0");
        }
    }

    // =========================================================================
    // 환율 변환기 (HttpPool.Shared 사용)
    // =========================================================================
    public class FxConverter
    {
        private decimal _usdKrw = 1380m;
        private DateTime _lastUpdate;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async System.Threading.Tasks.Task<decimal> GetUsdKrwAsync()
        {
            if ((DateTime.Now - _lastUpdate).TotalMinutes < 10) return _usdKrw;

            await _lock.WaitAsync();
            try
            {
                if ((DateTime.Now - _lastUpdate).TotalMinutes < 10) return _usdKrw;
                var url = "https://query1.finance.yahoo.com/v8/finance/chart/USDKRW=X?interval=1d&range=1d";
                var json = await HttpPool.Shared.GetStringAsync(url);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var v = Safe.JsonDec(doc.RootElement.GetProperty("chart").GetProperty("result")[0]
                    .GetProperty("meta"), "regularMarketPrice");
                if (v > 0) { _usdKrw = v; _lastUpdate = DateTime.Now; }
            }
            catch (Exception ex) { Log.Warn($"[FX] USD/KRW 실패: {ex.Message}"); }
            finally { _lock.Release(); }
            return _usdKrw;
        }

        public decimal ToKrw(decimal usd) => usd * _usdKrw;
        public decimal ToUsd(decimal krw) => _usdKrw == 0 ? 0 : krw / _usdKrw;
    }

}

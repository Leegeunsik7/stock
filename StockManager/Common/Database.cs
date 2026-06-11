// ============================================================================
// StockManager.Common - 로컬 데이터베이스 (v2.2)
// [개선점]
//  · Safe.Dec/Int/Long/Str/DT 적용 (DBNull 예외 방지)
//  · 신규 인덱스 (Signals, News)
//  · 신규 테이블: Alerts, Dividends, BrokerAccounts
//  · Settings Key-Value 편의 메서드
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
");
        }

        public int Execute(string sql, Dictionary<string, object>? pars = null)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                if (pars != null)
                    foreach (var kv in pars) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return cmd.ExecuteNonQuery();
            }
        }

        public DataTable Query(string sql, Dictionary<string, object>? pars = null)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                if (pars != null)
                    foreach (var kv in pars) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                using var rdr = cmd.ExecuteReader();
                var dt = new DataTable();
                dt.Load(rdr);
                return dt;
            }
        }

        public T? QueryScalar<T>(string sql, Dictionary<string, object>? pars = null)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                if (pars != null)
                    foreach (var kv in pars) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                var v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return default;
                return (T)Convert.ChangeType(v, typeof(T));
            }
        }

        // ===== Holdings =====
        public void SaveHolding(Holding h)
        {
            Execute(@"INSERT INTO Holdings(Symbol,Name,Market,Currency,Quantity,AvgPrice,CurrentPrice,FirstBuyDate,BrokerAccount,Memo,Tags)
                      VALUES(@s,@n,@m,@c,@q,@a,@p,@d,@b,@me,@t)
                      ON CONFLICT(Symbol,BrokerAccount) DO UPDATE SET
                      Quantity=@q, AvgPrice=@a, CurrentPrice=@p, Memo=@me, Tags=@t",
                new() {
                    ["@s"] = h.Symbol, ["@n"] = h.Name, ["@m"] = (int)h.Market,
                    ["@c"] = (int)h.Currency, ["@q"] = h.Quantity, ["@a"] = (double)h.AvgPrice,
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
                    Quantity = Safe.Int(r["Quantity"]),
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
            Execute(@"INSERT INTO Transactions(Symbol,Type,Quantity,Price,Commission,Tax,ExecutedAt,BrokerAccount,Memo)
                      VALUES(@s,@ty,@q,@p,@c,@tx,@e,@b,@m)",
                new() {
                    ["@s"] = t.Symbol, ["@ty"] = (int)t.Type, ["@q"] = t.Quantity,
                    ["@p"] = (double)t.Price, ["@c"] = (double)t.Commission,
                    ["@tx"] = (double)t.Tax, ["@e"] = t.ExecutedAt.ToString("o"),
                    ["@b"] = t.BrokerAccount, ["@m"] = t.Memo
                });
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

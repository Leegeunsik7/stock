// ============================================================================
// SyncService - 다기기 동기화 어댑터 (IO·DB·암호화 조립, 런타임·사용자 검증) (v3.43.0)
//  순수 코어(SyncEngine 머지 + SyncCrypto E2EE)를 DB·파일과 연결한다.
//  전송은 라이브 소켓이 아니라 암호화 '.ms2sync' 번들 파일(공유폴더/Tailscale/USB로 사용자가 옮김).
//  적용 전 VACUUM INTO로 DB를 자동 백업하고, 거래=합집합·보유=신규만·설정=확정시 덮어쓰기만 한다.
//  비밀정보(enc: 값, API키/토큰)는 절대 번들에 포함하지 않는다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StockManager.Common
{
    public sealed record SyncOptions(bool Transactions = true, bool Holdings = true, bool Settings = false);

    public sealed record SyncCounts(int Transactions, int Holdings, int Settings)
    {
        public int Total => Transactions + Holdings + Settings;
    }

    public sealed record ApplyResult(int Inserted, int ConflictsApplied, int Skipped, string BackupPath);

    // 직렬화 DTO (System.Text.Json 기본 옵션)
    public sealed record TxDto(string Symbol, int Type, int Quantity, double Price, double Commission, double Tax, string ExecutedAt, string BrokerAccount, string Memo);
    public sealed record HoldingDto(string Symbol, string Name, int Market, int Currency, int Quantity, double AvgPrice, double CurrentPrice, string FirstBuyDate, string BrokerAccount, string Memo, string Tags);
    public sealed record SettingDto(string Key, string Value);

    public static class SyncService
    {
        public const int BundleVersion = 1;

        public static string SyncDir => Path.Combine(Path.GetDirectoryName(LocalDatabase.DefaultPath)!, "sync");
        public static string BackupDir => Path.Combine(Path.GetDirectoryName(LocalDatabase.DefaultPath)!, "backups");

        public static Dictionary<SyncKind, MergePolicy> DefaultPolicies() => new()
        {
            [SyncKind.Transaction] = MergePolicy.UnionDistinct,
            [SyncKind.Holding] = MergePolicy.AddMissingOnly,
            [SyncKind.Setting] = MergePolicy.PreferIncoming,
        };

        // ── 기기 식별 ─────────────────────────────────────────────────────
        public static string DeviceId(LocalDatabase db)
        {
            var id = db.GetSetting("Sync.DeviceId");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 12);
                db.SetSetting("Sync.DeviceId", id);
            }
            return id;
        }

        // ── 설정 동기화: fail-closed 허용목록 ─────────────────────────────
        //  알 수 없는 키는 기본적으로 '내보내지 않음'(fail-closed). 거부목록 방식은 새 비밀 키가
        //  추가되면 자동 누출되므로(적대적 리뷰: Lan.Pin·Email.*·KIS.AccountNo·Telegram.ChatId 누출
        //  + Dashboard.Snapshots 오타) 허용목록으로 뒤집고, 비밀/식별자 토큰은 이중으로 차단한다.
        //  → 환경설정 동기화는 '명백한 환경설정 형태'(플래그·시각·임계값 등)만 통과.
        private static readonly string[] SecretTokens =
            { "Secret", "Token", "Pass", "ApiKey", "AppKey", "Key", "Pin", "Account", "ChatId", "Email", "Smtp", "Mail", "DeviceId", "User", "Url", "Host" };
        private static readonly string[] AllowSuffix =
            { ".Enabled", ".Hour", ".Hours", ".IntervalHours", ".BandPct", ".Pct", ".Threshold", ".Days", ".Count", ".Mode", ".Window", ".AmountKrw", ".Amount", ".Theme" };
        private static readonly HashSet<string> AllowExact =
            new(StringComparer.OrdinalIgnoreCase) { "Theme", "Ui.Theme", "AutoDca.Market" };

        public static bool IsPortableSetting(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (Secret.IsProtected(value) || value.StartsWith("enc:", StringComparison.Ordinal)) return false;   // DPAPI 비밀 제외
            if (key.StartsWith("Sync.", StringComparison.Ordinal)) return false;
            foreach (var s in SecretTokens)                                                                       // 비밀/식별자 토큰 이중 차단
                if (key.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (AllowExact.Contains(key)) return true;                                                            // fail-closed 허용목록
            foreach (var s in AllowSuffix)
                if (key.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── 로컬 수집 ─────────────────────────────────────────────────────
        public static List<SyncRecord> GatherLocal(LocalDatabase db, SyncOptions opt)
        {
            var recs = new List<SyncRecord>();

            if (opt.Transactions)
            {
                foreach (DataRow r in db.Query("SELECT * FROM Transactions").Rows)
                {
                    var dto = new TxDto(
                        Safe.Str(r["Symbol"]), Safe.Int(r["Type"]), Safe.Int(r["Quantity"]),
                        (double)Safe.Dec(r["Price"]), (double)Safe.Dec(r["Commission"]), (double)Safe.Dec(r["Tax"]),
                        Safe.Str(r["ExecutedAt"]), Safe.Str(r["BrokerAccount"]), Safe.Str(r["Memo"]));
                    var json = JsonSerializer.Serialize(dto);
                    var hash = SyncEngine.Fingerprint("tx", json);            // 거래는 내용 전체로 식별(union)
                    recs.Add(new SyncRecord(SyncKind.Transaction, hash, hash, json));
                }
            }

            if (opt.Holdings)
            {
                foreach (DataRow r in db.Query("SELECT * FROM Holdings").Rows)
                {
                    var dto = new HoldingDto(
                        Safe.Str(r["Symbol"]), Safe.Str(r["Name"]), Safe.Int(r["Market"]), Safe.Int(r["Currency"]),
                        Safe.Int(r["Quantity"]), (double)Safe.Dec(r["AvgPrice"]), (double)Safe.Dec(r["CurrentPrice"]),
                        Safe.Str(r["FirstBuyDate"]), Safe.Str(r["BrokerAccount"]), Safe.Str(r["Memo"]), Safe.Str(r["Tags"]));
                    var key = dto.Symbol + "" + dto.BrokerAccount;    // 자연 키(Symbol+계좌)
                    // 현재가(CurrentPrice)는 실시간 변동값이라 해시에서 제외 → 불필요한 충돌 방지
                    var hash = SyncEngine.Fingerprint("hold", dto.Symbol, dto.Name,
                        dto.Market.ToString(CultureInfo.InvariantCulture), dto.Currency.ToString(CultureInfo.InvariantCulture),
                        dto.Quantity.ToString(CultureInfo.InvariantCulture), dto.AvgPrice.ToString("R", CultureInfo.InvariantCulture),
                        dto.FirstBuyDate, dto.BrokerAccount, dto.Memo, dto.Tags);
                    recs.Add(new SyncRecord(SyncKind.Holding, key, hash, JsonSerializer.Serialize(dto)));
                }
            }

            if (opt.Settings)
            {
                foreach (DataRow r in db.Query("SELECT [Key],Value FROM Settings").Rows)
                {
                    var key = Safe.Str(r["Key"]);
                    var val = Safe.Str(r["Value"]);
                    if (!IsPortableSetting(key, val)) continue;
                    recs.Add(new SyncRecord(SyncKind.Setting, key, SyncEngine.Fingerprint("set", val), JsonSerializer.Serialize(new SettingDto(key, val))));
                }
            }

            return recs;
        }

        public static SyncCounts CountLocal(LocalDatabase db, SyncOptions opt)
        {
            var recs = GatherLocal(db, opt);
            return new SyncCounts(
                recs.Count(r => r.Kind == SyncKind.Transaction),
                recs.Count(r => r.Kind == SyncKind.Holding),
                recs.Count(r => r.Kind == SyncKind.Setting));
        }

        // ── 번들 생성/내보내기 ────────────────────────────────────────────
        public static SyncBundle BuildBundle(LocalDatabase db, SyncOptions opt) =>
            new SyncBundle(BundleVersion, DeviceId(db), DateTime.Now.ToString("o"), GatherLocal(db, opt));

        public static byte[] ExportEncrypted(LocalDatabase db, SyncOptions opt, string passphrase)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(BuildBundle(db, opt));
            return SyncCrypto.Encrypt(json, passphrase);
        }

        public static string ExportToFile(LocalDatabase db, SyncOptions opt, string passphrase)
        {
            Directory.CreateDirectory(SyncDir);
            var blob = ExportEncrypted(db, opt, passphrase);
            var name = $"MS2E-sync-{DeviceId(db)}-{DateTime.Now:yyyyMMdd_HHmmss}.ms2sync";
            var path = Path.Combine(SyncDir, name);
            File.WriteAllBytes(path, blob);
            Log.Info($"[Sync] 번들 내보내기: {name} ({blob.Length:N0} bytes)");
            return path;
        }

        public static List<string> ListBundles()
        {
            try { Directory.CreateDirectory(SyncDir); return Directory.GetFiles(SyncDir, "*.ms2sync").OrderByDescending(f => f).ToList(); }
            catch { return new List<string>(); }
        }

        // ── 가져오기/미리보기 ─────────────────────────────────────────────
        public static bool TryImportBytes(byte[] blob, string passphrase, out SyncBundle? bundle, out string error)
        {
            bundle = null;
            if (!SyncCrypto.TryDecrypt(blob, passphrase, out var json, out error)) return false;
            try
            {
                bundle = JsonSerializer.Deserialize<SyncBundle>(json);
                if (bundle == null) { error = "번들 파싱 실패(빈 내용)"; return false; }
                if (bundle.Version != BundleVersion) { error = $"지원하지 않는 번들 버전: {bundle.Version}"; bundle = null; return false; }
                bundle.Records.RemoveAll(r => r == null);
                return true;
            }
            catch (Exception ex) { error = "번들 파싱 실패: " + ex.Message; return false; }
        }

        public static bool TryImportFile(string path, string passphrase, out SyncBundle? bundle, out string error)
        {
            bundle = null;
            try { return TryImportBytes(File.ReadAllBytes(path), passphrase, out bundle, out error); }
            catch (Exception ex) { error = "파일 읽기 실패: " + ex.Message; return false; }
        }

        public static MergePlan PlanAgainstLocal(LocalDatabase db, SyncBundle incoming, SyncOptions opt) =>
            SyncEngine.Plan(GatherLocal(db, opt), incoming.Records, DefaultPolicies());

        // ── 안전 백업 (VACUUM INTO) ───────────────────────────────────────
        public static string BackupDb(LocalDatabase db)
        {
            Directory.CreateDirectory(BackupDir);
            var path = Path.Combine(BackupDir, $"portfolio_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            var sqlPath = path.Replace('\\', '/').Replace("'", "''");
            db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            db.Execute($"VACUUM INTO '{sqlPath}'");
            Log.Info($"[Sync] 적용 전 DB 백업: {path}");
            return path;
        }

        // ── 적용 (가산적·백업 후) ─────────────────────────────────────────
        public static ApplyResult ApplyPlan(LocalDatabase db, MergePlan plan, bool applyConflicts, bool backup = true)
        {
            var policies = DefaultPolicies();
            string backupPath = "";
            if (backup) { try { backupPath = BackupDb(db); } catch (Exception ex) { Log.Warn($"[Sync] 백업 실패(적용 중단): {ex.Message}"); return new ApplyResult(0, 0, 0, "백업 실패: " + ex.Message); } }

            int ins = 0, conf = 0, skip = 0;
            foreach (var it in plan.Items)
            {
                bool isInsert = it.Op == MergeOp.Insert;
                bool isConflictApplicable = it.Op == MergeOp.ConflictDiff && applyConflicts && policies[it.Kind] == MergePolicy.PreferIncoming;
                if (!isInsert && !isConflictApplicable) { if (it.Op != MergeOp.Identical) skip++; continue; }
                try
                {
                    switch (it.Kind)
                    {
                        case SyncKind.Transaction: ApplyTx(db, it.IncomingJson); break;
                        case SyncKind.Holding: ApplyHolding(db, it.IncomingJson); break;
                        case SyncKind.Setting: ApplySetting(db, it.IncomingJson); break;
                    }
                    if (isInsert) ins++; else conf++;
                }
                catch (Exception ex) { Log.Warn($"[Sync] 적용 실패 {it.Kind}/{it.Key}: {ex.Message}"); skip++; }
            }
            Log.Info($"[Sync] 적용 완료 — 추가 {ins} · 충돌적용 {conf} · 스킵 {skip}");
            return new ApplyResult(ins, conf, skip, backupPath);
        }

        private static void ApplyTx(LocalDatabase db, string json)
        {
            var d = JsonSerializer.Deserialize<TxDto>(json)!;
            db.AddTransaction(new Transaction
            {
                Symbol = d.Symbol,
                Type = (TransactionType)d.Type,
                Quantity = d.Quantity,
                Price = (decimal)d.Price,
                Commission = (decimal)d.Commission,
                Tax = (decimal)d.Tax,
                ExecutedAt = ParseDt(d.ExecutedAt),
                BrokerAccount = d.BrokerAccount,
                Memo = d.Memo,
            });
        }

        private static void ApplyHolding(LocalDatabase db, string json)
        {
            var d = JsonSerializer.Deserialize<HoldingDto>(json)!;
            db.SaveHolding(new Holding
            {
                Symbol = d.Symbol,
                Name = d.Name,
                Market = (Market)d.Market,
                Currency = (Currency)d.Currency,
                Quantity = d.Quantity,
                AvgPrice = (decimal)d.AvgPrice,
                CurrentPrice = (decimal)d.CurrentPrice,
                FirstBuyDate = ParseDt(d.FirstBuyDate),
                BrokerAccount = d.BrokerAccount,
                Memo = d.Memo,
                Tags = string.IsNullOrEmpty(d.Tags) ? Array.Empty<string>() : d.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries),
            });
        }

        private static void ApplySetting(LocalDatabase db, string json)
        {
            var d = JsonSerializer.Deserialize<SettingDto>(json)!;
            db.SetSetting(d.Key, d.Value);
        }

        private static DateTime ParseDt(string s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.Now;
    }
}

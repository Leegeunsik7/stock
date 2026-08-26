// ============================================================================
// SyncService - 다기기 동기화 어댑터 (IO·DB·암호화 조립, 런타임·사용자 검증) (v3.43.0)
//  순수 코어(SyncEngine 머지 + SyncCrypto E2EE)를 DB·파일과 연결한다.
//  전송은 라이브 소켓이 아니라 암호화 '.ms2sync' 번들 파일(공유폴더/Tailscale/USB로 사용자가 옮김).
//  적용 전 VACUUM INTO로 DB를 자동 백업하고, 거래=합집합·보유=신규만·설정=확정시 덮어쓰기만 한다.
//  비밀정보(enc: 값, API키/토큰)는 절대 번들에 포함하지 않는다.
//
//  적대적 리뷰 반영(v3.43.0):
//   · 거래 지문을 UTC 정규화 instant로 → 타임존/오프셋 차이로 인한 중복 방지(멱등 보장)
//   · AvgPrice는 저장 정밀도(double↔decimal)로 정규화 후 해시 → 유령 충돌 방지
//   · 백업 파일명에 밀리초+GUID → 동초 충돌 방지 · 적용 실패는 ApplyResult.Ok로 명시
//   · 적용 시 enum 범위 검증 + 설정 허용목록 재검증(fail-closed) + 비유한 double 0 보정
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    public sealed record SyncOptions(bool Transactions = true, bool Holdings = true, bool Settings = false);

    public sealed record SyncCounts(int Transactions, int Holdings, int Settings)
    {
        public int Total => Transactions + Holdings + Settings;
    }

    public sealed record ApplyResult(bool Ok, int Inserted, int ConflictsApplied, int Skipped, string BackupPath, string Error);

    // 직렬화 DTO (System.Text.Json 기본 옵션)
    public sealed record TxDto(string Symbol, int Type, int Quantity, double Price, double Commission, double Tax, string ExecutedAt, string BrokerAccount, string Memo);
    public sealed record HoldingDto(string Symbol, string Name, int Market, int Currency, int Quantity, double AvgPrice, double CurrentPrice, string FirstBuyDate, string BrokerAccount, string Memo, string Tags);
    public sealed record SettingDto(string Key, string Value);

    public static class SyncService
    {
        public const int BundleVersion = 1;
        private const string KeySep = "";   // 보유 자연 키 구분자(SOH · 데이터에 등장하지 않음)

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

        // ── 지문(결정론·기기/오프셋 불변) ─────────────────────────────────
        private static double Fin(double d) => double.IsFinite(d) ? d : 0.0;

        /// <summary>저장 정밀도(double→decimal→double)로 정규화 — 적용 후 재수집 시 동일 해시 보장.</summary>
        private static double CanonicalAvg(double v)
        {
            if (!double.IsFinite(v)) return 0.0;
            try { return (double)(decimal)v; } catch { return v; }
        }

        /// <summary>시각을 오프셋 불변 표준형으로. 절대시각(Local/Utc)→UTC, 벽시계(Unspecified)→그대로.</summary>
        public static string CanonicalInstant(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) return iso;
            return dt.Kind == DateTimeKind.Unspecified
                ? dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)
                : dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        }

        public static string TxFingerprint(TxDto d) => SyncEngine.Fingerprint(
            "tx", d.Symbol,
            d.Type.ToString(CultureInfo.InvariantCulture),
            d.Quantity.ToString(CultureInfo.InvariantCulture),
            Fin(d.Price).ToString("R", CultureInfo.InvariantCulture),
            Fin(d.Commission).ToString("R", CultureInfo.InvariantCulture),
            Fin(d.Tax).ToString("R", CultureInfo.InvariantCulture),
            CanonicalInstant(d.ExecutedAt), d.BrokerAccount, d.Memo);

        public static string HoldingFingerprint(HoldingDto d) => SyncEngine.Fingerprint(
            "hold", d.Symbol, d.Name,
            d.Market.ToString(CultureInfo.InvariantCulture), d.Currency.ToString(CultureInfo.InvariantCulture),
            d.Quantity.ToString(CultureInfo.InvariantCulture),
            CanonicalAvg(d.AvgPrice).ToString("R", CultureInfo.InvariantCulture),   // 현재가는 제외(실시간 변동)
            d.FirstBuyDate, d.BrokerAccount, d.Memo, d.Tags);

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
                        Fin((double)Safe.Dec(r["Price"])), Fin((double)Safe.Dec(r["Commission"])), Fin((double)Safe.Dec(r["Tax"])),
                        Safe.Str(r["ExecutedAt"]), Safe.Str(r["BrokerAccount"]), Safe.Str(r["Memo"]));
                    var hash = TxFingerprint(dto);
                    recs.Add(new SyncRecord(SyncKind.Transaction, hash, hash, JsonSerializer.Serialize(dto)));
                }
            }

            if (opt.Holdings)
            {
                foreach (DataRow r in db.Query("SELECT * FROM Holdings").Rows)
                {
                    var dto = new HoldingDto(
                        Safe.Str(r["Symbol"]), Safe.Str(r["Name"]), Safe.Int(r["Market"]), Safe.Int(r["Currency"]),
                        Safe.Int(r["Quantity"]), Fin((double)Safe.Dec(r["AvgPrice"])), Fin((double)Safe.Dec(r["CurrentPrice"])),
                        Safe.Str(r["FirstBuyDate"]), Safe.Str(r["BrokerAccount"]), Safe.Str(r["Memo"]), Safe.Str(r["Tags"]));
                    var key = dto.Symbol + KeySep + dto.BrokerAccount;
                    recs.Add(new SyncRecord(SyncKind.Holding, key, HoldingFingerprint(dto), JsonSerializer.Serialize(dto)));
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
                if (bundle.Records == null) bundle = bundle with { Records = new List<SyncRecord>() };  // null Records 보정
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

        // ── 안전 백업 (VACUUM INTO, 고유 파일명) ──────────────────────────
        public static string BackupDb(LocalDatabase db)
        {
            Directory.CreateDirectory(BackupDir);
            var name = $"portfolio_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.db";
            var path = Path.Combine(BackupDir, name);
            var sqlPath = path.Replace('\\', '/').Replace("'", "''");
            db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            db.Execute($"VACUUM INTO '{sqlPath}'");
            Log.Info($"[Sync] 적용 전 DB 백업: {name}");
            return path;
        }

        // ── 적용 (가산적·백업 후) ─────────────────────────────────────────
        public static ApplyResult ApplyPlan(LocalDatabase db, MergePlan plan, bool applyConflicts, bool backup = true)
        {
            var policies = DefaultPolicies();
            string backupPath = "";
            if (backup)
            {
                try { backupPath = BackupDb(db); }
                catch (Exception ex) { Log.Warn($"[Sync] 백업 실패(적용 중단): {ex.Message}"); return new ApplyResult(false, 0, 0, 0, "", "백업 실패: " + ex.Message); }
            }

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
                catch (Exception ex) { Log.Warn($"[Sync] 적용 건너뜀 {it.Kind}/{it.Key}: {ex.Message}"); skip++; }
            }
            Log.Info($"[Sync] 적용 완료 — 추가 {ins} · 충돌적용 {conf} · 스킵 {skip}");
            return new ApplyResult(true, ins, conf, skip, backupPath, "");
        }

        private static void ApplyTx(LocalDatabase db, string json)
        {
            var d = JsonSerializer.Deserialize<TxDto>(json)!;
            if (!Enum.IsDefined(typeof(TransactionType), d.Type)) throw new InvalidOperationException($"알 수 없는 거래유형 {d.Type}");
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
            if (!Enum.IsDefined(typeof(Market), d.Market)) throw new InvalidOperationException($"알 수 없는 시장 {d.Market}");
            if (!Enum.IsDefined(typeof(Currency), d.Currency)) throw new InvalidOperationException($"알 수 없는 통화 {d.Currency}");
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
            if (!IsPortableSetting(d.Key, d.Value))   // 적용 시에도 fail-closed 재검증(변조/구버전 번들 방어)
                throw new InvalidOperationException($"비허용 설정 {d.Key}");   // → ApplyPlan이 skip으로 집계
            db.SetSetting(d.Key, d.Value);
        }

        private static DateTime ParseDt(string s)
        {
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) return dt;
            throw new FormatException($"날짜 파싱 실패: '{s}'");   // Now로 무단 대체하지 않음 → 해당 레코드 스킵
        }
    }
}

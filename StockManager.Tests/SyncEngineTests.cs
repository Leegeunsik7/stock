// ============================================================================
// SyncEngineTests - 머지 엔진(순수) + E2EE 암호화 + 서비스 라운드트립(임시 DB)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace StockManager.Tests
{
    public class SyncEngineTests
    {
        // ── Fingerprint(순수) ──────────────────────────────────────────────
        [Fact]
        public void Fingerprint_Deterministic_And_SeparatorMatters()
        {
            Assert.Equal(SyncEngine.Fingerprint("a", "b"), SyncEngine.Fingerprint("a", "b"));
            Assert.NotEqual(SyncEngine.Fingerprint("a"), SyncEngine.Fingerprint("b"));
            // 구분자가 비어있다면 ("a","b")와 ("ab","")가 충돌 → 이 단언이 그것을 방지
            Assert.NotEqual(SyncEngine.Fingerprint("a", "b"), SyncEngine.Fingerprint("ab", ""));
        }

        // ── 머지 계획(순수) ────────────────────────────────────────────────
        [Fact]
        public void Plan_Classifies_Insert_Conflict_Identical()
        {
            var local = new List<SyncRecord>
            {
                new(SyncKind.Transaction, "hA", "hA", "{}"),
                new(SyncKind.Holding, "K1", "h1", "{}"),
                new(SyncKind.Setting, "S1", "sv1", "{}"),
            };
            var incoming = new List<SyncRecord>
            {
                new(SyncKind.Transaction, "hA", "hA", "{}"),   // 동일(union)
                new(SyncKind.Transaction, "hB", "hB", "{}"),   // 추가
                new(SyncKind.Holding, "K1", "h1b", "{}"),       // 충돌(같은 키·다른 해시)
                new(SyncKind.Holding, "K2", "h2", "{}"),        // 추가
                new(SyncKind.Setting, "S1", "sv1b", "{}"),      // 충돌
                new(SyncKind.Setting, "S2", "sv2", "{}"),       // 추가
            };

            var plan = SyncEngine.Plan(local, incoming);
            Assert.Equal(3, plan.Inserts);                       // tx hB, holding K2, setting S2
            Assert.Equal(2, plan.Conflicts);                     // holding K1, setting S1
            Assert.Equal(1, plan.Identical);                     // tx hA
            Assert.Equal(1, plan.InsertsOf(SyncKind.Transaction));
            Assert.Equal(1, plan.ConflictsOf(SyncKind.Holding));
        }

        [Fact]
        public void Plan_Union_NeverConflicts_OnTransactions()
        {
            var local = new List<SyncRecord> { new(SyncKind.Transaction, "h1", "h1", "{}") };
            var incoming = new List<SyncRecord> { new(SyncKind.Transaction, "h2", "h2", "{}") };
            var plan = SyncEngine.Plan(local, incoming);
            Assert.Equal(0, plan.Conflicts);   // 거래는 절대 충돌로 분류되지 않음(합집합)
            Assert.Equal(1, plan.Inserts);
        }

        // ── E2EE 암호화(순수) ──────────────────────────────────────────────
        [Fact]
        public void Crypto_RoundTrip_WrongPass_Tamper()
        {
            var data = Encoding.UTF8.GetBytes("MS2-E sync payload 안녕 12345");
            var blob = SyncCrypto.Encrypt(data, "correct-horse");

            Assert.True(SyncCrypto.TryDecrypt(blob, "correct-horse", out var pt, out _));
            Assert.Equal(data, pt);

            Assert.False(SyncCrypto.TryDecrypt(blob, "wrong-pass", out _, out var e1));
            Assert.NotEqual("", e1);

            blob[blob.Length - 1] ^= 0xFF;     // 변조
            Assert.False(SyncCrypto.TryDecrypt(blob, "correct-horse", out _, out _));
        }

        [Fact]
        public void Crypto_RejectsNonBundle()
        {
            Assert.False(SyncCrypto.TryDecrypt(new byte[] { 1, 2, 3 }, "x", out _, out _));     // 너무 짧음
            Assert.False(SyncCrypto.TryDecrypt(new byte[64], "x", out _, out _));               // 매직 불일치
        }

        // ── 설정 화이트리스트(순수) ────────────────────────────────────────
        [Fact]
        public void PortableSetting_AllowlistFailClosed_ExcludesSecretsPiiAndUnknown()
        {
            // 허용(명백한 환경설정 형태)
            Assert.True(SyncService.IsPortableSetting("Brief.Hour", "8"));
            Assert.True(SyncService.IsPortableSetting("Brief.Enabled", "true"));
            Assert.True(SyncService.IsPortableSetting("Drift.BandPct", "5"));
            Assert.True(SyncService.IsPortableSetting("AutoDca.Market", "KRW-BTC"));

            // 비밀/자격증명
            Assert.False(SyncService.IsPortableSetting("Gemini.ApiKey", "xxx"));        // Key/ApiKey
            Assert.False(SyncService.IsPortableSetting("KIS.Secret", "enc:v1:abc"));    // Secret + enc
            Assert.False(SyncService.IsPortableSetting("Lan.Pin", "1234"));             // 평문 LAN 인증 PIN(적대적 리뷰)

            // PII / 식별자
            Assert.False(SyncService.IsPortableSetting("Email.User", "a@b.com"));       // SMTP 사용자
            Assert.False(SyncService.IsPortableSetting("Email.Smtp", "smtp.x.com"));    // 서버
            Assert.False(SyncService.IsPortableSetting("Email.To", "a@b.com"));         // 수신 PII
            Assert.False(SyncService.IsPortableSetting("KIS.AccountNo", "12345678"));   // 계좌번호
            Assert.False(SyncService.IsPortableSetting("Telegram.ChatId", "99887766")); // 챗 ID

            // 런타임 상태 / 기기특화 / Sync / 알 수 없는 키(fail-closed)
            Assert.False(SyncService.IsPortableSetting("Optimizer.LastRun", "2026-01-01"));
            Assert.False(SyncService.IsPortableSetting("Dashboard.Snapshots", "[...]")); // 오타였던 항목
            Assert.False(SyncService.IsPortableSetting("Sync.DeviceId", "abc"));
            Assert.False(SyncService.IsPortableSetting("Ollama.Url", "http://x"));
            Assert.False(SyncService.IsPortableSetting("Some.Unknown.Key", "value"));    // 알 수 없음 → 제외
        }

        // ── 서비스 라운드트립(임시 DB, 결정론) ──────────────────────────────
        [Fact]
        public void Service_Export_Import_Plan_Apply_RoundTrip()
        {
            string p1 = TempDb(), p2 = TempDb();
            try
            {
                using (var src = new LocalDatabase(p1))
                {
                    src.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 10, Price = 70000m, ExecutedAt = new DateTime(2026, 1, 2, 9, 0, 0), BrokerAccount = "KIS" });
                    src.AddTransaction(new Transaction { Symbol = "000660", Type = TransactionType.Buy, Quantity = 5, Price = 120000m, ExecutedAt = new DateTime(2026, 1, 3, 9, 0, 0), BrokerAccount = "KIS" });
                    src.SaveHolding(new Holding { Symbol = "005930", Name = "삼성전자", Quantity = 10, AvgPrice = 70000m, BrokerAccount = "KIS", FirstBuyDate = new DateTime(2026, 1, 2) });

                    var opt = new SyncOptions(true, true, false);
                    var blob = SyncService.ExportEncrypted(src, opt, "shared-pass");

                    using var dst = new LocalDatabase(p2);
                    Assert.True(SyncService.TryImportBytes(blob, "shared-pass", out var bundle, out _));

                    var plan = SyncService.PlanAgainstLocal(dst, bundle!, opt);
                    Assert.Equal(3, plan.Inserts);          // 2 거래 + 1 보유 (모두 신규)
                    Assert.Equal(0, plan.Conflicts);

                    var res = SyncService.ApplyPlan(dst, plan, applyConflicts: false, backup: false);
                    Assert.True(res.Ok);
                    Assert.Equal(3, res.Inserted);

                    // 두 행이 실제로 들어갔는지
                    Assert.Equal(2, dst.Query("SELECT * FROM Transactions").Rows.Count);
                    Assert.Equal(1, dst.Query("SELECT * FROM Holdings").Rows.Count);

                    // 재계획 → 전부 동일(멱등성)
                    var plan2 = SyncService.PlanAgainstLocal(dst, bundle!, opt);
                    Assert.Equal(0, plan2.Inserts);
                    Assert.Equal(3, plan2.Identical);
                }
            }
            finally { Cleanup(p1); Cleanup(p2); }
        }

        [Fact]
        public void Service_AddMissingOnly_DoesNotOverwriteExistingHolding()
        {
            string p1 = TempDb(), p2 = TempDb();
            try
            {
                var opt = new SyncOptions(false, true, false);
                using var src = new LocalDatabase(p1);
                src.SaveHolding(new Holding { Symbol = "005930", Name = "삼성전자", Quantity = 99, AvgPrice = 80000m, BrokerAccount = "KIS", FirstBuyDate = new DateTime(2026, 1, 2) });
                var blob = SyncService.ExportEncrypted(src, opt, "pw");

                using var dst = new LocalDatabase(p2);
                // dst에 같은 키(005930|KIS)지만 다른 수량의 보유가 이미 존재
                dst.SaveHolding(new Holding { Symbol = "005930", Name = "삼성전자", Quantity = 1, AvgPrice = 60000m, BrokerAccount = "KIS", FirstBuyDate = new DateTime(2026, 1, 2) });

                Assert.True(SyncService.TryImportBytes(blob, "pw", out var bundle, out _));
                var plan = SyncService.PlanAgainstLocal(dst, bundle!, opt);
                Assert.Equal(0, plan.Inserts);       // 같은 키 → 추가 안 함
                Assert.Equal(1, plan.Conflicts);     // 충돌로만 표시

                // 충돌 미적용 → 기존(수량 1) 보존
                SyncService.ApplyPlan(dst, plan, applyConflicts: false, backup: false);
                var qty = dst.QueryScalar<long>("SELECT Quantity FROM Holdings WHERE Symbol='005930'");
                Assert.Equal(1, qty);                // 덮어쓰이지 않음
            }
            finally { Cleanup(p1); Cleanup(p2); }
        }

        // ── 적대적 리뷰 수정 검증 ──────────────────────────────────────────
        [Fact]
        public void CanonicalInstant_IsOffsetInvariant_ForAbsoluteTimes()
        {
            Assert.Equal(
                SyncService.CanonicalInstant("2026-01-02T09:30:00.0000000+09:00"),
                SyncService.CanonicalInstant("2026-01-02T00:30:00.0000000Z"));   // 같은 절대시각
            Assert.NotEqual(
                SyncService.CanonicalInstant("2026-01-02T09:30:00.0000000+09:00"),
                SyncService.CanonicalInstant("2026-01-02T09:30:00.0000000+00:00")); // 다른 절대시각
        }

        [Fact]
        public void TxFingerprint_IsTimezoneInvariant()
        {
            var a = new TxDto("005930", 0, 10, 70000, 0, 0, "2026-01-02T09:30:00.0000000+09:00", "KIS", "");
            var b = a with { ExecutedAt = "2026-01-02T00:30:00.0000000Z" };   // 동일 instant, UTC 표기
            Assert.Equal(SyncService.TxFingerprint(a), SyncService.TxFingerprint(b));   // 교차 TZ 중복 방지
        }

        [Fact]
        public void HoldingFingerprint_StableAcrossDoubleDecimalRoundTrip()
        {
            var h = new HoldingDto("005930", "S", 0, 0, 3, 1.0 / 3.0, 0, "2026-01-02T00:00:00.0000000", "KIS", "", "");
            var h2 = h with { AvgPrice = (double)(decimal)(1.0 / 3.0) };   // 적용 후 재수집 값
            Assert.Equal(SyncService.HoldingFingerprint(h), SyncService.HoldingFingerprint(h2));  // 유령 충돌 없음
        }

        [Fact]
        public void Apply_ReValidatesSettingAllowlist_BlocksNonPortable()
        {
            string p = TempDb();
            try
            {
                using var db = new LocalDatabase(p);
                var rec = new SyncRecord(SyncKind.Setting, "Broker.LiveArmed", "h",
                    System.Text.Json.JsonSerializer.Serialize(new SettingDto("Broker.LiveArmed", "1")));
                var plan = SyncEngine.Plan(new List<SyncRecord>(), new List<SyncRecord> { rec });
                Assert.Equal(1, plan.Inserts);
                var res = SyncService.ApplyPlan(db, plan, applyConflicts: false, backup: false);
                Assert.True(res.Ok);
                Assert.Equal(1, res.Skipped);                      // 비허용 → 스킵
                Assert.Null(db.GetSetting("Broker.LiveArmed"));    // 기록되지 않음
            }
            finally { Cleanup(p); }
        }

        [Fact]
        public void Apply_SkipsTransaction_WithOutOfRangeEnum()
        {
            string p = TempDb();
            try
            {
                using var db = new LocalDatabase(p);
                var rec = new SyncRecord(SyncKind.Transaction, "h1", "h1",
                    System.Text.Json.JsonSerializer.Serialize(new TxDto("005930", 99, 1, 100, 0, 0, "2026-01-02T09:00:00.0000000", "", "")));
                var plan = SyncEngine.Plan(new List<SyncRecord>(), new List<SyncRecord> { rec });
                var res = SyncService.ApplyPlan(db, plan, applyConflicts: false, backup: false);
                Assert.Equal(1, res.Skipped);                      // 알 수 없는 거래유형 → 스킵
                Assert.Equal(0, db.Query("SELECT * FROM Transactions").Rows.Count);
            }
            finally { Cleanup(p); }
        }

        [Fact]
        public void Import_NullRecords_YieldsEmptyBundle()
        {
            var raw = Encoding.UTF8.GetBytes("{\"Version\":1,\"DeviceId\":\"x\",\"ExportedAtIso\":\"\",\"Records\":null}");
            var blob = SyncCrypto.Encrypt(raw, "pw");
            Assert.True(SyncService.TryImportBytes(blob, "pw", out var bundle, out _));
            Assert.NotNull(bundle);
            Assert.Empty(bundle!.Records);
        }

        private static string TempDb() => Path.Combine(Path.GetTempPath(), "smsync_" + Guid.NewGuid().ToString("N") + ".db");
        private static void Cleanup(string path) { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
    }
}

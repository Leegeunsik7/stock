// ============================================================================
// BrokerSettingsStatusTests — v12.18.58-DEV: 거래 설정 상태 Port(판독 전용) · 마스킹 · Pure Shell 결속 회귀
//   ① CredentialMask.First2: 앞 2자 외 '*' · 상한 16 · 2자 이하 전부 '*'
//   ② BrokerSettingsStatusAdapter: DPAPI 봉인 → 마스킹 · 평문 저장 → Sealed=false('****' · 앞자리 미노출) · 미설정 → Present=false
//   ③ 크립토 모의: 미등록 = Wired=false(날조 0) · 등록 = 초기 예수금 1억 · 호가 미수신(-1)
//   ④ 구조: Port 는 GetSnapshot 단일(저장/발주 0) · InstallerBuildPanel 은 inject 0 + 구상 서비스 필드 0
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Ports.Brokers;
using StockManager.Adapters;
using StockManager.Engines.Brokers.Crypto;
using Xunit;

namespace StockManager.Tests
{
    public class BrokerSettingsStatusTests
    {
        private const string Key = "PSxAppKeyPlainText0123456789";
        private const string Sec = "SECRETplain-abcdefghijklmnop";
        private const string Acct = "50123456";

        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smbs_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        private static BrokerSettingsStatusAdapter Adapter(LocalDatabase db, CryptoPaperExchange? crypto = null)
        {
            var sc = new ServiceCollection();
            sc.AddSingleton(db);
            if (crypto is not null) sc.AddSingleton(crypto);
            return new BrokerSettingsStatusAdapter(sc.BuildServiceProvider());
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("a", "*")]
        [InlineData("ab", "**")]
        [InlineData("abc", "ab*")]
        [InlineData("abcdef", "ab****")]
        public void Mask_First2(string? plain, string expected) => Assert.Equal(expected, CredentialMask.First2(plain));

        [Fact]
        public void Mask_Caps_Star_Count()
        {
            var m = CredentialMask.First2(new string('k', 100));
            Assert.Equal(CredentialMask.Visible + CredentialMask.MaxStars, m.Length);
            Assert.StartsWith("kk*", m);
        }

        [Fact]
        public void Sealed_Profile_Is_Masked_And_Never_Plain() => WithDb(db =>
        {
            db.SetSetting("KIS.Profile.PaperDomestic.AppKey", Secret.Protect(Key));
            db.SetSetting("KIS.Profile.PaperDomestic.AppSecret", Secret.Protect(Sec));
            db.SetSetting("KIS.Profile.PaperDomestic.Account", Secret.Protect(Acct));

            var p = Adapter(db).GetSnapshot().PaperDomestic;
            Assert.True(p.AllSealed);
            Assert.False(p.AnyPlaintext);
            Assert.Equal("PS" + new string('*', CredentialMask.MaxStars), p.AppKey.Masked);
            Assert.Equal("50******", p.Account.Masked);
            foreach (var f in new[] { p.AppKey, p.AppSecret, p.Account })
            {
                Assert.DoesNotContain(Key[2..6], f.Masked);
                Assert.DoesNotContain(Sec[2..6], f.Masked);
                Assert.DoesNotContain(Acct[2..], f.Masked);
            }
        });

        [Fact]
        public void Plaintext_Stored_Value_Fails_Closed_Without_Prefix() => WithDb(db =>
        {
            db.SetSetting("KIS.Profile.PaperOverseas.AppKey", Key);   // 평문 저장(비봉인)
            db.SetSetting("KIS.Profile.PaperOverseas.AppSecret", Secret.Protect(Sec));

            var s = Adapter(db).GetSnapshot();
            var p = s.PaperOverseas;
            Assert.False(p.AllSealed);
            Assert.True(p.AnyPlaintext);
            Assert.True(p.AppKey.Present);
            Assert.False(p.AppKey.Sealed);
            Assert.Equal("****", p.AppKey.Masked);
            Assert.False(p.Account.Present);
            Assert.Equal("", p.Account.Masked);

            Assert.False(s.PaperDomestic.AppKey.Present);   // 타 프로필 무교차
            Assert.False(s.PaperDomestic.AllSealed);
        });

        [Fact]
        public void Undecryptable_Envelope_Is_Not_Reported_Sealed() => WithDb(db =>
        {
            db.SetSetting("Upbit.AccessKey", "enc:v1:AAAA");   // 마커만 있고 복호 불가
            var f = Adapter(db).GetSnapshot().UpbitAccessKey;
            Assert.True(f.Present);
            Assert.False(f.Sealed);
            Assert.Equal("****", f.Masked);
        });

        [Fact]
        public void Legacy_Vault_Keys_Are_Masked() => WithDb(db =>
        {
            ApiKeyVault.SaveKis(db, Key, Sec, Acct);
            ApiKeyVault.SaveUpbit(db, "UPaccess-1234567", "UPsecret-1234567");
            var s = Adapter(db).GetSnapshot();
            Assert.True(s.LegacyKisAppKey.Sealed);
            Assert.Equal("PS", s.LegacyKisAppKey.Masked[..2]);
            Assert.Equal("50******", s.LegacyKisAccount.Masked);
            Assert.Equal("UP**************", s.UpbitSecretKey.Masked);
        });

        [Fact]
        public void Crypto_Unregistered_Is_Unwired() => WithDb(db =>
        {
            var c = Adapter(db).GetSnapshot().Crypto;
            Assert.False(c.Wired);
            Assert.Equal(100_000_000m, c.InitialKrw);
            Assert.Equal(-1, c.QuoteAgeMs);
            Assert.Equal(0m, c.KrwBalance);
        });

        [Fact]
        public void Crypto_Registered_Reports_Seed_And_No_Quote() => WithDb(db =>
        {
            var c = Adapter(db, new CryptoPaperExchange()).GetSnapshot().Crypto;
            Assert.True(c.Wired);
            Assert.Equal(100_000_000m, c.InitialKrw);
            Assert.Equal(100_000_000m, c.KrwBalance);
            Assert.Equal(0m, c.BtcBalance);
            Assert.Equal(-1, c.QuoteAgeMs);
            Assert.Equal(0, c.FillCount);
        });

        [Fact]
        public void Port_Is_Read_Only()
        {
            var methods = typeof(IBrokerSettingsStatusPort).GetMethods();
            Assert.Single(methods);
            Assert.Equal(nameof(IBrokerSettingsStatusPort.GetSnapshot), methods[0].Name);
        }

        [Fact]
        public void InstallerBuildPanel_Uses_Port_Parameter_Not_Concrete_Service()
        {
            var panel = typeof(CanaryHealthSentinel).Assembly.GetType("StockManager.Blazor.Pages.InstallerBuildPanel", throwOnError: true)!;
            Assert.False(PureShellFitnessSentinel.IsHostCoupledUi(panel));   // inject 0 → 래칫 결합 수 불변
            var fields = panel.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.DoesNotContain(fields, f => f.FieldType == typeof(InstallerBuildService));
            var port = panel.GetProperty("Port");
            Assert.NotNull(port);
            Assert.Equal(typeof(MS2E.UI.Portable.Ports.Tools.IInstallerBuildPort), port!.PropertyType);
        }
    }
}

// ============================================================================
// PiiEngineHardeningTests — [PII 하드닝] 금융 PII 마스커 · 원문 자격증명 유출 인터락 · 로그 비밀 카탈로그 (StockManager)
//
//   수정 전 실측(Red):
//     · 로그 차폐 등록 목록이 금고 키 6종 누락(Toss.ClientId/ClientSecret/Account·KIS.Account·Email.Pass·Cartridge.PrivKey)
//     · LogRedactor 는 등록값·key=value 라벨만 차폐 — 주민번호·카드·이메일·전화·타 계좌·JWT 접근토큰·"Bearer …" 헤더는 평문 기록
//     · 원문 자격증명이 로그로 흘러도 차폐만 될 뿐 침해로 취급되지 않음(무장 계속 가능)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    public class PiiEngineHardeningTests
    {
        // ═══ §1 금융 PII 탐지 ═══

        [Theory]
        [InlineData("주민 900101-1234567 확인", "주민 ******-******* 확인")]
        [InlineData("주민 9001011234567", "주민 *************")]
        [InlineData("카드 4111-1111-1111-1111", "카드 ****-****-****-****")]
        [InlineData("카드 4111111111111111 결제", "카드 **************** 결제")]          // 무구분 16자리 + Luhn
        [InlineData("amex 3782-822463-10005", "amex ****-******-*****")]
        [InlineData("mail hong@samsung-sec.co.kr", "mail ****@***********.**.kr")]
        [InlineData("휴대 010-1234-5678", "휴대 ***-****-****")]
        [InlineData("휴대 01012345678", "휴대 ***********")]
        [InlineData("지점 02-3456-7890", "지점 **-****-****")]
        [InlineData("경기 031-123-4567", "경기 ***-***-****")]
        [InlineData("해외 +82 10-1234-5678", "해외 +** **-****-****")]
        [InlineData("KIS 12345678-01 조회", "KIS ********-** 조회")]                  // 한국투자 CANO-ACNT_PRDT_CD
        [InlineData("키움 8012-3456-11", "키움 ****-****-**")]
        [InlineData("신한 110-123-456789", "신한 ***-***-******")]
        [InlineData("계좌 8012345611 잔고", "계좌 ********** 잔고")]                  // 무구분 + 키워드 직전
        [InlineData("accno=8012345611", "accno=**********")]
        public void Financial_Pii_Masked(string input, string expected)
            => Assert.Equal(expected, FinancialPiiMasker.Mask(input));

        [Fact]
        public void Tokens_Masked_Jwt_KisAppKey_KnownPrefixes()
        {
            const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2lnbmF0dXJlMTIz";
            string masked = FinancialPiiMasker.Mask("token " + jwt + " ok");
            Assert.DoesNotContain("eyJ", masked);
            Assert.EndsWith(" ok", masked);
            Assert.DoesNotContain("AIzaSy", FinancialPiiMasker.Mask("key AIzaSyA1b2C3d4E5f6G7h8I9j0KlMnOpQrStU"));
            Assert.DoesNotContain("PSabc", FinancialPiiMasker.Mask("appkey PSabcdEFGH1234567890ijklMNOP12345678"));
        }

        // ═══ §2 주가/주문 데이터 오마스킹 금지 ═══

        [Theory]
        [InlineData("[주문] 005930 매수 10주 @72,500 ODNO=0000123456")]
        [InlineData("체결 035720 qty=150 px=48250 t=20260930091530123")]
        [InlineData("호가 72500/72600 잔량 1234/5678")]
        [InlineData("평가 1,234,567,890원 손익 -12,345원")]
        [InlineData("계좌 예수금 5000000000원")]                                     // 키워드 문맥이어도 금액 단위 → 계좌 아님
        [InlineData("계좌 12345678-01 주문번호 0000123456")]                         // 주문번호는 보존(계좌만 가려짐 — 아래 별도 검증)
        [InlineData("주문번호 0212345678 접수")]                                    // 02 시작 10자리 — 무구분 유선 규칙 비활성(증권 문맥)
        [InlineData("ts 20260930091530 seq 1234567890")]
        [InlineData("ticks 638635482000000000")]                                   // 18자리 — 무구분 카드는 15~16자리만
        [InlineData("sha256 3BD04E4B26DA015C75B4489A9F193A1F89F4E1154CDA0538DD009ACE6C452322")]
        [InlineData("MS2E v12.14.1 부팅 2026-09-30 09:15:30.123")]
        [InlineData("KRX:005930 KOSDAQ:035720 ETF:069500")]
        [InlineData("price 72500.50 qty 1000000 vwap 71234.5678")]
        [InlineData("id 3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
        [InlineData("git@github.com:ms2e/stock.git")]
        [InlineData("latency p99=1234567ns budget=500000ns")]
        public void Stock_And_Order_Data_Not_Over_Masked(string text)
        {
            string masked = FinancialPiiMasker.Mask(text);
            if (text.Contains("12345678-01", StringComparison.Ordinal))
                Assert.Equal("계좌 ********-** 주문번호 0000123456", masked);
            else
                Assert.Equal(text, masked);
        }

        [Theory]
        [InlineData("005930"), InlineData("035720"), InlineData("000660"), InlineData("069500"), InlineData("373220")]
        public void Every_Six_Digit_Stock_Code_Survives_In_Any_Position(string code)
        {
            foreach (var line in new[] { code, $"종목 {code}", $"{code} 매수", $"[{code}]", $"sym={code}", $"\"{code}\"", $"{code},72500,10" })
                Assert.Equal(line, FinancialPiiMasker.Mask(line));
        }

        // ═══ §1 δ=0 ═══

        [Fact]
        public void MaskInPlace_Is_Zero_Alloc_On_Mixed_Trading_Log()
        {
            string line = string.Concat(Enumerable.Repeat(
                "[주문] 005930 매수 10주 @72,500 ODNO=0000123456 계좌 12345678-01 휴대 010-1234-5678 hong@samsung-sec.co.kr t=20260930091530 | ", 10));
            var src = line.ToCharArray();
            var buf = new char[src.Length];
            for (int i = 0; i < 3; i++) FinancialPiiMasker.MaskInto(src, buf);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) FinancialPiiMasker.MaskInto(src, buf); }));
            Assert.Equal(30, FinancialPiiMasker.MaskInto(src, buf));   // 반복당 계좌1·전화1·이메일1
            Assert.Contains("005930", new string(buf));
            Assert.Contains("0000123456", new string(buf));
        }

        [Fact]
        public void Masking_Is_Idempotent()
        {
            string once = FinancialPiiMasker.Mask("hong@a.co.kr 010-1234-5678 12345678-01 4111111111111111");
            Assert.Equal(once, FinancialPiiMasker.Mask(once));
        }

        // ═══ §3 방향 B — 원문 자격증명 유출 인터락 ═══

        [Fact]
        public void Raw_Credential_In_Log_Trips_Zeroizes_Order_Token_And_Blocks_Arming()
        {
            var interlock = new RawCredentialLeakInterlock();
            var purger = new VolatileMemoryPurger();
            var apiSecret = Encoding.ASCII.GetBytes("SECRET-BYTES");
            purger.Register(apiSecret);
            var orderToken = new PoisonedSecretBuffer(new byte[] { 0xA1, 0xB2, 0xC3 });
            int kills = 0;
            interlock.Bind(purger, () => kills++, orderToken);
            interlock.RegisterCredential("KisAppSecret-9f8e7d6c5b4a");

            Assert.False(interlock.Inspect("정상 로그 005930 매수 10주"));
            Assert.False(interlock.ArmingBlocked);
            Assert.True(interlock.Inspect("HttpRequestException: auth failed secret=KisAppSecret-9f8e7d6c5b4a"));
            Assert.True(interlock.ArmingBlocked);
            Assert.True(orderToken.IsPoisoned && orderToken.AllZero());
            Assert.Throws<ObjectDisposedException>(() => orderToken.ReadByte(0));
            Assert.True(purger.AllZero);
            Assert.Equal(1, kills);
            Assert.Equal(0, interlock.CredentialCount);                   // 보관 사본도 소거
            interlock.RegisterCredential("another-credential-123");        // Trip 이후 등록 거부(유출 추정 상태)
            Assert.Equal(0, interlock.CredentialCount);
        }

        [Fact]
        public void Masked_Or_Short_Values_Do_Not_Trip()
        {
            var interlock = new RawCredentialLeakInterlock();
            interlock.RegisterCredential("short");                          // < 8자 — 오격발 방지로 미등록
            interlock.RegisterCredential("Toss-ClientSecret-ABCDEF123");
            Assert.Equal(1, interlock.CredentialCount);
            Assert.False(interlock.Inspect(RuntimeConfig.Redact("Toss-ClientSecret-ABCDEF123")));   // 이미 가려진 형태
            Assert.False(interlock.Inspect("short short short"));
            Assert.False(interlock.Tripped);
        }

        [Fact]
        public void Utf8_Byte_Channel_Detects_Raw_Credential()
        {
            var interlock = new RawCredentialLeakInterlock();
            interlock.RegisterCredential("비밀번호-Pass-2026!");
            Assert.True(interlock.InspectUtf8(Encoding.UTF8.GetBytes("dump: 비밀번호-Pass-2026! end")));
            Assert.True(interlock.Tripped);
        }

        [Fact]
        public void Inspect_Is_Zero_Alloc()
        {
            var interlock = new RawCredentialLeakInterlock();
            for (int i = 0; i < 8; i++) interlock.RegisterCredential("credential-value-" + i.ToString("D4"));
            var line = "[체결] 005930 150주 @48,250 t=20260930091530 정상 라인 — 자격증명 없음".ToCharArray();
            var bytes = Encoding.UTF8.GetBytes(new string(line));
            for (int i = 0; i < 3; i++) { interlock.Inspect(line); interlock.InspectUtf8(bytes); }
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 500; i++) { interlock.Inspect(line); interlock.InspectUtf8(bytes); } }));
            Assert.False(interlock.Tripped);
        }

        // ═══ §2 로그 초크포인트(LogRedactor) 결속 ═══

        [Fact]
        public void Redactor_With_Interlock_Masks_Pattern_Pii_Labels_Bearer_And_Trips_On_Raw_Credential()
        {
            var interlock = new RawCredentialLeakInterlock();
            var r = new LogRedactor { LeakInterlock = interlock };
            string scrubbed = r.Scrub("주문 005930 10주 CANO=80123456 client_secret: abcdef123456 Authorization: Bearer eyJhbGciOi.eyJzdWIi.sig12345 휴대 010-9876-5432 hong@naver.com");
            Assert.Contains("005930 10주", scrubbed);
            foreach (var raw in new[] { "80123456", "abcdef123456", "eyJhbGciOi", "9876-5432", "hong@" }) Assert.DoesNotContain(raw, scrubbed);
            Assert.False(interlock.Tripped);                               // 미등록 값은 차폐만(침해 판정은 등록 자격증명 기준)

            interlock.RegisterCredential("UpbitSecretKey-000111222");
            string leak = r.Scrub("debug dump UpbitSecretKey-000111222");
            Assert.Equal(LogRedactor.LeakDiscardedLine, leak);            // 라인 전체 폐기 — 원문 0바이트(수정 전: 원문 그대로 기록)
            Assert.True(interlock.Tripped);
        }

        [Fact]
        public void Standalone_Redactor_Has_No_Interlock_And_Shared_Has_Global_One()
        {
            Assert.Null(new LogRedactor().LeakInterlock);
            Assert.Same(RawCredentialLeakInterlock.Shared, LogRedactor.Shared.LeakInterlock);
        }

        // ═══ 카탈로그 — 금고 저장 키 전수 커버(누락 회귀 자동 적발) ═══

        [Fact]
        public void Catalog_Covers_Every_Secret_Key_Stored_Anywhere_In_Source()
        {
            string root = GmPaths.RepoRoot();
            var stored = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var dir in new[] { "src", "StockManager", "MS2E.UI.Portable" })
                foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                {
                    if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(f), "SetSecret\\(\"([A-Za-z0-9_.]+)\"")) stored.Add(m.Groups[1].Value);
                }
            Assert.True(stored.Count >= 10, $"stored keys={stored.Count}");
            var covered = new HashSet<string>(LogSecretCatalog.CredentialSecretKeys.Concat(LogSecretCatalog.IdentifierSecretKeys), StringComparer.Ordinal);
            var missing = stored.Where(k => !covered.Contains(k)).ToList();
            Assert.True(missing.Count == 0, "로그 차폐 카탈로그 누락: " + string.Join(", ", missing));   // 수정 전: Toss.*·KIS.Account·Email.Pass·Cartridge.PrivKey
        }

        [Fact]
        public void RegisterAll_Routes_Credentials_To_Interlock_And_Identifiers_To_Redactor_Only()
        {
            var secrets = new Dictionary<string, string>
            {
                ["Toss.ClientSecret"] = "toss-client-secret-xyz",
                ["KIS.AppKey"] = "PSkisappkey1234567890",
                ["Toss.Account"] = "9876543210",
            };
            var settings = new Dictionary<string, string> { ["KIS.AccountNo"] = "12345678-01" };
            var redactor = new LogRedactor();
            var interlock = new RawCredentialLeakInterlock();
            var (c, i) = LogSecretCatalog.RegisterAll(k => secrets.GetValueOrDefault(k), k => settings.GetValueOrDefault(k), redactor, interlock);
            Assert.Equal((2, 2), (c, i));
            Assert.Equal(4, redactor.Count);
            Assert.Equal(2, interlock.CredentialCount);
            Assert.False(interlock.Inspect("계좌 9876543210 조회"));          // 계좌(식별자)는 차단 대상 아님 — 마스킹만
            Assert.DoesNotContain("9876543210", redactor.Scrub("계좌 9876543210 조회"));
        }

        [Fact]
        public void Host_Arming_Predicate_Reads_Leak_Interlock()
        {
            var getter = typeof(RawCredentialLeakInterlock).GetProperty(nameof(RawCredentialLeakInterlock.ArmingBlocked))!.GetMethod!;
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool found = false;
            foreach (var t in typeof(StockManager.Blazor.BlazorHomeForm).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                foreach (var m in t.GetMethods(All))
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int k = 0; k + 4 < il.Length && !found; k++)
                        if (il[k] is 0x28 or 0x6F)
                            try { found = m.Module.ResolveMethod(BitConverter.ToInt32(il, k + 1)) == getter; } catch (ArgumentException) { }
                }
            Assert.True(found, "BlazorHomeForm 무장 술어가 RawCredentialLeakInterlock.ArmingBlocked 를 판독하지 않음");
        }
    }
}

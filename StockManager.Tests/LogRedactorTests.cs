using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class LogRedactorTests
    {
        private static LogRedactor Fresh() => new LogRedactor();   // Shared 미오염 — 독립 인스턴스

        // ════════════════════════ §1 등록 비밀 정확 일치 ═════════════════════

        [Fact]
        public void RegisteredSecret_IsMasked_AndPlaintextGone()
        {
            const string key = "ABCD1234EFGH5678";
            var r = Fresh();
            r.Register(key);

            var scrubbed = r.Scrub($"KIS 인증 실패: appkey={key} 응답=401");

            Assert.DoesNotContain(key, scrubbed);   // 평문 소거
            Assert.Contains("****", scrubbed);
        }

        [Fact]
        public void RegisteredSecret_InExceptionDump_IsMasked()
        {
            const string token = "tg-bot-SECRET-987654321";
            var r = Fresh();
            r.Register(token);

            // 예외 메시지에 생으로 박힌 비밀 — Log.Error(msg, ex) 의 ex.ToString() 시나리오
            var scrubbed = r.Scrub($"System.Exception: send failed with {token}\n  at Telegram.Push()");

            Assert.DoesNotContain(token, scrubbed);
        }

        [Fact]
        public void MultipleOccurrences_AllMasked()
        {
            const string s = "key-abcdef-123456";
            var r = Fresh();
            r.Register(s);
            var scrubbed = r.Scrub($"{s} ... 재시도 ... {s}");
            Assert.DoesNotContain(s, scrubbed);
        }

        [Fact]
        public void UnregisteredValue_IsNotMasked_NoFalsePositive()
        {
            var r = Fresh();
            r.Register("REGISTERED-SECRET-1");
            const string benign = "주문수량 12345 가격 70000";
            Assert.Equal(benign, r.Scrub(benign));
        }

        [Theory]
        [InlineData("ab")] [InlineData("12345")]   // 6자 미만
        public void ShortSecret_IsNotRegistered(string tooShort)
        {
            var r = Fresh();
            r.Register(tooShort);
            Assert.Equal(0, r.Count);
            Assert.Equal($"value={tooShort}", r.Scrub($"value={tooShort}"));   // 과차폐 없음
        }

        [Fact]
        public void DuplicateRegister_DoesNotGrowCount()
        {
            var r = Fresh();
            r.Register("dup-secret-xyz");
            r.Register("dup-secret-xyz");
            Assert.Equal(1, r.Count);
        }

        [Fact]
        public void LongestFirst_PreventsPartialLeak()
        {
            // 짧은 비밀이 긴 비밀의 부분과 겹쳐도, 긴 것 우선 치환으로 긴 비밀이 온전히 차폐돼야.
            var r = Fresh();
            r.Register("SECRET");              // 6자
            r.Register("SECRETKEY12345");      // 14자 — 앞부분이 위 비밀을 포함
            var scrubbed = r.Scrub("auth=SECRETKEY12345 done");
            Assert.DoesNotContain("SECRETKEY12345", scrubbed);
            Assert.DoesNotContain("KEY12345", scrubbed);   // 부분 누출 없음
        }

        // ════════════════════════ §2 라벨 패턴(미등록 심층 방어) ═════════════

        [Theory]
        [InlineData("AppKey=PLAINTEXTKEY123")]
        [InlineData("api_key: PLAINTEXTKEY123")]
        [InlineData("token = PLAINTEXTKEY123")]
        [InlineData("password=PLAINTEXTKEY123")]
        public void LabeledSecret_ValueMasked_LabelPreserved(string line)
        {
            var scrubbed = Fresh().Scrub(line);
            Assert.DoesNotContain("PLAINTEXTKEY123", scrubbed);
            Assert.Contains("****", scrubbed);
        }

        [Fact]
        public void LabeledSecret_QuotedValue_Masked()
        {
            var scrubbed = Fresh().Scrub("secret=\"hunter2hunter2\"");
            Assert.DoesNotContain("hunter2hunter2", scrubbed);
        }

        [Fact]
        public void NonSecretLabel_ShortValue_NotMasked()
        {
            // id=42 같은 비밀 아닌 짧은 값은 건드리지 않음(값 4자 미만)
            const string line = "orderId=42 status=ok";
            Assert.Equal(line, Fresh().Scrub(line));
        }

        // ════════════════════════ §3 견고성 ══════════════════════════════════

        [Fact]
        public void Scrub_Null_ReturnsEmpty_NoThrow()
            => Assert.Equal("", Fresh().Scrub(null));

        [Fact]
        public void Scrub_Empty_ReturnsEmpty()
            => Assert.Equal("", Fresh().Scrub(""));

        [Fact]
        public void Scrub_NoSecrets_PassesThrough()
        {
            const string line = "[INFO] 정상 기동 — 보유 12종목, 평가액 1.2억";
            Assert.Equal(line, Fresh().Scrub(line));
        }

        [Fact]
        public void Register_NullOrBlank_Ignored()
        {
            var r = Fresh();
            r.Register(null);
            r.Register("   ");
            Assert.Equal(0, r.Count);
        }

        [Fact]
        public void Clear_RemovesAllSecrets()
        {
            var r = Fresh();
            r.Register("some-secret-value");
            r.Clear();
            Assert.Equal(0, r.Count);
            Assert.Equal("x=some-secret-value", r.Scrub("x=some-secret-value"));
        }

        // ════════════════════════ §4 실 시나리오 (계좌번호) ══════════════════

        [Fact]
        public void AccountNumber_Registered_IsMaskedInLogLine()
        {
            const string acct = "5012345601";   // 10자리 계좌번호
            var r = Fresh();
            r.Register(acct);
            var scrubbed = r.Scrub($"[KIS] 주문 전송 계좌 {acct} 종목 005930 수량 10");
            Assert.DoesNotContain(acct, scrubbed);
            Assert.Contains("005930", scrubbed);   // 비-비밀 토큰은 보존
        }
    }
}

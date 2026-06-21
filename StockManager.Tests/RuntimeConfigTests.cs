using System;
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class RuntimeConfigTests
    {
        // 키→값 딕셔너리 주입 (부재 시 null) — 결정론·I/O 0
        private static RuntimeConfig Cfg(params (string k, string? v)[] pairs)
        {
            var d = new Dictionary<string, string?>();
            foreach (var (k, v) in pairs) d[k] = v;
            return new RuntimeConfig(key => d.TryGetValue(key, out var x) ? x : null);
        }

        // ════════════════════════ §1 Bool ════════════════════════════════════

        [Theory]
        [InlineData("true")] [InlineData("TRUE")] [InlineData("1")]
        [InlineData("yes")]  [InlineData("on")]   [InlineData(" On ")]
        public void GetBool_TruthyVariants(string raw)
            => Assert.True(Cfg(("K", raw)).GetBool("K", fallback: false));

        [Theory]
        [InlineData("false")] [InlineData("0")] [InlineData("no")] [InlineData("off")]
        public void GetBool_FalsyVariants(string raw)
            => Assert.False(Cfg(("K", raw)).GetBool("K", fallback: true));

        [Theory]
        [InlineData("maybe")] [InlineData("")] [InlineData("   ")] [InlineData(null)]
        public void GetBool_GarbageOrMissing_ReturnsFallback(string? raw)
        {
            Assert.True(Cfg(("K", raw)).GetBool("K", fallback: true));
            Assert.False(Cfg(("K", raw)).GetBool("K", fallback: false));
        }

        [Fact]
        public void GetBool_AbsentKey_ReturnsFallback()
            => Assert.True(Cfg().GetBool("MISSING", fallback: true));

        // ════════════════════════ §2 Int ═════════════════════════════════════

        [Fact]
        public void GetInt_ParsesAndPassesThroughWhenInRange()
            => Assert.Equal(42, Cfg(("K", "42")).GetInt("K", fallback: 0, min: 0, max: 100));

        [Fact]
        public void GetInt_ClampsAboveMax()
            => Assert.Equal(100, Cfg(("K", "9999")).GetInt("K", fallback: 0, min: 0, max: 100));

        [Fact]
        public void GetInt_ClampsBelowMin()
            => Assert.Equal(10, Cfg(("K", "-5")).GetInt("K", fallback: 0, min: 10, max: 100));

        [Theory]
        [InlineData("notanumber")] [InlineData("4.2")] [InlineData("")] [InlineData(null)]
        public void GetInt_BadOrMissing_ReturnsFallbackClamped(string? raw)
        {
            // fallback 7 은 [0,100] 내 → 7
            Assert.Equal(7, Cfg(("K", raw)).GetInt("K", fallback: 7, min: 0, max: 100));
            // fallback 자체도 바운드 밖이면 클램프
            Assert.Equal(100, Cfg(("K", raw)).GetInt("K", fallback: 9999, min: 0, max: 100));
        }

        // ════════════════════════ §3 Double ══════════════════════════════════

        [Fact]
        public void GetDouble_ParsesAndClamps()
        {
            Assert.Equal(1.5, Cfg(("K", "1.5")).GetDouble("K", 0, 0, 10), 6);
            Assert.Equal(10.0, Cfg(("K", "999.9")).GetDouble("K", 0, 0, 10), 6);
        }

        [Theory]
        [InlineData("NaN")] [InlineData("Infinity")] [InlineData("xyz")] [InlineData(null)]
        public void GetDouble_NonFiniteOrBad_ReturnsFallback(string? raw)
            => Assert.Equal(3.14, Cfg(("K", raw)).GetDouble("K", 3.14, -100, 100), 6);

        // ════════════════════════ §4 String ══════════════════════════════════

        [Fact]
        public void GetString_PresentValue_Trimmed()
            => Assert.Equal("hello", Cfg(("K", "  hello  ")).GetString("K", "fb"));

        [Theory]
        [InlineData("")] [InlineData("   ")] [InlineData(null)]
        public void GetString_BlankOrMissing_ReturnsFallback(string? raw)
            => Assert.Equal("fb", Cfg(("K", raw)).GetString("K", "fb"));

        // ════════════════════════ §5 Redact (런타임 보안) ════════════════════

        [Fact]
        public void Redact_NullOrEmpty_ShowsNone()
        {
            Assert.Equal("(none)", RuntimeConfig.Redact(null));
            Assert.Equal("(none)", RuntimeConfig.Redact(""));
        }

        [Theory]
        [InlineData("a")] [InlineData("abcd")]
        public void Redact_ShortSecret_FullyMasked(string s)
            => Assert.Equal("****", RuntimeConfig.Redact(s));

        [Fact]
        public void Redact_LongSecret_ShowsOnlyEndpoints_MiddleMasked()
        {
            const string secret = "SK-LIVE-abcdefghijklmnop";   // 24자
            var masked = RuntimeConfig.Redact(secret);

            Assert.StartsWith("SK", masked);          // 앞 2자만 노출
            Assert.EndsWith("op", masked);            // 뒤 2자만 노출
            Assert.Contains("*", masked);             // 중간 차폐
            Assert.DoesNotContain("LIVE", masked);    // 본문 비노출
            Assert.DoesNotContain(secret, masked);    // 평문 전체 비노출
        }

        // ════════════════════════ §6 안전 불변식 ═════════════════════════════

        [Fact]
        public void LiveTrading_IsNeverConfigurable()
        {
            // 설정 격리 계층이 거래 방아쇠로 새지 않는다 — liveArmed 는 코드 게이트 전용
            Assert.False(Cfg().LiveTradingConfigurable);
        }

        [Fact]
        public void NullProvider_Throws()
            => Assert.Throws<ArgumentNullException>(() => new RuntimeConfig(null!));

        [Fact]
        public void ThrowingProvider_IsAbsorbed_ReturnsFallback()
        {
            // provider 장애가 안전을 깨뜨리지 않는다 — 예외 흡수 후 폴백
            var cfg = new RuntimeConfig(_ => throw new InvalidOperationException("boom"));
            Assert.True(cfg.GetBool("K", fallback: true));
            Assert.Equal(99, cfg.GetInt("K", fallback: 99));
            Assert.Equal("fb", cfg.GetString("K", "fb"));
        }
    }
}

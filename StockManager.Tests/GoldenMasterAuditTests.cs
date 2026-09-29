// ============================================================================
// GoldenMasterAuditTests — v12.13.0 [Step 7/7] 시스템 설정 전수 오디팅 & Production Golden Master 최종 동결
//   Section 1: 65개 Port 인터페이스 전수 DI 스캔 — IOrderBookPort(v12.3.0) 유일 미등록 확인·최초 결속.
//   Think Beyond A: ConfigHotSwapSanitizer(δ=0 경계 교정). Think Beyond B: GoldenMasterSealVerificationSentinel
//     (SHA-256 어셈블리 무결성 + liveArmed:false 결합 검증).
//   ★SettingsPage.razor 감사 결과: 6곳의 Math.Clamp 호출 모두 이미 정확(결함 無) — 정직 명기, 불필요한 리팩터링
//   강행 없음(0 Concrete Ref 제약상 Portable 컴포넌트가 Core 엔진을 직접 호출할 수도 없음).
// ============================================================================
using System;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class GoldenMasterAuditTests
    {
        private readonly ITestOutputHelper _out;
        public GoldenMasterAuditTests(ITestOutputHelper o) => _out = o;

        // ══ ConfigHotSwapSanitizer(Think Beyond A) ═════════════════════════════
        [Fact]
        public void Sanitize_WithinRange_Unchanged()
            => Assert.Equal(50, ConfigHotSwapSanitizer.Sanitize(50, new ConfigBoundary(1, 65535, 8787)));

        [Fact]
        public void Sanitize_BelowMin_ClampsToMin()
            => Assert.Equal(1, ConfigHotSwapSanitizer.Sanitize(-100, new ConfigBoundary(1, 65535, 8787)));

        [Fact]
        public void Sanitize_AboveMax_ClampsToMax()
            => Assert.Equal(65535, ConfigHotSwapSanitizer.Sanitize(999_999, new ConfigBoundary(1, 65535, 8787)));

        [Fact]
        public void Sanitize_ContradictoryBoundary_FallsBackToDefault()
            => Assert.Equal(8787, ConfigHotSwapSanitizer.Sanitize(50, new ConfigBoundary(100, 1, 8787)));   // Min>Max

        [Fact]
        public void WasClamped_DetectsChange()
        {
            Assert.False(ConfigHotSwapSanitizer.WasClamped(50, 50));
            Assert.True(ConfigHotSwapSanitizer.WasClamped(-1, 1));
        }

        [Fact]
        public void SanitizeBatch_AppliesPerElementBoundary()
        {
            long[] values = { -1, 50, 999_999 };
            var bounds = new[]
            {
                new ConfigBoundary(1, 65535, 8787),
                new ConfigBoundary(1, 168, 24),
                new ConfigBoundary(0, 23, 6),
            };
            var results = new long[3];
            ConfigHotSwapSanitizer.SanitizeBatch(values, bounds, results);
            Assert.Equal(1, results[0]);
            Assert.Equal(50, results[1]);
            Assert.Equal(23, results[2]);
        }

        [Fact]
        public void SanitizeBatch_MismatchedLength_Throws()
        {
            long[] values = { 1, 2 };
            var bounds = new[] { new ConfigBoundary(0, 10, 5) };
            var results = new long[2];
            Assert.Throws<ArgumentException>(() => ConfigHotSwapSanitizer.SanitizeBatch(values, bounds, results));
        }

        // ══ GoldenMasterSealVerificationSentinel(Think Beyond B) ═══════════════
        [Fact]
        public void ComputeSha256Hex_DeterministicForSameFile()
        {
            string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string h1 = GoldenMasterSealVerificationSentinel.ComputeSha256Hex(path);
            string h2 = GoldenMasterSealVerificationSentinel.ComputeSha256Hex(path);
            Assert.Equal(h1, h2);
            Assert.Equal(64, h1.Length);   // SHA-256 = 32B = 64 hex chars
        }

        [Fact]
        public void Verify_MatchingHash_LiveArmedFalse_Passes()
        {
            string hash = new string('A', 64);
            Assert.True(GoldenMasterSealVerificationSentinel.Verify(hash, hash, liveArmed: false));
        }

        [Fact]
        public void Verify_MismatchedHash_Fails()
        {
            string a = new string('A', 64);
            string b = new string('B', 64);
            Assert.False(GoldenMasterSealVerificationSentinel.Verify(a, b, liveArmed: false));
        }

        [Fact]
        public void Verify_LiveArmedTrue_AlwaysFails_EvenIfHashMatches()
        {
            string hash = new string('A', 64);
            Assert.False(GoldenMasterSealVerificationSentinel.Verify(hash, hash, liveArmed: true));
        }

        [Fact]
        public void Verify_MalformedHex_FailsClosed()
        {
            Assert.False(GoldenMasterSealVerificationSentinel.Verify("not-hex-!!", new string('A', 64), liveArmed: false));
        }

        [Fact]
        public void VerifyFile_RealAssembly_MatchesComputedHash()
        {
            string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string expected = GoldenMasterSealVerificationSentinel.ComputeSha256Hex(path);
            Assert.True(GoldenMasterSealVerificationSentinel.VerifyFile(path, expected, liveArmed: false));
        }

        [Fact]
        public void VerifyFile_MissingFile_FailsClosed_NoThrow()
        {
            Assert.False(GoldenMasterSealVerificationSentinel.VerifyFile(
                @"D:\nonexistent\path\ghost.dll", new string('A', 64), liveArmed: false));
        }

        // ══ Section 1 — DI 완전성 스캔 결과 회귀 고정(문서화 목적) ══════════════
        [Fact]
        public void IOrderBookPort_TypesExist_And_AdapterConstructible()
        {
            // v12.13.0 이전엔 이 Port 가 DI 미등록이었음(실측) — 타입 자체는 항상 존재했으므로 여기서는 어댑터가
            // 여전히 정상 구성 가능함을(회귀 방지) 확인한다. 실제 DI 등록 검증은 BlazorHomeForm.cs 컴파일+런타임
            // 스모크(사중 빌드)로 실증(단위테스트로 ServiceProvider 를 재구성하지 않음 — Host 조립 로직 중복 방지).
            var engine = new OrderBookDepthMatrixCoalescer(depth: 10, capacityPow2: 4);
            var adapter = new OrderBookAdapter("005930", engine);
            Assert.False(adapter.Snapshot().HasData);   // 아직 Publish 없음 — 정직 렌더
        }
    }
}

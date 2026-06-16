// ============================================================================
// CartridgeTests - 전략 카트리지 서명/증명서 (순수). 무신뢰 검증의 핵심 회귀 방지.
// ============================================================================
using System.Collections.Generic;
using System.Text;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class CartridgeTests
    {
        // ── ECDsa 서명 ──────────────────────────────────────────────────
        [Fact]
        public void Crypto_Sign_Verify_RoundTrip_And_Reject()
        {
            var (priv, pub) = CartridgeCrypto.GenerateKeyPair();
            var msg = Encoding.UTF8.GetBytes("hello cartridge");
            var sig = CartridgeCrypto.Sign(msg, priv);

            Assert.True(CartridgeCrypto.Verify(msg, sig, pub));
            Assert.False(CartridgeCrypto.Verify(Encoding.UTF8.GetBytes("hello cartridgX"), sig, pub)); // 메시지 변조
            var (_, pub2) = CartridgeCrypto.GenerateKeyPair();
            Assert.False(CartridgeCrypto.Verify(msg, sig, pub2));    // 다른 키
            Assert.False(CartridgeCrypto.Verify(msg, "not-base64!!", pub)); // 깨진 서명
        }

        // ── 결정론 백테스트 증명서 ──────────────────────────────────────
        [Fact]
        public void Attestation_VerifiesAndDetectsTamper()
        {
            var s = CartridgeEngine.Attest("MA 10/30", 10, 30, 3);
            Assert.True(CartridgeEngine.VerifyStrategy(s));
            Assert.False(CartridgeEngine.VerifyStrategy(s with { AttestTrades = s.AttestTrades + 1 }));
            Assert.False(CartridgeEngine.VerifyStrategy(s with { AttestTotalReturnPct = s.AttestTotalReturnPct + 25 }));
            Assert.False(CartridgeEngine.VerifyStrategy(s with { Short = 7 }));   // 다른 스펙 → 주장 성과 재현 실패
        }

        [Fact]
        public void ReferenceSeries_IsDeterministic()
        {
            var a = CartridgeEngine.Backtest(5, 20, 99);
            var b = CartridgeEngine.Backtest(5, 20, 99);
            Assert.Equal(a, b);   // 동일 시드 → 동일 성과
        }

        // ── 카트리지 서명 + 검증 ────────────────────────────────────────
        private static (Cartridge signed, string priv, string pub) MakeSigned()
        {
            var (priv, pub) = CartridgeCrypto.GenerateKeyPair();
            var s1 = CartridgeEngine.Attest("MA 5/20", 5, 20, 7);
            var cart = new Cartridge(CartridgeEngine.FormatVersion, "pack1", "내 전략팩", "alice", "2026-01-01",
                pub, "데모", new() { new CartMacro("risk", "005930 RISK") }, new() { s1 }, new(), "");
            return (CartridgeEngine.Sign(cart, priv), priv, pub);
        }

        [Fact]
        public void Cartridge_Signed_FullyTrusted()
        {
            var (signed, _, _) = MakeSigned();
            var v = CartridgeEngine.Verify(signed);
            Assert.True(v.SignatureOk);
            Assert.True(v.AttestationOk);
            Assert.True(v.FullyTrusted);
            Assert.Equal(1, v.VerifiedStrategies);
        }

        [Fact]
        public void Cartridge_TamperAfterSign_BreaksSignature()
        {
            var (signed, _, _) = MakeSigned();
            var tampered = signed with { Name = "해킹된 팩" };
            Assert.False(CartridgeEngine.Verify(tampered).SignatureOk);
        }

        [Fact]
        public void Cartridge_FakeMetric_EvenIfReSigned_FailsAttestation()
        {
            var (signed, priv, _) = MakeSigned();
            var s = signed.Strategies[0];
            // 거짓 성과로 바꿔치고 '자기 키로 다시 서명'(유효 서명)
            var lying = signed with { Strategies = new() { s with { AttestTotalReturnPct = s.AttestTotalReturnPct + 80 } } };
            var lyingSigned = CartridgeEngine.Sign(lying, priv);

            var v = CartridgeEngine.Verify(lyingSigned);
            Assert.True(v.SignatureOk);      // 서명 자체는 유효
            Assert.False(v.AttestationOk);   // 그러나 재실행 대조에서 거짓 발각 — 무신뢰 검증의 핵심
            Assert.False(v.FullyTrusted);
        }
    }
}

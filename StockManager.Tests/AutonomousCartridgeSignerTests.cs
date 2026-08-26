// ============================================================================
// AutonomousCartridgeSignerTests — v4.86.0 과제2: 금고 연동 자율 서명 + fail-closed 폐기
//
//  §1 zero-alloc 서명(2) — SignZeroAlloc↔Verify 호환·변조 거부.
//  §2 서명/폐기(5)       — BuildAndSign 성공·Sign 성공+검증·키 유실 폐기·동결 폐기·성공률.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace StockManager.Tests
{
    public sealed class AutonomousCartridgeSignerTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;

        public AutonomousCartridgeSignerTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"signer_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
        }
        public void Dispose() { _db.Dispose(); try { File.Delete(_dbPath); } catch { } }

        private SecurityAssetVault Vault(OrderAttestationService att) => new(_db, att, () => false);
        private AutonomousCartridgeSigner Signer(OrderAttestationService att) => new(_db, Vault(att), att);
        private static List<CartStrategy> Strat() => new() { CartridgeEngine.Attest("MA5/20", 5, 20, 5020) };

        // ── §1 zero-alloc 서명 크로스-호환 ────────────────────────────────────

        [Fact]
        public void SignZeroAlloc_VerifiesWithBclVerify()
        {
            var (priv, pub) = CartridgeCrypto.GenerateKeyPair();
            byte[] msg = Encoding.UTF8.GetBytes("hello evolution v4.86");
            string sig = CartridgeCrypto.SignZeroAlloc(msg, priv.AsSpan());   // SignHash(SHA256)
            Assert.True(CartridgeCrypto.Verify(msg, sig, pub));               // ↔ VerifyData(SHA256) 호환
            Assert.False(CartridgeCrypto.Verify(Encoding.UTF8.GetBytes("tampered"), sig, pub));
        }

        [Fact]
        public void SignZeroAlloc_CorruptKey_Throws()
            => Assert.Throws<FormatException>(() =>
                CartridgeCrypto.SignZeroAlloc(Encoding.UTF8.GetBytes("x"), "not-base64!!".AsSpan()));

        // ── §2 서명 / 폐기 ────────────────────────────────────────────────────

        [Fact]
        public void BuildAndSign_Success_AndVerifies()
        {
            var att = new OrderAttestationService();
            var signer = Signer(att);
            var outcome = signer.BuildAndSign("EVO-pack", "테스트", Strat());
            Assert.True(outcome.IsSigned);
            Assert.NotNull(outcome.Cartridge);
            Assert.True(CartridgeEngine.Verify(outcome.Cartridge!).SignatureOk);   // 서명 검증 통과
            Assert.Equal(1L, signer.Signed);
            Assert.Equal(0L, signer.Trashed);
            Assert.Equal(1.0, signer.SuccessRate, precision: 6);
        }

        [Fact]
        public void Sign_Frozen_TrashedFailClosed()
        {
            var att = new OrderAttestationService();
            var signer = Signer(att);
            CartridgeService.EnsureIdentity(_db);   // 키는 있음
            att.ForceFreeze("테스트 동결");
            var outcome = signer.BuildAndSign("EVO", "x", Strat());
            Assert.False(outcome.IsSigned);
            Assert.Equal(AutonomousCartridgeSigner.SignStatus.TrashedFrozen, outcome.Status);
            Assert.Equal(1L, signer.Trashed);
        }

        [Fact]
        public void Sign_MissingKey_TrashedKeyLost()
        {
            var att = new OrderAttestationService();
            var signer = Signer(att);
            // EnsureIdentity 호출 안 함 → Cartridge.PrivKey 미보관. 미서명 카트리지 직접 Sign.
            var cart = new Cartridge(CartridgeEngine.FormatVersion, "id0000000a", "n", "a", "2026-01-01",
                "", "d", new List<CartMacro>(), Strat(), new List<CartPrompt>(), "");
            var outcome = signer.Sign(cart);
            Assert.False(outcome.IsSigned);
            Assert.Equal(AutonomousCartridgeSigner.SignStatus.TrashedKeyLost, outcome.Status);
            Assert.Equal(1L, signer.Trashed);
        }

        [Fact]
        public void SuccessRate_MixedSignedAndTrashed()
        {
            var att = new OrderAttestationService();
            var signer = Signer(att);
            signer.BuildAndSign("ok", "x", Strat());   // signed
            att.ForceFreeze("freeze");
            signer.BuildAndSign("no", "x", Strat());   // trashed(frozen)
            Assert.Equal(1L, signer.Signed);
            Assert.Equal(1L, signer.Trashed);
            Assert.Equal(0.5, signer.SuccessRate, precision: 6);
        }
    }
}

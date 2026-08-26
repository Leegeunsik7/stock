// ============================================================================
// SecurityAssetVaultTests — 과제 1: DPAPI 금고 + 메모리 와이프 + 동결 래치 + 무장 펜스
//
//  §1 라운드트립/봉인(4) — 저장→열람 정합·리스 Dispose 와이프·미보관 fail-closed·Has.
//  §2 무장 펜스(2)       — 미무장 Reveal 차단·무장 시 허용(liveArmed:false).
//  §3 동결 래치(2)       — EmergencyFreeze 시 Store/Reveal 차단 + 영구 봉인(래치 불가역).
// ============================================================================
using System;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    public sealed class SecurityAssetVaultTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;

        public SecurityAssetVaultTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"vault_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        private SecurityAssetVault Vault(OrderAttestationService att, bool armed)
            => new(_db, att, () => armed);

        // ── §1 라운드트립/봉인 ────────────────────────────────────────────────

        [Fact]
        public void StoreReveal_RoundTrips_WhenArmed()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: true);
            v.StoreString("Toss.ClientSecret", "s3cr3t-토큰-Ω");
            Assert.True(v.Has("Toss.ClientSecret"));

            using var lease = v.Reveal("Toss.ClientSecret");
            Assert.True(lease.IsRevealed);
            Assert.True(lease.Value.SequenceEqual("s3cr3t-토큰-Ω".AsSpan()));
        }

        [Fact]
        public void Lease_Dispose_WipesAndEmpties()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: true);
            v.StoreString("k", "abc");
            var lease = v.Reveal("k");
            Assert.Equal(3, lease.Length);
            lease.Dispose();
            Assert.False(lease.IsRevealed);
            Assert.Equal(0, lease.Value.Length);   // Dispose 후 빈 스팬(버퍼 소거·반환)
            lease.Dispose();                         // 멱등 — 이중 Dispose 안전
        }

        [Fact]
        public void Reveal_MissingKey_FailClosed()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: true);
            Assert.Throws<InvalidOperationException>(() => v.Reveal("nope"));
        }

        [Fact]
        public void EncryptedAtRest_NotPlaintext()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: true);
            v.StoreString("k", "PLAINTEXT_SECRET");
            var stored = _db.GetSetting("k") ?? "";
            Assert.StartsWith("enc:v1:", stored);            // DPAPI 마커
            Assert.DoesNotContain("PLAINTEXT_SECRET", stored); // 평문 미노출
        }

        // ── §2 무장 펜스(liveArmed:false) ────────────────────────────────────

        [Fact]
        public void Reveal_NotArmed_FailClosed()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: false);
            v.StoreString("k", "abc");   // Store 는 무장과 무관(허용)
            Assert.Throws<InvalidOperationException>(() => v.Reveal("k"));   // 미무장 → 복호화 차단
        }

        [Fact]
        public void ArmingPredicateThrows_TreatedAsUnarmed_FailClosed()
        {
            var att = new OrderAttestationService();
            var v = new SecurityAssetVault(_db, att, () => throw new InvalidOperationException("boom"));
            v.StoreString("k", "abc");
            Assert.Throws<InvalidOperationException>(() => v.Reveal("k"));   // 술어 예외 → 미무장 취급
        }

        // ── §3 동결 래치 ──────────────────────────────────────────────────────

        [Fact]
        public void ForceFreeze_SealsVault_BlocksRevealAndStore()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: true);
            v.StoreString("k", "abc");

            att.ForceFreeze("테스트 동결");
            Assert.Throws<InvalidOperationException>(() => v.Reveal("k"));   // 동결 → 복호화 차단
            Assert.True(v.IsSealed);                                          // 동결 연동 래치 트립
            Assert.Throws<InvalidOperationException>(() => v.StoreString("k2", "x"));  // 봉인 → 저장도 차단
        }

        [Fact]
        public void Seal_IsIrreversibleForRuntime()
        {
            var att = new OrderAttestationService();
            var v = Vault(att, armed: true);
            v.StoreString("k", "abc");
            v.Seal();
            Assert.True(v.IsSealed);
            // att 는 정상(동결 아님)이어도, 명시 봉인은 재복호화를 원천 차단.
            Assert.Equal(GatingMode.Normal, att.Mode);
            Assert.Throws<InvalidOperationException>(() => v.Reveal("k"));
        }
    }
}

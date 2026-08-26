// ============================================================================
// AutonomousCartridgeSigner — v4.86.0 과제2: 승격 유전자 자율 ECDsa 서명(금고 연동·메모리 하드닝)
//
//  ⚠️ liveArmed:false 불변 — 서명은 '연구 산출물(.ms2cart)' 생성이며 실주문 송출이 아니다. 따라서 무장과
//     무관하게 수행되나(vault Reveal requireArmed:false), 봉인·EmergencyFreeze 는 fail-closed 로 즉시 폐기.
//
//  ── 흐름 ────────────────────────────────────────────────────────────────────
//   1. EmergencyFreeze 감지 → 서명 취소·'폐기(Trash)' (fail-closed).
//   2. SecurityAssetVault 에서 서명 개인키를 SecretLease(char[] 풀·사용 후 ZeroMemory 와이프)로 로드.
//   3. Cartridge 정준 바이트를 stackalloc[32] SHA256 로 zero-alloc 해시 → ec.SignHash(=SignData(SHA256) 호환).
//   4. 서명 부착 후 즉시 CartridgeEngine.Verify 로 재검증(서명·정준 미스매치면 폐기).
//   5. 서명 중 재동결·키 유실·해시 미스매치 → 해당 유전자 즉시 폐기 격리(모호한 배포 금지).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace MS2E.Infrastructure
{
    public sealed class AutonomousCartridgeSigner
    {
        public enum SignStatus { Signed, TrashedFrozen, TrashedKeyLost, TrashedVerifyFail, TrashedError }

        /// <summary>서명 결과. Signed 면 Cartridge 채워짐, 그 외는 폐기(Cartridge=null)+사유.</summary>
        public readonly record struct SignOutcome(SignStatus Status, Cartridge? Cartridge, string Reason)
        {
            public bool IsSigned => Status == SignStatus.Signed && Cartridge is not null;
        }

        /// <summary>서명 개인키의 vault/DB 키 이름(CartridgeService.EnsureIdentity 와 동일 슬롯).</summary>
        public const string PrivKeyName = "Cartridge.PrivKey";

        private readonly LocalDatabase           _db;
        private readonly SecurityAssetVault      _vault;
        private readonly OrderAttestationService _att;

        private long _signed;
        private long _trashed;

        public AutonomousCartridgeSigner(LocalDatabase db, SecurityAssetVault vault, OrderAttestationService attestation)
        {
            _db    = db    ?? throw new ArgumentNullException(nameof(db));
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            _att   = attestation ?? throw new ArgumentNullException(nameof(attestation));
        }

        public long   Signed      => Interlocked.Read(ref _signed);
        public long   Trashed     => Interlocked.Read(ref _trashed);
        /// <summary>서명 성공률(0..1). 시도 0이면 0.</summary>
        public double SuccessRate { get { long s = Signed, t = Trashed, n = s + t; return n == 0 ? 0.0 : (double)s / n; } }

        /// <summary>전략 목록으로 미서명 Cartridge 를 구성(신원 키 보장)한 뒤 서명. 편의 진입점(파이프라인용).</summary>
        public SignOutcome BuildAndSign(string name, string desc, List<CartStrategy> strategies)
        {
            if (_att.Mode == GatingMode.EmergencyFreeze)
                return Trash(SignStatus.TrashedFrozen, "EmergencyFreeze — 서명 취소·폐기(fail-closed)");
            try
            {
                CartridgeService.EnsureIdentity(_db);                       // 키쌍 보장(최초 1회 생성)
                string pub = _db.GetSetting("Cartridge.PubKey") ?? "";
                var cart = new Cartridge(
                    CartridgeEngine.FormatVersion, Guid.NewGuid().ToString("N")[..10],
                    string.IsNullOrWhiteSpace(name) ? "진화전략팩" : name.Trim(),
                    "AutonomousEvolution", DateTime.Now.ToString("yyyy-MM-dd"),
                    pub, desc ?? "",
                    new List<CartMacro>(), strategies ?? new List<CartStrategy>(), new List<CartPrompt>(), "");
                return Sign(cart);
            }
            catch (Exception ex) { return Trash(SignStatus.TrashedError, "구성/서명 오류 — " + ex.Message); }
        }

        /// <summary>미서명 Cartridge(AuthorPubKey 설정됨)에 금고 키로 ECDsa 서명·재검증. 실패 시 폐기.</summary>
        public SignOutcome Sign(Cartridge unsigned)
        {
            // (1) fail-closed: 동결 시 서명 취소·폐기.
            if (_att.Mode == GatingMode.EmergencyFreeze)
                return Trash(SignStatus.TrashedFrozen, "EmergencyFreeze — 서명 취소·폐기(fail-closed)");
            try
            {
                byte[] canonical = CartridgeEngine.CanonicalBytes(unsigned);

                // (2) 금고에서 개인키 로드(SecretLease·requireArmed:false — 서명은 주문 송출 아님).
                string sig;
                SecretLease lease;
                try { lease = _vault.Reveal(PrivKeyName, requireArmed: false); }
                catch (Exception ex) { return Trash(SignStatus.TrashedKeyLost, "서명 키 유실/봉인 — " + ex.Message); }
                using (lease)   // ★using 종료 시 char 버퍼 ZeroMemory 와이프
                {
                    if (!lease.IsRevealed || lease.Length == 0)
                        return Trash(SignStatus.TrashedKeyLost, "빈 서명 키 — 폐기");
                    // (3) zero-alloc SHA256 + SignHash.
                    sig = CartridgeCrypto.SignZeroAlloc(canonical, lease.Value);
                }

                var signed = unsigned with { Signature = sig };

                // (4) 즉시 재검증 — 서명/정준 미스매치면 폐기.
                var verdict = CartridgeEngine.Verify(signed);
                if (!verdict.SignatureOk)
                    return Trash(SignStatus.TrashedVerifyFail, "서명 검증 미스매치 — 변조/키 불일치 폐기");

                // (5) 서명 중 재동결 방어 — 배포 직전 재확인.
                if (_att.Mode == GatingMode.EmergencyFreeze)
                    return Trash(SignStatus.TrashedFrozen, "서명 중 EmergencyFreeze — 폐기(fail-closed)");

                Interlocked.Increment(ref _signed);
                return new SignOutcome(SignStatus.Signed, signed, "서명 완료·검증 통과");
            }
            catch (Exception ex) { return Trash(SignStatus.TrashedError, "서명 오류 — " + ex.Message); }
        }

        private SignOutcome Trash(SignStatus status, string reason)
        {
            Interlocked.Increment(ref _trashed);
            return new SignOutcome(status, null, reason);
        }
    }
}

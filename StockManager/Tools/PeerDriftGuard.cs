// ============================================================================
// PeerDriftGuard — [v12.18.4-GM · 방향 B] 피어 철회 드리프트 방어(PeerRevocationDriftSafeguard) — 브랜치 이탈 · HEAD 1커밋 전진 · 봉인 매니페스트 변경
//
//  배경: release-package.ps1 [0/10] 은 피어(QuantumVault) HEAD · 봉인 매니페스트 SHA-256 을 고정(peerPinHead/peerPinSha)하고 [10/10] 동기화 ·
//    핸드셰이크가 그 고정값으로 대조한다. 두 빈틈이 남았다:
//     ⓐ 브랜치는 고정하지 않았다 — 피어 세션이 같은 커밋을 가리키는 다른 브랜치로 전환(checkout -b · switch)해도 HEAD 해시는 같아 통과.
//        그 순간부터 피어의 '다음 커밋'은 출하 기록이 가리키는 계보 밖으로 간다(교차 봉인이 묶은 브랜치 = 운용 중인 브랜치 보증 붕괴).
//     ⓑ 대조가 [10/10] 에만 있다 — [0/10]~[7/10] 의 수십 분(빌드 · 전체 스위트) 동안 피어가 움직여도 zip 은 먼저 만들어진다
//        (핸드셰이크에서 폐기되긴 하지만, 그 사이 산출물이 디스크에 존재 — '드리프트 시 zip 사출 전 중단'이 아니다).
//  이 가드는 [0/10] 고정값(브랜치 · HEAD · 매니페스트 SHA-256)을 받아 현재 피어를 다시 읽고 1비트라도 다르면 거부한다(전부 읽기 전용):
//   ① 저장소 최상위 == 지정 경로(감싸는 저장소 오판 차단 — PeerReadinessProbe 와 같은 규칙).
//   ② 브랜치 = symbolic-ref --short HEAD == 고정 브랜치. 분리 HEAD(detached) = 거부 — 출하가 묶을 계보가 없다.
//   ③ HEAD = rev-parse HEAD == 고정 40 hex(대소문자 무시 · 1커밋 전진/후퇴/리베이스 전부 거부).
//   ④ 봉인 매니페스트(release/offline-bundle/RELEASE_MANIFEST.json · gitignore 산출물) SHA-256 == 고정 64 hex — 재봉인/삭제 = 거부.
//   ⑤ 미커밋 코드 0(GitRepoProbe.IsCodePath — 스크립트 [0/10] 과 같은 규칙) — HEAD 는 그대로지만 작업 트리가 바뀐 피어 = 거부.
//  판정 순서는 위 번호 그대로이며 첫 위반만 보고한다(사유 1개 · 결정적). 판독 실패 = 해당 위반(fail-closed — 확인 불가 = 이동으로 간주).
//  CLI: --peer-drift <피어 루트> --pin-peer-branch <b> --pin-peer-head <40hex> --pin-peer-manifest <64hex> [--peer-manifest <상대 경로>]
//       → 0 = 고정 그대로 · 2 = 드리프트(사유) · 64 = 사용 오류(고정값 형식 이탈 포함). 스크립트는 [7/10] zip 직전 · [10/10] 동기화 전 · 완료 직전 3회 호출.
//  ★비파괴: git 은 GitRepoProbe.Git(--no-optional-locks) — 피어 .git/index 쓰기 0. 매니페스트는 해시만(파싱 · 내용 출력 0).
//  콜드 경로(출하 게이트 · git 프로세스 호출) — 할당 무관.
// ============================================================================
using System;
using System.IO;
using System.Security.Cryptography;

namespace StockManager.Tools
{
    public enum PeerDriftVerdict : byte { Ok = 0, NotARepository = 1, BranchDetached = 2, BranchSwitched = 3, HeadDrift = 4, ManifestDrift = 5, UncommittedCode = 6 }

    public sealed record PeerDriftReport(PeerDriftVerdict Verdict, string Detail, string? Branch, string? Head, string? ManifestSha256)
    {
        public bool Ok => Verdict == PeerDriftVerdict.Ok;
    }

    public static class PeerDriftGuard
    {
        public const string DriftFlag = "--peer-drift";

        /// <summary>고정 브랜치 이름 형식(git 참조 이름 부분집합 — 영숫자 · '/' '-' '_' '.' · 앞 '-' 금지 · '..' 금지). 셸/인자 주입 차단.</summary>
        public static bool IsSafeBranch(string? b)
        {
            if (string.IsNullOrEmpty(b) || b.Length > 200 || b[0] == '-' || b.Contains("..", StringComparison.Ordinal) || b.EndsWith('/') || b.EndsWith(".lock", StringComparison.Ordinal)) return false;
            foreach (char c in b) if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')) return false;
            return true;
        }

        public static bool IsHex(string? s, int len)
        {
            if (s is null || s.Length != len) return false;
            foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        /// <summary>현재 피어 브랜치(분리 HEAD = null) · HEAD · 매니페스트 SHA-256(없으면 null) 판독 — [0/10] 고정값 생성과 같은 판독기.</summary>
        public static (string? Branch, string? Head, string? ManifestSha) Observe(string peerRoot, string manifestRel = PeerReadinessProbe.DefaultManifest)
        {
            string? top = Directory.Exists(peerRoot) ? GitRepoProbe.Git(peerRoot, "rev-parse", "--show-toplevel") : null;
            if (top is null || !PeerReadinessProbe.SamePath(top, peerRoot)) return (null, null, null);
            string? head = GitRepoProbe.Git(peerRoot, "rev-parse", "HEAD");
            string? branch = GitRepoProbe.Git(peerRoot, "symbolic-ref", "--short", "-q", "HEAD");   // 분리 HEAD = 종료 1 → null
            string mp = Path.Combine(peerRoot, manifestRel.Replace('/', Path.DirectorySeparatorChar));
            string? sha = null;
            try { if (File.Exists(mp)) using (var fs = File.OpenRead(mp)) sha = Convert.ToHexString(SHA256.HashData(fs)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sha = null; }
            return (string.IsNullOrEmpty(branch) ? null : branch, head, sha);
        }

        /// <summary>[0/10] 고정값 ↔ 현재 피어 대조(첫 위반 1개 보고). 고정값 형식은 호출측(CLI)이 검증한다.</summary>
        public static PeerDriftReport Check(string peerRoot, string pinBranch, string pinHead, string pinManifestSha, string manifestRel = PeerReadinessProbe.DefaultManifest)
        {
            var (branch, head, sha) = Observe(peerRoot, manifestRel);
            PeerDriftReport R(PeerDriftVerdict v, string d) => new(v, d, branch, head, sha);
            if (head is null) return R(PeerDriftVerdict.NotARepository, "git 저장소 최상위 아님/HEAD 판독 실패: " + peerRoot);
            if (branch is null) return R(PeerDriftVerdict.BranchDetached, $"분리 HEAD — 고정 브랜치 '{pinBranch}' 이탈(출하가 묶을 계보 없음)");
            if (!string.Equals(branch, pinBranch, StringComparison.Ordinal)) return R(PeerDriftVerdict.BranchSwitched, $"브랜치 '{branch}' ≠ 고정 '{pinBranch}'");
            if (!string.Equals(head, pinHead, StringComparison.OrdinalIgnoreCase)) return R(PeerDriftVerdict.HeadDrift, $"HEAD {head} ≠ 고정 {pinHead}");
            if (sha is null || !string.Equals(sha, pinManifestSha, StringComparison.OrdinalIgnoreCase))
                return R(PeerDriftVerdict.ManifestDrift, sha is null ? "봉인 매니페스트 없음/판독 실패: " + manifestRel : $"봉인 매니페스트 SHA-256 {sha} ≠ 고정 {pinManifestSha}");
            var dirty = GitRepoProbe.StatusPaths(peerRoot);
            if (dirty.Count > 0) return R(PeerDriftVerdict.UncommittedCode, dirty.Count + "건(예: " + dirty[0] + ")");
            return R(PeerDriftVerdict.Ok, $"브랜치 {branch} · HEAD {head} · 매니페스트 {sha} = [0/10] 고정 · 미커밋 코드 0");
        }
    }
}

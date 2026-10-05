// ============================================================================
// AssemblySealGenerator — v12.14.0-GM Production Golden Master 봉인 & 배포 패키징 자동화
//   Section 1: Assembly Cryptographic Seal & Manifest Generator
//
//   사중 빌드(Host/WebOnly/PrimesBench/x86 KiwoomProxy) 중 실제로 '동시 출하'되는 산출물은 Host(win-x64)
//   + KiwoomProxy(win-x86) 뿐이다(WebOnly·PrimesBench 는 CI 검증 전용 대체/벤치 빌드로, build-installer.ps1 도
//   이 둘만 배포한다 — 정직 스코핑, 4종을 전부 하나의 배포 매니페스트에 넣는다고 과장하지 않는다).
//
//   기존 ReleasePackager(v6.2.0)가 이미 genesis-봉인(CausalAuditVault 헤드 해시 결합) SHA-256 마스터 매니페스트
//   생성/검증을 구현하고 있으므로, 이 클래스는 해시 알고리즘을 재구현하지 않고 그 위에 '배포 대상 아티팩트 집합
//   해석'만 얹는 얇은 조립기다(중복 암호 로직 0 — DRY).
//
//   불변: 결정론(파일 바이트 순수 해시·시각/난수 0) · liveArmed:false(IBroker·소켓 참조 0) ·
//        fail-closed(아티팩트 누락/접근 실패 → 예외 전파, 부분 매니페스트로 위장하지 않음).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;

namespace MS2E.Infrastructure
{
    /// <summary>배포 대상(Host win-x64 + KiwoomProxy win-x86) 아티팩트 집합을 해석해 ReleasePackager 로
    /// genesis-봉인 매니페스트(manifest.sig)를 생성/검증하는 조립기. 해시/봉인 알고리즘 자체는 ReleasePackager 위임.</summary>
    public static class AssemblySealGenerator
    {
        /// <summary>Host(win-x64) 퍼블리시 출력 폴더에서 실제 출하 아티팩트(*.dll/*.exe)만 골라 반환(정렬).
        /// pdb/xml/json 등 비-바이너리 산출물은 봉인 대상에서 제외(코드 무결성만 증명 — 문서/설정은 별도 관심사).</summary>
        public static IReadOnlyList<string> ResolveHostArtifacts(string hostPublishDir)
        {
            if (!Directory.Exists(hostPublishDir))
                throw new DirectoryNotFoundException($"Host publish dir not found: {hostPublishDir}");
            return CollectBinaries(hostPublishDir);
        }

        /// <summary>KiwoomProxy(win-x86) 퍼블리시 출력 폴더에서 아티팩트 수집(위와 동일 규칙).</summary>
        public static IReadOnlyList<string> ResolveKiwoomProxyArtifacts(string proxyPublishDir)
        {
            if (!Directory.Exists(proxyPublishDir))
                throw new DirectoryNotFoundException($"KiwoomProxy publish dir not found: {proxyPublishDir}");
            return CollectBinaries(proxyPublishDir);
        }

        /// <summary>Host + KiwoomProxy 아티팩트를 합쳐 genesis-봉인 manifest.sig 를 생성해 기록한다.
        /// 반환값은 기록된 JSON 본문(호출측 로그/감사 기록용).</summary>
        public static string GenerateReleaseSeal(string version, string hostPublishDir, string proxyPublishDir, string outManifestPath)
        {
            var artifacts = NamedArtifacts(hostPublishDir, proxyPublishDir);
            if (artifacts.Count == 0)
                throw new InvalidOperationException("배포 아티팩트 0건 — 퍼블리시 선행 필요(fail-closed: 빈 매니페스트 봉인 금지).");
            return ReleasePackager.WriteManifest(version, artifacts, outManifestPath);   // [Phase 4] v2 역할 한정 명명 봉인
        }

        /// <summary>기록된 manifest.sig(JSON) 의 masterSeal 을 현재 Host+Proxy 산출물과 재대조(배포 후 무결성 재검증).
        /// 매니페스트 파싱 실패·해시 불일치 → false(fail-closed).</summary>
        public static bool VerifyReleaseSeal(string manifestPath, string hostPublishDir, string proxyPublishDir)
        {
            string expectedMasterSeal;
            try { expectedMasterSeal = ExtractMasterSeal(File.ReadAllText(manifestPath)); }
            catch { return false; }
            if (string.IsNullOrEmpty(expectedMasterSeal)) return false;

            List<ReleasePackager.ReleaseArtifact> artifacts;
            try { artifacts = NamedArtifacts(hostPublishDir, proxyPublishDir); }
            catch { return false; }

            try { return ReleasePackager.VerifyArtifacts(artifacts, expectedMasterSeal); }
            catch { return false; }
        }

        /// <summary>
        /// [봉인 운영] 외부 신뢰 앵커 결속 재검증 — 매니페스트 masterSeal 이 앵커(64 hex · 상수시간 1비트 비교)와 같고, 현재 산출물이
        /// 그 봉인과 일치할 때만 true. 앵커 없는 자기 일관성 검증(위 오버로드)은 바이너리+매니페스트 동시 재작성을 통과시키므로
        /// 배포 승인 판정에는 이 오버로드를 쓴다. 형식 오류·파싱 실패·불일치 → false(fail-closed).
        /// </summary>
        public static bool VerifyReleaseSeal(string manifestPath, string hostPublishDir, string proxyPublishDir, string expectedAnchorHex)
        {
            if (!BootIntegrityGate.IsStrictSealHex(expectedAnchorHex)) return false;
            string manifestSeal;
            try { manifestSeal = ExtractMasterSeal(File.ReadAllText(manifestPath)); }
            catch { return false; }
            byte[] a, m;
            try { a = Convert.FromHexString(expectedAnchorHex.Trim()); m = Convert.FromHexString(manifestSeal.Trim()); }
            catch (FormatException) { return false; }
            if (a.Length != m.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, m)) return false;
            return VerifyReleaseSeal(manifestPath, hostPublishDir, proxyPublishDir);
        }

        /// <summary>[Phase 4] 역할 한정 이름("host/…"·"proxy/…")으로 봉인 대상 구성 — 동명 바이너리의 폴더 간 치환도 봉인이 탐지.</summary>
        public static List<ReleasePackager.ReleaseArtifact> NamedArtifacts(string hostPublishDir, string proxyPublishDir)
        {
            var list = new List<ReleasePackager.ReleaseArtifact>();
            foreach (var p in ResolveHostArtifacts(hostPublishDir)) list.Add(new("host/" + Path.GetFileName(p), p));
            foreach (var p in ResolveKiwoomProxyArtifacts(proxyPublishDir)) list.Add(new("proxy/" + Path.GetFileName(p), p));
            return list;
        }

        private static List<string> CollectBinaries(string dir)
        {
            var list = new List<string>();
            list.AddRange(Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly));
            list.AddRange(Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // ReleasePackager.GenerateManifestJson 은 수기 StringBuilder 로 JSON 을 작성(외부 직렬화 의존 無·δ=0 대상
        // 아닌 콜드 도구이므로 허용) — 여기서는 그 스키마의 "masterSeal": "<hex>" 한 줄만 최소 파싱으로 역추출한다
        // (System.Text.Json 도입 없이 대칭적으로 간단 문자열 스캔 — 프로젝트 전역 BCL 우선 원칙과 정합).
        private static string ExtractMasterSeal(string manifestJson)
        {
            const string key = "\"masterSeal\": \"";
            int i = manifestJson.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            int start = i + key.Length;
            int end = manifestJson.IndexOf('"', start);
            if (end < 0) return "";
            return manifestJson.Substring(start, end - start);
        }
    }
}

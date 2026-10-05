// ============================================================================
// ReleasePackager — v6.2.0 Task 2: 프로덕션 릴리즈 패키징 & 배포 서명(SHA-256 Manifest)
//
//  최종 빌드 아티팩트(*.dll/*.exe)의 SHA-256 마스터 매니페스트를 생성하고, CausalAuditVault 의 최초 제네시스
//  헤드 해시(결정론 앵커)와 결합해 배포 무결성을 봉인한다:
//      masterSeal = SHA256( genesisHeadHash(32B) ‖ Σ 아티팩트별 SHA-256(정렬) )
//  → 매니페스트만으로 (a) 각 아티팩트 위·변조, (b) 아티팩트 집합의 누락/추가, (c) 우리 제네시스 계보를
//    동시에 검증할 수 있다(다른 빌드/조직의 매니페스트는 genesis 가 달라 seal 이 갈린다).
//
//  ★비-핫패스 콜드 도구(δ=0 불필요). 결정론(시각 미포함 — 동일 아티팩트 → 동일 seal, 검증 가능).
//    기존 불변 회로 무접촉(신규 파일). CausalAuditVault 는 '읽기'만(제네시스 캡처).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MS2E.Infrastructure
{
    /// <summary>SHA-256 릴리즈 매니페스트 생성/검증(제네시스 봉인). 콜드 패키징 도구.</summary>
    public static class ReleasePackager
    {
        // ★stale-ref 교정: 하드코딩 "6.2.0" → 어셈블리 버전 동적 추출(릴리스마다 자동 추종·no stale).
        public static readonly string DefaultVersion = typeof(ReleasePackager).Assembly.GetName().Version?.ToString(3) ?? "10.1.0";
        private const int HashLen = 32;

        /// <summary>신선한 CausalAuditVault 의 제네시스 헤드 해시(결정론 배포 앵커·32B).</summary>
        /// <remarks>[봉인 운영 실측] 수정 전엔 호출마다 CausalAuditVault 를 새로 생성(≈3.6MB 사전할당 링 → LOH)해 32B 만 읽고 버렸다 —
        /// 봉인 산출 1회 = 3.6MB 쓰레기(부팅 게이트·패키징·CLI 공통). 제네시스는 결정론 상수이므로 프로세스당 1회 산출·캐시하고 사본 반환.</remarks>
        public static byte[] GenesisHeadHash() => (byte[])s_genesis.Value.Clone();

        private static readonly Lazy<byte[]> s_genesis = new(static () =>
        {
            var vault = new CausalAuditVault();
            var g = new byte[HashLen];
            vault.CopyHeadHash(g);
            return g;
        }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>masterSeal = SHA256(genesis ‖ 정렬된 아티팩트 해시들). 배포 무결성 서명(hex).</summary>
        public static string ComputeMasterSealHex(IReadOnlyList<string> artifactPaths)
        {
            var entries = HashArtifacts(artifactPaths);
            byte[] genesis = GenesisHeadHash();
            byte[] input = new byte[HashLen + entries.Count * HashLen];
            genesis.CopyTo(input, 0);
            for (int i = 0; i < entries.Count; i++) entries[i].Hash.CopyTo(input, HashLen + i * HashLen);
            return Convert.ToHexString(SHA256.HashData(input));
        }

        /// <summary>배포 매니페스트 JSON 생성(결정론).</summary>
        public static string GenerateManifestJson(string version, IReadOnlyList<string> artifactPaths)
        {
            var entries = HashArtifacts(artifactPaths);
            byte[] genesis = GenesisHeadHash();

            byte[] sealInput = new byte[HashLen + entries.Count * HashLen];
            genesis.CopyTo(sealInput, 0);
            for (int i = 0; i < entries.Count; i++) entries[i].Hash.CopyTo(sealInput, HashLen + i * HashLen);
            string masterSeal = Convert.ToHexString(SHA256.HashData(sealInput));

            var sb = new StringBuilder(512);
            sb.Append("{\n");
            sb.Append("  \"schema\": \"ms2e-release-manifest/1\",\n");
            sb.Append("  \"version\": \"").Append(Esc(version)).Append("\",\n");
            sb.Append("  \"assembly\": \"MS2E.StockSuite\",\n");
            sb.Append("  \"artifactCount\": ").Append(entries.Count).Append(",\n");
            sb.Append("  \"genesisHeadHash\": \"").Append(Convert.ToHexString(genesis)).Append("\",\n");
            sb.Append("  \"artifacts\": [\n");
            for (int i = 0; i < entries.Count; i++)
            {
                sb.Append("    { \"name\": \"").Append(Esc(entries[i].Name))
                  .Append("\", \"sizeBytes\": ").Append(entries[i].Size)
                  .Append(", \"sha256\": \"").Append(Convert.ToHexString(entries[i].Hash)).Append("\" }")
                  .Append(i < entries.Count - 1 ? "," : "").Append('\n');
            }
            sb.Append("  ],\n");
            sb.Append("  \"masterSeal\": \"").Append(masterSeal).Append("\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>매니페스트를 생성해 파일로 기록하고 JSON 을 반환.</summary>
        public static string WriteManifest(string version, IReadOnlyList<string> artifactPaths, string outPath)
        {
            string json = GenerateManifestJson(version, artifactPaths);
            File.WriteAllText(outPath, json);
            return json;
        }

        /// <summary>아티팩트 집합이 주어진 masterSeal(hex)과 일치하는지 검증(위·변조/누락 감지).</summary>
        public static bool VerifyArtifacts(IReadOnlyList<string> artifactPaths, string expectedMasterSealHex)
            => string.Equals(ComputeMasterSealHex(artifactPaths), expectedMasterSealHex?.Trim(),
                             StringComparison.OrdinalIgnoreCase);

        // ── [Phase 4] v2 명명 봉인(역할 한정 이름 결속) ─────────────────────────────────────────────────────
        //   v1(경로 오버로드)은 masterSeal 입력이 genesis ‖ 해시들뿐이라 "이름"이 봉인에 들어가지 않고, 정렬 키도 파일명뿐이다.
        //   Host 와 KiwoomProxy 가 같은 파일명을 가지면 (a) List.Sort 가 불안정 정렬이라 masterSeal 이 비결정적이 되고
        //   (b) 두 폴더의 동명 바이너리를 맞바꿔도(x86↔x64 치환) 해시 다중집합이 같아 봉인이 그대로 통과했다(재현 테스트).
        //   v2 는 "host/…"·"proxy/…" 역할 한정 이름을 쓰고, 항목마다 SHA256(name) ‖ SHA256(bytes) 를 봉인 입력에 넣는다.
        //   이름 중복은 봉인 자체를 거부(fail-closed). v1 은 하위 호환 유지 — 단 동명 충돌은 동일하게 거부.

        /// <summary>봉인 대상 1건: 매니페스트 표기 이름(역할 한정 권장, 예 "host/MS2E.StockSuite.dll") + 실제 파일 경로.</summary>
        public readonly record struct ReleaseArtifact(string Name, string Path);

        /// <summary>v2 masterSeal = SHA256(genesis ‖ Σ(SHA256(utf8 name) ‖ SHA256(bytes)))(이름 서수 정렬·이름 결속).</summary>
        public static string ComputeMasterSealHex(IReadOnlyList<ReleaseArtifact> artifacts)
            => Convert.ToHexString(SealV2(HashNamed(artifacts)));

        /// <summary>v2 매니페스트 JSON(schema ms2e-release-manifest/2).</summary>
        public static string GenerateManifestJson(string version, IReadOnlyList<ReleaseArtifact> artifacts)
        {
            var entries = HashNamed(artifacts);
            string masterSeal = Convert.ToHexString(SealV2(entries));
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            sb.Append("  \"schema\": \"ms2e-release-manifest/2\",\n");
            sb.Append("  \"version\": \"").Append(Esc(version)).Append("\",\n");
            sb.Append("  \"assembly\": \"MS2E.StockSuite\",\n");
            sb.Append("  \"artifactCount\": ").Append(entries.Count).Append(",\n");
            sb.Append("  \"genesisHeadHash\": \"").Append(Convert.ToHexString(GenesisHeadHash())).Append("\",\n");
            sb.Append("  \"artifacts\": [\n");
            for (int i = 0; i < entries.Count; i++)
            {
                sb.Append("    { \"name\": \"").Append(Esc(entries[i].Name))
                  .Append("\", \"sizeBytes\": ").Append(entries[i].Size)
                  .Append(", \"sha256\": \"").Append(Convert.ToHexString(entries[i].Hash)).Append("\" }")
                  .Append(i < entries.Count - 1 ? "," : "").Append('\n');
            }
            sb.Append("  ],\n");
            sb.Append("  \"masterSeal\": \"").Append(masterSeal).Append("\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public static string WriteManifest(string version, IReadOnlyList<ReleaseArtifact> artifacts, string outPath)
        {
            string json = GenerateManifestJson(version, artifacts);
            File.WriteAllText(outPath, json);
            return json;
        }

        public static bool VerifyArtifacts(IReadOnlyList<ReleaseArtifact> artifacts, string expectedMasterSealHex)
        {
            byte[] expected;
            try { expected = Convert.FromHexString(expectedMasterSealHex?.Trim() ?? ""); }
            catch (FormatException) { return false; }
            byte[] actual = SealV2(HashNamed(artifacts));
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private static byte[] SealV2(List<ArtifactEntry> entries)
        {
            byte[] input = new byte[HashLen + entries.Count * 2 * HashLen];
            GenesisHeadHash().CopyTo(input, 0);
            for (int i = 0; i < entries.Count; i++)
            {
                int at = HashLen + i * 2 * HashLen;
                SHA256.HashData(Encoding.UTF8.GetBytes(entries[i].Name), input.AsSpan(at, HashLen));
                entries[i].Hash.CopyTo(input, at + HashLen);
            }
            return SHA256.HashData(input);
        }

        private static List<ArtifactEntry> HashNamed(IReadOnlyList<ReleaseArtifact> artifacts)
        {
            if (artifacts is null) throw new ArgumentNullException(nameof(artifacts));
            var list = new List<ArtifactEntry>(artifacts.Count);
            foreach (var a in artifacts)
            {
                if (string.IsNullOrWhiteSpace(a.Name)) throw new ArgumentException("아티팩트 이름 누락", nameof(artifacts));
                // [봉인 운영 · 방향 A] ReadAllBytes(파일 크기만큼 LOH 할당) → POH 청크 스트리밍(할당 = 파일당 핸들 1개)
                byte[] hash = PinnedSha256Streamer.HashFile(a.Path, out long size);
                list.Add(new ArtifactEntry(a.Name.Replace('\\', '/'), size, hash));
            }
            SortRejectingDuplicates(list);
            return list;
        }

        private static void SortRejectingDuplicates(List<ArtifactEntry> list)
        {
            list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            for (int i = 1; i < list.Count; i++)
                if (string.Equals(list[i - 1].Name, list[i].Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"동명 아티팩트 '{list[i].Name}' — 봉인 순서가 비결정적이고 치환을 탐지할 수 없어 봉인 거부(fail-closed).");
        }

        // 아티팩트별 (이름·크기·SHA-256), 이름 정렬(결정론).
        private static List<ArtifactEntry> HashArtifacts(IReadOnlyList<string> artifactPaths)
        {
            if (artifactPaths is null) throw new ArgumentNullException(nameof(artifactPaths));
            var list = new List<ArtifactEntry>(artifactPaths.Count);
            foreach (string path in artifactPaths)
            {
                byte[] hash = PinnedSha256Streamer.HashFile(path, out long size);   // [봉인 운영] LOH 적재 제거
                list.Add(new ArtifactEntry(Path.GetFileName(path), size, hash));
            }
            SortRejectingDuplicates(list);   // [Phase 4] 동명 충돌 → 불안정 정렬 비결정 봉인 대신 거부
            return list;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private readonly struct ArtifactEntry
        {
            public readonly string Name; public readonly long Size; public readonly byte[] Hash;
            public ArtifactEntry(string name, long size, byte[] hash) { Name = name; Size = size; Hash = hash; }
        }
    }
}

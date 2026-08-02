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

namespace StockManager.Common
{
    /// <summary>SHA-256 릴리즈 매니페스트 생성/검증(제네시스 봉인). 콜드 패키징 도구.</summary>
    public static class ReleasePackager
    {
        // ★stale-ref 교정: 하드코딩 "6.2.0" → 어셈블리 버전 동적 추출(릴리스마다 자동 추종·no stale).
        public static readonly string DefaultVersion = typeof(ReleasePackager).Assembly.GetName().Version?.ToString(3) ?? "10.1.0";
        private const int HashLen = 32;

        /// <summary>신선한 CausalAuditVault 의 제네시스 헤드 해시(결정론 배포 앵커·32B).</summary>
        public static byte[] GenesisHeadHash()
        {
            var vault = new CausalAuditVault();
            var g = new byte[HashLen];
            vault.CopyHeadHash(g);
            return g;
        }

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

        // 아티팩트별 (이름·크기·SHA-256), 이름 정렬(결정론).
        private static List<ArtifactEntry> HashArtifacts(IReadOnlyList<string> artifactPaths)
        {
            if (artifactPaths is null) throw new ArgumentNullException(nameof(artifactPaths));
            var list = new List<ArtifactEntry>(artifactPaths.Count);
            foreach (string path in artifactPaths)
            {
                byte[] bytes = File.ReadAllBytes(path);
                list.Add(new ArtifactEntry(Path.GetFileName(path), bytes.LongLength, SHA256.HashData(bytes)));
            }
            list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
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

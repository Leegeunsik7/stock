// ============================================================================
// ReleaseHandshake — [릴리스 CI · 추천 A] 출하 zip 무추출 수검(방향 A) + 다중 저장소 앤티-탬퍼 핸드셰이크(방향 B)
//
//  ① ZipSealDryRunVerifier(방향 A) — release-v*.zip 을 디스크에 풀지 않고 항목 스트림을 직접 SHA-256 으로 흘려
//     (a) zip 내장 manifest.sig 의 masterSeal = 외부 앵커(master-seal.txt), (b) 항목별 해시 = 매니페스트 기재값,
//     (c) 실측 해시로 재계산한 v2 봉인 = 앵커, (d) 매니페스트 누락/미등재 바이너리/경로 이탈 0 — 전부 성립해야 Ok.
//     ★δ=0 범위(정직): 항목 해시 루프(HashStreamInto · 풀 버퍼 1개 + IncrementalHash 재사용)와 봉인 재계산(ComputeSealInto)은
//       힙 0B 실측. zip 디렉터리 파싱(ZipArchive 항목 객체·Deflate 스트림)과 매니페스트 JSON 파싱은 BCL 내부 할당이 있다(콜드 1회).
//  ② MultiRepoReleaseManifest(방향 B) — StockManager 출하 사실(태그·소스 커밋·manifest.sig 해시·masterSeal·38 아티팩트·zip·
//     COM 쉼 산출물·liveArmed 기본값·인터락 목록)과 피어 저장소(QuantumVault) 관측값(HEAD·봉인 매니페스트 해시·rootHash)을
//     RELEASE_MANIFEST.json 하나로 묶고, 정규화 문자열의 SHA-256(manifestHash)으로 자기 봉인한다.
//     Verify 는 기록값 ↔ 실측값을 대조해 하나라도 어긋나면 거부(어느 항목이 왜 어긋났는지 판정값으로 반환).
//     ★[다중 저장소 봉인 감사 · 실결함 교정] manifestHash 는 무키 SHA-256 — 본문을 고친 뒤 재계산하면 자기 봉인은 통과한다.
//       따라서 모든 기록 필드가 '독립 실측'에 묶여 있어야 한다. 수정 전 genesisHeadHash(manifest.sig 와 미대조) · interlocks(무대조) ·
//       peer.manifestPath/name(관측 경로와 미대조)은 위조 후 재봉인하면 그대로 통과했다 → 각각 manifest.sig 제네시스 · 태그 소스의
//       ArmingInterlockSource 열거(git show <태그>:ArmingInterlockBitmask.cs) · 관측한 피어 경로/이름과 대조(GenesisMismatch ·
//       InterlockSetMismatch · PeerIdentityMismatch). 관측값 부재 = 거부(fail-closed).
//     ★피어 저장소는 읽기 전용 — 쓰기 0(QuantumVault 는 별도 제품 · v2.7.0 봉인 동결 · 사용자 결정 2026-10-06).
//  ③ GitRepoProbe — git 을 셸 없이(ArgumentList) 호출해 HEAD·태그 커밋·작업 트리 변경·태그↔HEAD 코드 이탈을 관측.
//     문서(*.md)·docs/·release/·.claude/·graphify-out/ 변경은 코드 이탈로 보지 않는다(출하 바이너리에 영향 없음).
//  ④ ReleaseHandshakeCli — 호스트 실행 파일의 콜드 CLI(--verify-zip · --sync-release-manifest · --release-handshake).
//     release-package.ps1 가 [9/10]·[10/10] 에서 호출 — 실패 = zip·해시·앵커·매니페스트 삭제 후 중단(fail-closed).
// ============================================================================
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Tools
{
    // ════════════════════════════════════════════════════════════════════════
    // ① 방향 A — 출하 zip 무추출 수검
    // ════════════════════════════════════════════════════════════════════════

    public enum ZipSealVerdict : byte
    {
        Ok = 0, AnchorMalformed = 1, ZipUnreadable = 2, ManifestMissing = 3, ManifestInvalid = 4, AnchorMismatch = 5,
        PathEscape = 6, UnlistedBinary = 7, DuplicateEntry = 8, ArtifactTampered = 9, ArtifactMissing = 10, SealMismatch = 11,
    }

    public static class ZipSealDryRunVerifier
    {
        public const int HashLen = 32;
        private const int ChunkBytes = 81_920;

        /// <summary>zip 무추출 수검. expectedSealHex = 외부 앵커(master-seal.txt 내용 · 64 hex).</summary>
        public static ZipSealVerdict Verify(string zipPath, string expectedSealHex, out string detail)
        {
            detail = "";
            string anchor = (expectedSealHex ?? "").Trim();
            if (!BootIntegrityGate.IsStrictSealHex(anchor)) { detail = "앵커 형식 오류(64 hex 아님)"; return ZipSealVerdict.AnchorMalformed; }
            byte[] anchorBytes = Convert.FromHexString(anchor);
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(zipPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { detail = "zip 열기 실패: " + ex.GetType().Name; return ZipSealVerdict.ZipUnreadable; }
            using (zip)
            {
                var mEntry = zip.GetEntry("manifest.sig");
                if (mEntry is null) { detail = "zip 에 manifest.sig 없음 — 비봉인 패키지"; return ZipSealVerdict.ManifestMissing; }
                string mText;
                try { using var sr = new StreamReader(mEntry.Open(), Encoding.UTF8); mText = sr.ReadToEnd(); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { detail = "manifest.sig 읽기 실패: " + ex.GetType().Name; return ZipSealVerdict.ZipUnreadable; }
                if (!BootIntegrityGate.TryParse(mText, out var entries, out string sealField, out string why)) { detail = "manifest.sig 무효: " + why; return ZipSealVerdict.ManifestInvalid; }
                if (!HexEquals(sealField, anchorBytes)) { detail = "manifest.sig masterSeal ≠ 외부 앵커"; return ZipSealVerdict.AnchorMismatch; }

                int n = entries.Count;
                var names = new string[n];
                var expected = new byte[n * HashLen];
                entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                for (int i = 0; i < n; i++) { names[i] = entries[i].Name; entries[i].Sha.CopyTo(expected, i * HashLen); }
                var actual = new byte[n * HashLen];
                var seen = new bool[n];
                // [v12.17.0-GM · 방향 B] 항목 정규화 중복 가드 — 모든 파일 항목(manifest.sig 포함)을 '\\'→'/' + 대소문자 무시로 대조.
                //   기존 검사는 매니페스트 '기재' 항목의 서수 중복만 잡았다: ① manifest.sig 2개(어느 쪽이 쓰일지 모호) ② 기재 비바이너리
                //   (runtimeconfig/deps.json 등)의 대소문자 변형 사본 — Windows(대소문자 무시 파일시스템) 압축 해제 시 봉인 항목을 덮어쓴다.
                var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                try
                {
                    using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (var e in zip.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        if (name.Length == 0 || name[^1] == '/') continue;
                        if (!allNames.Add(name)) { detail = "중복 항목(구분자 · 대소문자 정규화 후): " + name; return ZipSealVerdict.DuplicateEntry; }
                        if (name == "manifest.sig") continue;
                        if (name[0] == '/' || name.Contains("..", StringComparison.Ordinal) || name.IndexOf(':') >= 0)
                        { detail = "경로 이탈 항목: " + name; return ZipSealVerdict.PathEscape; }
                        int idx = Array.BinarySearch(names, name, StringComparer.Ordinal);
                        if (idx < 0)
                        {
                            if (IsBinary(name)) { detail = "매니페스트 미등재 바이너리: " + name; return ZipSealVerdict.UnlistedBinary; }
                            continue;   // 설정·문서 등 비바이너리는 봉인 대상 아님(호스트 BootIntegrityGate 와 동일 범위)
                        }
                        if (seen[idx]) { detail = "중복 항목: " + name; return ZipSealVerdict.DuplicateEntry; }
                        seen[idx] = true;
                        try { using var s = e.Open(); HashStreamInto(s, ih, buffer, actual.AsSpan(idx * HashLen, HashLen)); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException) { detail = "항목 읽기 실패(손상): " + name; return ZipSealVerdict.ArtifactTampered; }
                        if (!CryptographicOperations.FixedTimeEquals(actual.AsSpan(idx * HashLen, HashLen), expected.AsSpan(idx * HashLen, HashLen)))
                        { detail = "해시 불일치: " + name; return ZipSealVerdict.ArtifactTampered; }
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                for (int i = 0; i < n; i++)
                    if (!seen[i]) { detail = "매니페스트 기재 아티팩트 누락: " + names[i]; return ZipSealVerdict.ArtifactMissing; }

                // 실측 해시로 v2 봉인 재계산 — manifest 의 masterSeal 필드만 맞추고 항목 해시를 바꾼 위조까지 차단
                var nameHashes = new byte[n * HashLen];
                for (int i = 0; i < n; i++) SHA256.HashData(Encoding.UTF8.GetBytes(names[i]), nameHashes.AsSpan(i * HashLen, HashLen));
                var scratch = new byte[HashLen + n * 2 * HashLen];
                Span<byte> seal = stackalloc byte[HashLen];
                ComputeSealInto(ReleasePackager.GenesisHeadHash(), nameHashes, actual, scratch, seal);
                if (!CryptographicOperations.FixedTimeEquals(seal, anchorBytes)) { detail = "실측 재계산 봉인 ≠ 앵커"; return ZipSealVerdict.SealMismatch; }
                detail = $"artifacts={n} seal={anchor[..16]}… 일치(무추출)";
                return ZipSealVerdict.Ok;
            }
        }

        /// <summary>스트림 → SHA-256(δ=0 · 호출측 버퍼/해셔 재사용). dst = 32B.</summary>
        public static void HashStreamInto(Stream s, IncrementalHash ih, byte[] buffer, Span<byte> dst)
        {
            int r;
            while ((r = s.Read(buffer, 0, buffer.Length)) > 0) ih.AppendData(buffer, 0, r);
            if (!ih.TryGetHashAndReset(dst, out int w) || w != HashLen) throw new CryptographicException("SHA-256 출력 길이 이상");
        }

        /// <summary>v2 봉인 = SHA256(genesis ‖ Σ(nameHash ‖ contentHash))(이름 서수 정렬 순 · δ=0). scratch ≥ 32 + n·64.</summary>
        public static void ComputeSealInto(ReadOnlySpan<byte> genesis, ReadOnlySpan<byte> nameHashes, ReadOnlySpan<byte> contentHashes, Span<byte> scratch, Span<byte> dst)
        {
            int n = contentHashes.Length / HashLen;
            if (genesis.Length != HashLen || nameHashes.Length != contentHashes.Length || contentHashes.Length % HashLen != 0) throw new ArgumentException("해시 길이 불일치");
            int len = HashLen + n * 2 * HashLen;
            if (scratch.Length < len) throw new ArgumentException("scratch 부족", nameof(scratch));
            genesis.CopyTo(scratch);
            for (int i = 0; i < n; i++)
            {
                int at = HashLen + i * 2 * HashLen;
                nameHashes.Slice(i * HashLen, HashLen).CopyTo(scratch.Slice(at, HashLen));
                contentHashes.Slice(i * HashLen, HashLen).CopyTo(scratch.Slice(at + HashLen, HashLen));
            }
            SHA256.HashData(scratch[..len], dst);
        }

        /// <summary>hex 문자열 ↔ 바이트 상수 시간 대조(대소문자 무관 · δ=0 · 형식 오류 = false).</summary>
        public static bool HexEquals(ReadOnlySpan<char> hex, ReadOnlySpan<byte> bytes)
        {
            hex = hex.Trim();
            if (hex.Length != bytes.Length * 2 || bytes.Length > 64) return false;
            Span<byte> tmp = stackalloc byte[64];
            var t = tmp[..bytes.Length];
            return TryDecodeHex(hex, t) && CryptographicOperations.FixedTimeEquals(t, bytes);
        }

        /// <summary>hex → 바이트(δ=0 · 길이 = dst·2 정확히 · 형식 오류 = false).</summary>
        public static bool TryDecodeHex(ReadOnlySpan<char> hex, Span<byte> dst)
        {
            if (hex.Length != dst.Length * 2) return false;
            for (int i = 0; i < dst.Length; i++)
            {
                int hi = Nibble(hex[2 * i]), lo = Nibble(hex[2 * i + 1]);
                if ((hi | lo) < 0) return false;
                dst[i] = (byte)((hi << 4) | lo);
            }
            return true;
        }

        private static int Nibble(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        private static bool IsBinary(string name)
            => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    // ════════════════════════════════════════════════════════════════════════
    // ② 방향 B — 다중 저장소 릴리스 매니페스트 + 핸드셰이크
    // ════════════════════════════════════════════════════════════════════════

    public sealed record PeerRecord(string Name, string Commit, string ManifestPath, string ManifestSha256, string RootHash);

    public sealed record ReleaseManifestInput(
        string Version, string Tag, string SourceCommit,
        string ManifestSigSha256, string MasterSeal, string GenesisHeadHash,
        IReadOnlyList<(string Name, string Sha256)> Artifacts,
        string ZipName, string ZipSha256, string ShimSha256,
        bool LiveArmedDefault, IReadOnlyList<string> Interlocks, PeerRecord? Peer,
        IReadOnlyList<string>? TightenRules = null);   // [v12.18.1-GM] 강화 전용 행렬 "키|규칙|폴백|하한|상한"(태그 소스 판독 · 없으면 기록 생략 = 구 매니페스트 호환)

    public sealed record RepoObservation(string? Head, string? TagCommit, IReadOnlyList<string> DirtyPaths, IReadOnlyList<string> DriftPaths);

    public sealed record PeerObservation(string? Head, IReadOnlyList<string> DirtyPaths, string? ManifestSha256, string? RootHash,
                                         string? ManifestPath = null, string? Name = null);

    /// <summary>
    /// [다중 저장소 · 방향 B] 패키징 시작([0/10] 피어 사전 점검) 시점에 고정한 피어 상태(HEAD 커밋 · 봉인 매니페스트 SHA-256).
    /// 동기화 · 핸드셰이크는 기록된 피어가 이 고정값과 같아야 통과 — 빌드(수십 분) 도중 피어가 새 커밋/새 봉인으로 움직이면
    /// '출하를 시작할 때 승인한 피어'가 아닌 다른 피어와 조용히 짝지어지는 것을 막는다(PeerDrift → 산출물 전량 폐기).
    /// </summary>
    public sealed record PeerPin(string Head, string ManifestSha256);

    public sealed record HandshakeObservation(
        RepoObservation Repo, string? ManifestSigSha256, string? ManifestSigVersion, string? ManifestSigMasterSeal,
        IReadOnlyList<(string Name, string Sha256)> ManifestSigArtifacts, string? ZipSha256, string? AnchorSeal, string? ShimSha256,
        PeerObservation? Peer, string? ManifestSigGenesis = null, IReadOnlyList<string>? TagInterlocks = null, PeerPin? Pin = null,
        IReadOnlyList<string>? TagTightenRules = null);

    public enum ReleaseHandshakeVerdict : byte
    {
        Ok = 0, Malformed = 1, SelfHashMismatch = 2, LiveArmedViolation = 3, TagMissing = 4, SourceCommitMismatch = 5,
        HeadDrift = 6, DirtyTree = 7, ManifestSigMismatch = 8, MasterSealMismatch = 9, ArtifactSetMismatch = 10,
        ZipMismatch = 11, ShimMismatch = 12, PeerMissing = 13, PeerDirty = 14, PeerCommitMismatch = 15, PeerManifestMismatch = 16,
        GenesisMismatch = 17, InterlockSetMismatch = 18, PeerIdentityMismatch = 19,   // [다중 저장소 봉인 감사] 무대조 필드 교정
        ParserDifferential = 20,   // 독립 무할당 판독기(ManifestSealStreamer)가 거부 — 중복 키 · 이스케이프 등 판독기마다 달리 보이는 매니페스트
        PeerDrift = 21,            // [방향 B] 기록 피어 ≠ 패키징 시작 시 고정한 피어(빌드 도중 피어 HEAD/봉인 이동)
        TightenRuleMismatch = 22,  // [v12.18.1-GM] 강화 전용 행렬 기록 ≠ 태그 소스(TightenOnlyUiSettingWriter 표) — 누락(제거) · 방향/상수 변조
    }

    public static class MultiRepoReleaseManifest
    {
        public const string Schema = "ms2e-multirepo-release/1";
        public const string FileName = "RELEASE_MANIFEST.json";

        /// <summary>정규화 문자열 SHA-256(자기 봉인) — JSON 공백·필드 순서와 무관, 해시는 대문자·커밋은 소문자로 정규화.</summary>
        public static string CanonicalHash(ReleaseManifestInput m)
        {
            var sb = new StringBuilder(4096);
            void L(string k, string v) => sb.Append(k).Append('=').Append(v).Append('\n');
            L("schema", Schema); L("version", m.Version); L("tag", m.Tag); L("sourceCommit", m.SourceCommit.ToLowerInvariant());
            L("manifestSigSha256", m.ManifestSigSha256.ToUpperInvariant()); L("masterSeal", m.MasterSeal.ToUpperInvariant());
            L("genesisHeadHash", m.GenesisHeadHash.ToUpperInvariant()); L("zipName", m.ZipName); L("zipSha256", m.ZipSha256.ToUpperInvariant());
            L("shimSha256", m.ShimSha256.ToUpperInvariant()); L("liveArmedDefault", m.LiveArmedDefault ? "true" : "false");
            foreach (var i in m.Interlocks) L("interlock", i);
            if (m.TightenRules is { } tr) foreach (var t in tr) L("tighten", t);
            var arts = new List<(string Name, string Sha256)>(m.Artifacts);
            arts.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var a in arts) L("artifact", a.Name + "|" + a.Sha256.ToUpperInvariant());
            if (m.Peer is { } p)
            {
                L("peer.name", p.Name); L("peer.commit", p.Commit.ToLowerInvariant()); L("peer.manifestPath", p.ManifestPath);
                L("peer.manifestSha256", p.ManifestSha256.ToUpperInvariant()); L("peer.rootHash", p.RootHash);
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        public static string Build(ReleaseManifestInput m)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("schema", Schema);
                w.WriteString("version", m.Version);
                w.WriteString("tag", m.Tag);
                w.WriteString("sourceCommit", m.SourceCommit.ToLowerInvariant());
                w.WriteString("manifestSigSha256", m.ManifestSigSha256.ToUpperInvariant());
                w.WriteString("masterSeal", m.MasterSeal.ToUpperInvariant());
                w.WriteString("genesisHeadHash", m.GenesisHeadHash.ToUpperInvariant());
                w.WriteString("zipName", m.ZipName);
                w.WriteString("zipSha256", m.ZipSha256.ToUpperInvariant());
                w.WriteString("shimSha256", m.ShimSha256.ToUpperInvariant());
                w.WriteBoolean("liveArmedDefault", m.LiveArmedDefault);
                w.WriteStartArray("interlocks");
                foreach (var i in m.Interlocks) w.WriteStringValue(i);
                w.WriteEndArray();
                if (m.TightenRules is { Count: > 0 } tr)
                {
                    w.WriteStartArray("tightenRules");
                    foreach (var t in tr) w.WriteStringValue(t);
                    w.WriteEndArray();
                }
                w.WriteNumber("artifactCount", m.Artifacts.Count);
                w.WriteStartArray("artifacts");
                var arts = new List<(string Name, string Sha256)>(m.Artifacts);
                arts.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var a in arts) { w.WriteStartObject(); w.WriteString("name", a.Name); w.WriteString("sha256", a.Sha256.ToUpperInvariant()); w.WriteEndObject(); }
                w.WriteEndArray();
                if (m.Peer is { } p)
                {
                    w.WriteStartObject("peer");
                    w.WriteString("name", p.Name); w.WriteString("commit", p.Commit.ToLowerInvariant()); w.WriteString("manifestPath", p.ManifestPath);
                    w.WriteString("manifestSha256", p.ManifestSha256.ToUpperInvariant()); w.WriteString("rootHash", p.RootHash);
                    w.WriteEndObject();
                }
                w.WriteString("manifestHash", CanonicalHash(m));
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static bool TryParse(string json, out ReleaseManifestInput manifest, out string storedHash, out string why)
        {
            manifest = null!; storedHash = ""; why = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new FormatException(k);
                if (S(r, "schema") != Schema) { why = "스키마 불일치"; return false; }
                var interlocks = new List<string>();
                foreach (var i in r.GetProperty("interlocks").EnumerateArray()) interlocks.Add(i.GetString() ?? throw new FormatException("interlock"));
                var arts = new List<(string, string)>();
                foreach (var a in r.GetProperty("artifacts").EnumerateArray()) arts.Add((S(a, "name"), S(a, "sha256")));
                if (r.GetProperty("artifactCount").GetInt32() != arts.Count) { why = "artifactCount 불일치"; return false; }
                List<string>? tighten = null;
                if (r.TryGetProperty("tightenRules", out var tre))
                {
                    tighten = new List<string>();
                    foreach (var t in tre.EnumerateArray()) tighten.Add(t.GetString() ?? throw new FormatException("tightenRule"));
                    if (tighten.Count == 0) { why = "tightenRules 빈 배열(비정규 — 없으면 키 자체를 생략)"; return false; }
                }
                PeerRecord? peer = null;
                if (r.TryGetProperty("peer", out var pe) && pe.ValueKind == JsonValueKind.Object)
                    peer = new PeerRecord(S(pe, "name"), S(pe, "commit"), S(pe, "manifestPath"), S(pe, "manifestSha256"), S(pe, "rootHash"));
                manifest = new ReleaseManifestInput(S(r, "version"), S(r, "tag"), S(r, "sourceCommit"), S(r, "manifestSigSha256"), S(r, "masterSeal"),
                    S(r, "genesisHeadHash"), arts, S(r, "zipName"), S(r, "zipSha256"), S(r, "shimSha256"),
                    r.GetProperty("liveArmedDefault").GetBoolean(), interlocks, peer, tighten);
                storedHash = S(r, "manifestHash");
                return true;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                why = "파싱 실패: " + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        /// <summary>핸드셰이크 — 기록(json) ↔ 실측(obs) 대조. 첫 불일치에서 거부(원자적: 부분 통과 없음).</summary>
        public static ReleaseHandshakeVerdict Verify(string json, HandshakeObservation obs, out string detail)
        {
            if (!TryParse(json, out var m, out string stored, out string why)) { detail = why; return ReleaseHandshakeVerdict.Malformed; }
            if (!HexEq(stored, CanonicalHash(m))) { detail = "manifestHash ≠ 정규화 재계산(매니페스트 변조)"; return ReleaseHandshakeVerdict.SelfHashMismatch; }
            if (m.LiveArmedDefault) { detail = "liveArmedDefault=true — Fail-Closed 불변식 위반"; return ReleaseHandshakeVerdict.LiveArmedViolation; }
            {
                // [방향 A] 두 번째 독립 판독(고정 버퍼 · 무할당) — 기준 판독(JsonDocument)과 같은 자기 봉인을 재계산하지 못하면 거부
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                var st = new ManifestSealStreamer(Math.Max(1024, bytes.Length));
                var sv = st.Load(bytes) ? st.Verify(default) : ManifestStreamVerdict.TooLarge;
                if (sv != ManifestStreamVerdict.Ok) { detail = "독립 판독기 거부: " + sv + "(판독기 차이 — 중복 키/이스케이프/비정규 형식)"; return ReleaseHandshakeVerdict.ParserDifferential; }
            }
            var repo = obs.Repo;
            if (repo.TagCommit is null) { detail = $"태그 {m.Tag} 없음"; return ReleaseHandshakeVerdict.TagMissing; }
            if (!string.Equals(repo.TagCommit, m.SourceCommit, StringComparison.OrdinalIgnoreCase))
            { detail = $"태그 {m.Tag} → {Short(repo.TagCommit)} ≠ 기록 소스 커밋 {Short(m.SourceCommit)}(태그 이동/재작성)"; return ReleaseHandshakeVerdict.SourceCommitMismatch; }
            if (repo.DriftPaths.Count > 0) { detail = $"태그↔HEAD 코드 이탈 {repo.DriftPaths.Count}건(예: {repo.DriftPaths[0]})"; return ReleaseHandshakeVerdict.HeadDrift; }
            if (repo.DirtyPaths.Count > 0) { detail = $"작업 트리 미커밋 코드 {repo.DirtyPaths.Count}건(예: {repo.DirtyPaths[0]}) — 출하 바이너리 ≠ 태그 소스"; return ReleaseHandshakeVerdict.DirtyTree; }
            if (obs.ManifestSigSha256 is null || !HexEq(obs.ManifestSigSha256, m.ManifestSigSha256) || obs.ManifestSigVersion != m.Version || m.Tag != "v" + m.Version)
            { detail = "manifest.sig 파일 해시/버전/태그 명명 불일치"; return ReleaseHandshakeVerdict.ManifestSigMismatch; }
            if (obs.ManifestSigMasterSeal is null || obs.AnchorSeal is null || !HexEq(obs.ManifestSigMasterSeal, m.MasterSeal) || !HexEq(obs.AnchorSeal, m.MasterSeal))
            { detail = "masterSeal(manifest.sig · master-seal.txt · 기록) 3자 불일치"; return ReleaseHandshakeVerdict.MasterSealMismatch; }
            if (!SameArtifacts(m.Artifacts, obs.ManifestSigArtifacts, out string diff)) { detail = "아티팩트 집합 불일치: " + diff; return ReleaseHandshakeVerdict.ArtifactSetMismatch; }
            if (obs.ZipSha256 is null || !HexEq(obs.ZipSha256, m.ZipSha256)) { detail = "zip SHA-256 불일치/없음"; return ReleaseHandshakeVerdict.ZipMismatch; }
            if (obs.ShimSha256 is null || !HexEq(obs.ShimSha256, m.ShimSha256)) { detail = "COM 쉼 산출물 해시 불일치/없음(쉼 게이트 미통과 빌드)"; return ReleaseHandshakeVerdict.ShimMismatch; }
            if (obs.ManifestSigGenesis is null || !HexEq(obs.ManifestSigGenesis, m.GenesisHeadHash))
            { detail = "genesisHeadHash(기록 ≠ manifest.sig) — 제네시스 계보 위조/불일치"; return ReleaseHandshakeVerdict.GenesisMismatch; }
            if (!SameSequence(m.Interlocks, obs.TagInterlocks, out string idiff))
            { detail = "인터락 목록 ≠ 태그 소스 ArmingInterlockSource: " + idiff; return ReleaseHandshakeVerdict.InterlockSetMismatch; }
            // [v12.18.1-GM] 강화 전용 행렬 — 태그 소스에 표가 있으면 기록도 같아야 한다(기록에서 지운 '조용한 누락' = 거부).
            //  기록에 있는데 태그 소스를 관측하지 못함 = 거부(fail-closed). 둘 다 없음(구 태그 · 구 매니페스트) = 대상 아님.
            if (m.TightenRules is not null || obs.TagTightenRules is { Count: > 0 })
            {
                if (!SameSequence(m.TightenRules ?? Array.Empty<string>(), obs.TagTightenRules, out string tdiff))
                { detail = "강화 전용 행렬 ≠ 태그 소스 TightenOnlyUiSettingWriter: " + tdiff; return ReleaseHandshakeVerdict.TightenRuleMismatch; }
            }
            // ★[다중 저장소 · 실결함 교정] 피어 대조를 요청(관측 피어 있음)했는데 기록에 피어가 없으면 거부 — 수정 전엔 피어 블록을 통째로
            //   건너뛰어 단독(-NoPeer) 매니페스트가 '피어 포함 핸드셰이크 통과'로 보였다.
            if (m.Peer is null && (obs.Peer is not null || obs.Pin is not null))
            { detail = "피어 대조 요청인데 기록에 피어 없음(단독 출하 매니페스트) — 다중 저장소 봉인 아님"; return ReleaseHandshakeVerdict.PeerMissing; }
            if (m.Peer is { } p)
            {
                if (obs.Pin is { } pin && (!string.Equals(pin.Head, p.Commit, StringComparison.OrdinalIgnoreCase) || !HexEq(pin.ManifestSha256, p.ManifestSha256)))
                { detail = $"피어 {p.Name} 기록 {Short(p.Commit)} ≠ 패키징 시작 고정 {Short(pin.Head)}(또는 봉인 매니페스트 해시) — 빌드 중 피어 이동"; return ReleaseHandshakeVerdict.PeerDrift; }
                var po = obs.Peer;
                if (po?.Head is null) { detail = $"피어 {p.Name} 저장소 관측 불가"; return ReleaseHandshakeVerdict.PeerMissing; }
                if (!string.Equals(po.Name, p.Name, StringComparison.Ordinal) || !string.Equals(po.ManifestPath, p.ManifestPath, StringComparison.Ordinal))
                { detail = $"피어 이름/매니페스트 경로(기록 {p.Name}:{p.ManifestPath} ≠ 관측 {po.Name}:{po.ManifestPath})"; return ReleaseHandshakeVerdict.PeerIdentityMismatch; }
                if (po.DirtyPaths.Count > 0) { detail = $"피어 {p.Name} 미커밋 코드 {po.DirtyPaths.Count}건(예: {po.DirtyPaths[0]})"; return ReleaseHandshakeVerdict.PeerDirty; }
                if (!string.Equals(po.Head, p.Commit, StringComparison.OrdinalIgnoreCase)) { detail = $"피어 {p.Name} HEAD {Short(po.Head)} ≠ 기록 {Short(p.Commit)}"; return ReleaseHandshakeVerdict.PeerCommitMismatch; }
                if (po.ManifestSha256 is null || !HexEq(po.ManifestSha256, p.ManifestSha256) || po.RootHash != p.RootHash)
                { detail = $"피어 {p.Name} 봉인 매니페스트 해시/rootHash 불일치"; return ReleaseHandshakeVerdict.PeerManifestMismatch; }
            }
            detail = $"{m.Tag} @{Short(m.SourceCommit)} · artifacts={m.Artifacts.Count} · seal={m.MasterSeal[..16]}…" + (m.Peer is { } pp ? $" · peer {pp.Name}@{Short(pp.Commit)}" : "");
            return ReleaseHandshakeVerdict.Ok;
        }

        private static bool SameSequence(IReadOnlyList<string> recorded, IReadOnlyList<string>? observed, out string diff)
        {
            diff = "";
            if (observed is null) { diff = "태그 소스 관측 불가"; return false; }
            if (recorded.Count != observed.Count) { diff = $"개수 {recorded.Count} ≠ {observed.Count}"; return false; }
            for (int i = 0; i < recorded.Count; i++)
                if (!string.Equals(recorded[i], observed[i], StringComparison.Ordinal)) { diff = $"#{i} {recorded[i]} ≠ {observed[i]}"; return false; }
            return true;
        }

        private static bool SameArtifacts(IReadOnlyList<(string Name, string Sha256)> a, IReadOnlyList<(string Name, string Sha256)> b, out string diff)
        {
            diff = "";
            if (a.Count != b.Count) { diff = $"개수 {a.Count} ≠ {b.Count}"; return false; }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var x in b) map[x.Name] = x.Sha256;
            foreach (var x in a)
            {
                if (!map.TryGetValue(x.Name, out var sha)) { diff = "누락 " + x.Name; return false; }
                if (!HexEq(sha, x.Sha256)) { diff = "해시 " + x.Name; return false; }
            }
            return true;
        }

        private static bool HexEq(string a, string b)
        {
            if (a.Length != b.Length || a.Length % 2 != 0 || a.Length > 128) return false;
            Span<byte> bb = stackalloc byte[64];
            var t = bb[..(b.Length / 2)];
            if (!ZipSealDryRunVerifier.TryDecodeHex(b, t)) return false;
            return ZipSealDryRunVerifier.HexEquals(a, t);
        }

        private static string Short(string s) => s.Length > 10 ? s[..10] : s;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ③ git 관측(셸 없음 · 콜드)
    // ════════════════════════════════════════════════════════════════════════

    public static class GitRepoProbe
    {
        /// <summary>출하 바이너리에 영향 없는 경로(문서·산출물·도구 캐시) — 코드 이탈/미커밋 판정에서 제외.</summary>
        public static bool IsCodePath(string path)
        {
            string p = path.Replace('\\', '/').Trim().Trim('"');
            if (p.Length == 0) return false;
            if (p.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("release/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("graphify-out/", StringComparison.OrdinalIgnoreCase))
                return false;
            return !p.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSafeRef(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > 64 || tag[0] == '-') return false;
            foreach (char c in tag) if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
            return true;
        }

        public static RepoObservation Observe(string repoRoot, string tag)
        {
            string? head = Git(repoRoot, "rev-parse", "HEAD");
            string? tagCommit = IsSafeRef(tag) ? Git(repoRoot, "rev-list", "-n", "1", tag) : null;
            var dirty = StatusPaths(repoRoot);
            var drift = new List<string>();
            if (head is not null && tagCommit is not null && !string.Equals(head, tagCommit, StringComparison.OrdinalIgnoreCase))
            {
                string? names = Git(repoRoot, "diff", "--name-only", tagCommit, head);
                if (names is null) drift.Add("(git diff 실패)");
                else foreach (var l in names.Split('\n')) if (IsCodePath(l)) drift.Add(l.Trim());
            }
            return new RepoObservation(head, tagCommit, dirty, drift);
        }

        public const string InterlockSourcePath = "src/01_Core/ArmingInterlockBitmask.cs";

        /// <summary>
        /// 태그 커밋의 ArmingInterlockSource 열거 이름(값 오름차순 = Enum.GetNames 순서)을 소스에서 판독(git show · 콜드).
        /// 실패/형식 이탈(열거 블록 없음 · 값 중복 · 0개) = null(호출측 fail-closed).
        /// </summary>
        public static IReadOnlyList<string>? TagInterlockNames(string repoRoot, string? commit)
        {
            if (commit is null || commit.Length is < 7 or > 64) return null;
            foreach (char c in commit) if (!char.IsAsciiHexDigit(c)) return null;
            string? src = Git(repoRoot, "show", commit + ":" + InterlockSourcePath);
            return src is null ? null : ParseInterlockEnum(src);
        }

        public const string TightenSourcePath = "src/02_Infrastructure/Engine/TightenOnlyUiSettingWriter.cs";

        /// <summary>
        /// [v12.18.1-GM] 태그 커밋의 강화 전용 행렬("키|규칙|폴백|하한|상한" · 키 표 순서). 파일 부재(구 태그) = 빈 목록 ·
        /// git 실패 · 형식 이탈(열 길이 불일치 · 미지 규칙 · 비정수 · 표 모호) = null(호출측 fail-closed).
        /// </summary>
        public static IReadOnlyList<string>? TagTightenRules(string repoRoot, string? commit)
        {
            if (commit is null || commit.Length is < 7 or > 64) return null;
            foreach (char c in commit) if (!char.IsAsciiHexDigit(c)) return null;
            string? listed = Git(repoRoot, "ls-tree", "--name-only", commit, "--", TightenSourcePath);
            if (listed is null) return null;
            if (listed.Trim().Length == 0) return Array.Empty<string>();
            string? src = Git(repoRoot, "show", commit + ":" + TightenSourcePath);
            return src is null ? null : ParseTightenMatrix(src);
        }

        public static IReadOnlyList<string>? ParseTightenMatrix(string source)
        {
            static string? Block(string s, string anchor)
            {
                int at = s.IndexOf(anchor, StringComparison.Ordinal);
                if (at < 0 || s.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0) return null;   // 0개 · 2개 이상 = 모호
                int open = s.IndexOf('{', at), close = open < 0 ? -1 : s.IndexOf('}', open);
                return open < 0 || close < 0 ? null : s.Substring(open + 1, close - open - 1);
            }
            static List<string> Items(string block)
            {
                var r = new List<string>();
                foreach (var line in block.Split('\n'))
                {
                    string t = line;
                    int cm = t.IndexOf("//", StringComparison.Ordinal);
                    if (cm >= 0) t = t[..cm];
                    foreach (var item in t.Split(','))
                        if (item.Trim() is { Length: > 0 } x) r.Add(x);
                }
                return r;
            }
            static int? Int(string t)
                => int.TryParse(t.Replace("_", ""), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : null;
            string? kb = Block(source, "string[] Keys ="), rb = Block(source, "ReadOnlySpan<byte> Rules =>"),
                    fb = Block(source, "int[] Fallbacks ="), nb = Block(source, "int[] Minimums ="), xb = Block(source, "int[] Maximums =");
            if (kb is null || rb is null || fb is null || nb is null || xb is null) return null;
            var keys = Items(kb); var rules = Items(rb); var fbs = Items(fb); var mins = Items(nb); var maxs = Items(xb);
            int n = keys.Count;
            if (n == 0 || rules.Count != n || fbs.Count != n || mins.Count != n || maxs.Count != n) return null;
            var rows = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                string k = keys[i];
                if (k.Length < 3 || k[0] != '"' || k[^1] != '"') return null;
                k = k[1..^1];
                foreach (char c in k) if (!(char.IsAsciiLetterOrDigit(c) || c == '.')) return null;
                int? rule = rules[i] switch { "RuleTrueOnly" => 1, "RuleNonDecreasing" => 2, "RuleNonIncreasing" => 4, var t => Int(t) };
                int? f = Int(fbs[i]), lo = Int(mins[i]), hi = Int(maxs[i]);
                if (rule is not (1 or 2 or 4) || f is null || lo is null || hi is null) return null;
                rows.Add(FormattableString.Invariant($"{k}|{rule}|{f}|{lo}|{hi}"));
            }
            return rows;
        }

        public static IReadOnlyList<string>? ParseInterlockEnum(string source)
        {
            int at = source.IndexOf("enum ArmingInterlockSource", StringComparison.Ordinal);
            if (at < 0) return null;
            int open = source.IndexOf('{', at), close = open < 0 ? -1 : source.IndexOf('}', open);
            if (open < 0 || close < 0) return null;
            var items = new List<(long Value, string Name)>();
            foreach (var rawLine in source.AsSpan(open + 1, close - open - 1).ToString().Split('\n'))
            {
                string line = rawLine;
                int cm = line.IndexOf("//", StringComparison.Ordinal);
                if (cm >= 0) line = line[..cm];
                line = line.Trim().TrimEnd(',').Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) return null;                                              // 명시 값 없는 멤버 = 형식 이탈
                string name = line[..eq].Trim();
                if (name.Length == 0 || !long.TryParse(line[(eq + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v)) return null;
                foreach (char c in name) if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return null;
                foreach (var it in items) if (it.Value == v || it.Name == name) return null;
                items.Add((v, name));
            }
            if (items.Count == 0) return null;
            items.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            var names = new List<string>(items.Count);
            foreach (var it in items) names.Add(it.Name);
            return names;
        }

        public static List<string> StatusPaths(string repoRoot)
        {
            var list = new List<string>();
            string? st = Git(repoRoot, "status", "--porcelain", "--untracked-files=all");
            if (st is null) { list.Add("(git status 실패)"); return list; }
            foreach (var line in st.Split('\n'))
            {
                string raw = line.TrimEnd('\r');
                if (raw.Length < 4) continue;
                string p = raw[3..].Trim();
                int arrow = p.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) p = p[(arrow + 4)..];
                if (IsCodePath(p)) list.Add(p);
            }
            return list;
        }

        /// <summary>git 호출(셸 미경유 · ArgumentList) — 실패/비정상 종료 = null(fail-closed 판정은 호출측).</summary>
        public static string? Git(string repoRoot, params string[] args)
        {
            try
            {
                // [실결함 교정] 출력 인코딩 미지정 = 콘솔 코드페이지(한국어 Windows CP949)로 디코딩 — git 은 UTF-8 을 내보내므로 한글 바이트열의
                //   끝 바이트가 뒤따르는 줄바꿈을 2바이트 문자로 삼켜 줄이 병합됐다(태그 소스 열거 판독 시 한글 주석 다음 멤버 소실 ·
                //   core.quotepath=false porcelain 의 한글 경로 손상). UTF-8(BOM 없음)로 고정.
                var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repoRoot,
                                                        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                // [v12.17.x · 실결함 교정] 읽기 전용 피어(QuantumVault) 보호 — 일반 'git status' 는 인덱스 stat 캐시를 기회적으로 갱신하며
                //   .git/index 를 다시 쓴다(선택적 잠금). 판독 전용 호출이므로 전부 --no-optional-locks(쓰기 0).
                psi.ArgumentList.Add("--no-optional-locks");
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=false");
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) return null;
                var err = p.StandardError.ReadToEndAsync();   // stderr 파이프 포화 교착 방지(병행 배출)
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
                err.Wait(5_000);
                return p.ExitCode == 0 ? o.TrimEnd('\r', '\n', ' ') : null;   // ★앞 공백 보존 — porcelain 첫 줄 " D .claude/…" 의 상태열 손상 방지
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ④ 콜드 CLI
    // ════════════════════════════════════════════════════════════════════════

    public static class ReleaseHandshakeCli
    {
        public const string VerifyZipFlag = "--verify-zip", SyncFlag = "--sync-release-manifest", HandshakeFlag = "--release-handshake";
        public const string VerifyLiveArmedFlag = "--verify-live-armed", VerifyInterlocksFlag = "--verify-interlocks";
        public const string VerifyTightenFlag = "--verify-tighten", VerifyAuditChainFlag = "--verify-audit-chain";
        public const string PeerReadinessFlag = "--peer-readiness", PeerWatchFlag = "--peer-watch";
        public const int ExitOk = 0, ExitRejected = 2, ExitUsage = 64;
        public const string PeerManifestDefault = "release/offline-bundle/RELEASE_MANIFEST.json";

        public static bool IsInvocation(string[]? args)
            => args is { Length: > 0 } && (Eq(args[0], VerifyZipFlag) || Eq(args[0], VerifyLiveArmedFlag) || Eq(args[0], VerifyInterlocksFlag) || Eq(args[0], VerifyTightenFlag) || Eq(args[0], VerifyAuditChainFlag) || Eq(args[0], PeerReadinessFlag) || Eq(args[0], PeerWatchFlag) || Eq(args[0], SyncFlag) || Eq(args[0], HandshakeFlag));

        /// <summary>CLI 진입. --report &lt;파일&gt; 지정 시 같은 보고를 UTF-8 로 기록(PS 5.1 콘솔 코드페이지 무관 판독용).</summary>
        public static int Run(string[] args, TextWriter output)
        {
            var sw = new StringWriter();
            int code = RunCore(args, sw);
            output.Write(sw.ToString());
            string? report = ParseOptions(args).GetValueOrDefault("--report");
            if (!string.IsNullOrWhiteSpace(report))
            {
                try { File.WriteAllText(report, sw.ToString(), new UTF8Encoding(false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (code == ExitOk) code = ExitUsage; }
            }
            return code;
        }

        private static int RunCore(string[] args, TextWriter output)
        {
            var o = ParseOptions(args);
            try
            {
                if (Eq(args[0], VerifyZipFlag))
                {
                    string? zip = Arg(args, 1), sealFile = o.GetValueOrDefault("--seal-file");
                    if (zip is null || sealFile is null || !File.Exists(sealFile)) { output.WriteLine("[verify-zip] 사용: --verify-zip <zip> --seal-file <master-seal.txt>"); return ExitUsage; }
                    var v = ZipSealDryRunVerifier.Verify(zip, File.ReadAllText(sealFile), out string d);
                    output.WriteLine($"[verify-zip] {v}: {d}");
                    return v == ZipSealVerdict.Ok ? ExitOk : ExitRejected;
                }
                if (Eq(args[0], PeerWatchFlag))
                {
                    // [피어 대기 · 방향 B] 주기 관측 — 전이 = 블랙박스(102) · 같은 HEAD 연속 Ready 확정 시 런북 '알림'(출하 실행 0) → 종료 0.
                    //   --polls N(기본 1) · --interval-ms M(기본 60000) — 확정 없이 끝나면 2.
                    string? peerRoot = Arg(args, 1);
                    if (peerRoot is null) { output.WriteLine("[peer-watch] 사용: --peer-watch <피어 루트> [--polls N] [--interval-ms M] [--confirm K]"); return ExitUsage; }
                    int polls = int.TryParse(o.GetValueOrDefault("--polls"), out int pn) && pn > 0 ? pn : 1;
                    int interval = int.TryParse(o.GetValueOrDefault("--interval-ms"), out int im) && im >= 0 ? im : 60_000;
                    int confirm = int.TryParse(o.GetValueOrDefault("--confirm"), out int cf) && cf > 0 ? cf : PeerReadinessWatcher.DefaultConfirmPolls;
                    var box = new MmfFlightRecorder(1_024);
                    bool fired = false;
                    var w = new PeerReadinessWatcher(peerRoot, box, (rep, runbook) => { fired = true; output.WriteLine("[peer-watch] ★Ready 확정 HEAD " + rep.Head + " — " + runbook); },
                                                     confirm, o.GetValueOrDefault("--peer-manifest") ?? PeerReadinessProbe.DefaultManifest);
                    for (int i = 0; i < polls && !fired; i++)
                    {
                        if (i > 0 && interval > 0) Thread.Sleep(interval);
                        w.Poll();
                        var rep = w.Last!;
                        output.WriteLine($"[peer-watch] #{i + 1} {(rep.Ready ? "Ready" : "NotReady")} HEAD {rep.Head ?? "?"} · 미커밋 코드 {rep.DirtyCodePaths} · 키 의심 파일 {rep.KeyFiles}");
                        foreach (var (issue, detail) in rep.Issues) output.WriteLine("  " + issue + ": " + detail);
                    }
                    output.WriteLine($"[peer-watch] 폴링 {w.Polls} · 전이 {w.Transitions} · 블랙박스 {box.TotalRecorded} · 런북 트리거 {w.RunbookTriggers}");
                    return fired ? ExitOk : ExitRejected;
                }
                if (Eq(args[0], PeerReadinessFlag))
                {
                    // [단독 운용 · 방향 B] 피어 정돈 완료 비파괴 판정 — 0 = Ready(정식 모드 집행 가능) · 2 = NotReady
                    string? peerRoot = Arg(args, 1);
                    if (peerRoot is null) { output.WriteLine("[peer-readiness] 사용: --peer-readiness <피어 루트> [--peer-manifest <상대 경로>]"); return ExitUsage; }
                    var rep = PeerReadinessProbe.Probe(peerRoot, o.GetValueOrDefault("--peer-manifest") ?? PeerReadinessProbe.DefaultManifest);
                    output.WriteLine($"[peer-readiness] {(rep.Ready ? "Ready" : "NotReady")}: HEAD {rep.Head ?? "?"} · 미커밋 코드 {rep.DirtyCodePaths} · 키 의심 파일 {rep.KeyFiles}");
                    foreach (var (issue, detail) in rep.Issues) output.WriteLine("  " + issue + ": " + detail);
                    return rep.Ready ? ExitOk : ExitRejected;
                }
                if (Eq(args[0], VerifyLiveArmedFlag))
                {
                    // [v12.16.1-GM · 방향 B] 출하 zip 안 호스트 DLL 의 이중 물리 키 기본값을 IL 로 직접 판독(추출 · 로드 · 실행 0)
                    string? zip = Arg(args, 1);
                    if (zip is null || !File.Exists(zip)) { output.WriteLine("[verify-live-armed] 사용: --verify-live-armed <zip> [--expect-tighten-keys <n>] [--require-audit-ledger]"); return ExitUsage; }
                    // [v12.18.1-GM] 강화 전용 키 수 고정(스크립트 = 6) — 가드의 '경로 전체 부재 = 통과'(구 바이너리 호환)를 출하 게이트에선 닫는다
                    int expectTighten = -1;
                    if (o.TryGetValue("--expect-tighten-keys", out string? et) && (!int.TryParse(et, out expectTighten) || expectTighten < 0))
                    { output.WriteLine("[verify-live-armed] --expect-tighten-keys <n> 형식 오류"); return ExitUsage; }
                    var r = ShippedBinaryLiveArmedGuard.ScanZip(zip);
                    output.WriteLine($"[verify-live-armed] {(r.Ok ? "Ok" : "Rejected")}: 조회 지점 {r.Sites} · 마스터 플래그 생성 {r.MasterSites} · UI 설정 기입 가드 {r.UiGuardSites}/3 · 시뮬레이션 격리 형식 {r.SimulationTypes}(CLI 참조 {r.SimulationCliSites}) · 강화 전용 키 {r.TightenKeys}(호스트 판독 {r.TightenHostSites}) · 감사 원장 ⑪ {(r.AuditLedgerAware ? "적용" : "없음")} · 위반 {r.Violations.Count}");
                    foreach (var v in r.Violations) output.WriteLine("  " + v);
                    if (expectTighten >= 0 && r.TightenKeys != expectTighten)
                    {
                        output.WriteLine($"  강화 전용 키 {r.TightenKeys} ≠ 출하 고정 {expectTighten} — 단조성 기입기 누락/변형 출하 거부");
                        return ExitRejected;
                    }
                    // [v12.18.2-GM] 규칙 ⑪은 ITightenAuditSink 형식이 있을 때만 적용(구 바이너리 호환) — 출하 게이트는 형식 부재(= ⑪ 조용한 생략)를 거부
                    if (o.ContainsKey("--require-audit-ledger") && !r.AuditLedgerAware)
                    {
                        output.WriteLine("  감사 원장(ITightenAuditSink) 부재 — 규칙 ⑪ 판독 대상 소실 · 무감사 기입기 출하 거부");
                        return ExitRejected;
                    }
                    return r.Ok ? ExitOk : ExitRejected;
                }
                if (Eq(args[0], VerifyAuditChainFlag))
                {
                    // [v12.18.2-DEV · 방향 A] 강화 전용 감사 원장(MMF 해시 체인) 무재기동 정적 검증 — 실행 중 호스트와 공존(읽기 전용 뷰 · 복사 0).
                    //  선택 외부 앵커: --expect-head <64hex>(운영자가 따로 보관한 머리 해시) · --expect-count <n> — 오프라인 '일관 재해시' 위조 대조.
                    string? file = Arg(args, 1), eh = o.GetValueOrDefault("--expect-head"), ec = o.GetValueOrDefault("--expect-count");
                    int expectCount = -1;
                    if (file is null || !File.Exists(file) || (eh is not null && !IsHex(eh, 64)) || (ec is not null && (!int.TryParse(ec, out expectCount) || expectCount < 0)))
                    { output.WriteLine("[verify-audit-chain] 사용: --verify-audit-chain <tighten-audit.mmf> [--expect-head <64hex>] [--expect-count <n>]"); return ExitUsage; }
                    Span<byte> head = stackalloc byte[32];
                    var v = StockManager.Security.MonotonicSettingAuditLog.VerifyFile(file, out int n, head, out int at, out long us);
                    string headHex = Convert.ToHexString(head);
                    if (v == StockManager.Security.AuditChainVerdict.Ok && ((eh is not null && !string.Equals(eh, headHex, StringComparison.OrdinalIgnoreCase)) || (expectCount >= 0 && expectCount != n)))
                        v = StockManager.Security.AuditChainVerdict.AnchorMismatch;
                    output.WriteLine($"[verify-audit-chain] {v}: 레코드 {n} · 머리 해시 {(v == StockManager.Security.AuditChainVerdict.Ok || v == StockManager.Security.AuditChainVerdict.AnchorMismatch ? headHex : "-")}{(at >= 0 ? " · 첫 불일치 #" + at : "")} · 검증 {us}µs");
                    return v == StockManager.Security.AuditChainVerdict.Ok ? ExitOk : ExitRejected;
                }
                if (Eq(args[0], VerifyTightenFlag))
                {
                    // [v12.18.1-GM · 방향 A] 출하 zip 호스트 DLL 강화 전용 행렬(IL) ↔ RELEASE_MANIFEST tightenRules ↔ 가드 명세 ↔ master-seal.txt 무추출 대조
                    string? zip = Arg(args, 1), mf = o.GetValueOrDefault("--manifest"), sealFile = o.GetValueOrDefault("--seal-file");
                    if (zip is null || mf is null || sealFile is null || !File.Exists(zip) || !File.Exists(mf) || !File.Exists(sealFile)
                        || !int.TryParse(o.GetValueOrDefault("--expect-tighten-keys"), out int expect))
                    { output.WriteLine("[verify-tighten] 사용: --verify-tighten <zip> --manifest <RELEASE_MANIFEST.json> --seal-file <master-seal.txt> --expect-tighten-keys <n> [--require-audit-ledger] [--require-audit-telemetry]"); return ExitUsage; }
                    // [v12.18.3-GM · 방향 A] --require-audit-telemetry = 관제 배지 무잠금 seqlock 결선(⑤) 수검 추가
                    var r = ShippedTightenMatrixVerifier.Verify(zip, File.ReadAllText(mf), File.ReadAllText(sealFile), expect, o.ContainsKey("--require-audit-ledger"),
                                                                o.ContainsKey("--require-audit-telemetry"));
                    output.WriteLine($"[verify-tighten] {r.Verdict}: {r.Detail} · 팽창+해시 {r.InflateMicros}µs · 행렬 대조 {r.CompareMicros}µs");
                    return r.Verdict == TightenSealVerdict.Ok ? ExitOk : ExitRejected;
                }
                if (Eq(args[0], VerifyInterlocksFlag))
                {
                    // [v12.18.0-GM · 방향 A] 출하 zip 호스트 DLL 인터락 정의 ↔ RELEASE_MANIFEST ↔ master-seal.txt 무추출 대조(기대 개수 = 스크립트 고정)
                    string? zip = Arg(args, 1), mf = o.GetValueOrDefault("--manifest"), sealFile = o.GetValueOrDefault("--seal-file");
                    if (zip is null || mf is null || sealFile is null || !File.Exists(zip) || !File.Exists(mf) || !File.Exists(sealFile)
                        || !int.TryParse(o.GetValueOrDefault("--expect-interlocks"), out int expect))
                    { output.WriteLine("[verify-interlocks] 사용: --verify-interlocks <zip> --manifest <RELEASE_MANIFEST.json> --seal-file <master-seal.txt> --expect-interlocks <n>"); return ExitUsage; }
                    var r = ShippedInterlockManifestVerifier.Verify(zip, File.ReadAllText(mf), File.ReadAllText(sealFile), expect);
                    output.WriteLine($"[verify-interlocks] {r.Verdict}: {r.Detail} · 팽창+해시 {r.InflateMicros}µs · 정의 대조 {r.CompareMicros}µs");
                    return r.Verdict == InterlockSealVerdict.Ok ? ExitOk : ExitRejected;
                }
                string? releaseDir = o.GetValueOrDefault("--release-dir"), repo = o.GetValueOrDefault("--repo"), shim = o.GetValueOrDefault("--shim");
                if (releaseDir is null || repo is null || shim is null) { output.WriteLine("[handshake] 사용: --release-dir <dir> --repo <root> --shim <dll> [--peer <root>] [--peer-manifest <rel>] [--out|--manifest <json>]"); return ExitUsage; }
                string? peer = o.GetValueOrDefault("--peer");
                string peerRel = o.GetValueOrDefault("--peer-manifest") ?? PeerManifestDefault;
                // [방향 B] 피어 고정값(--pin-peer-head 40hex · --pin-peer-manifest 64hex) — 둘 다 · 피어와 함께만 · 형식 이탈 = 사용 오류(거부)
                string? pinHead = o.GetValueOrDefault("--pin-peer-head"), pinSha = o.GetValueOrDefault("--pin-peer-manifest");
                PeerPin? pin = null;
                if (pinHead is not null || pinSha is not null)
                {
                    if (peer is null || !IsHex(pinHead, 40) || !IsHex(pinSha, 64)) { output.WriteLine("[handshake] 피어 고정값 형식 오류(--peer 와 함께 --pin-peer-head <40hex> --pin-peer-manifest <64hex>)"); return ExitUsage; }
                    pin = new PeerPin(pinHead!, pinSha!);
                }
                if (Eq(args[0], SyncFlag))
                {
                    if (!o.ContainsKey("--live-armed-guard-passed")) { output.WriteLine("[sync] liveArmed 소스 가드 통과 표지 없음 — 기록 거부"); return ExitRejected; }
                    string outPath = o.GetValueOrDefault("--out") ?? Path.Combine(releaseDir, MultiRepoReleaseManifest.FileName);
                    if (!TryCollect(releaseDir, repo, shim, peer, peerRel, out var obs, out var sig, out string why)) { output.WriteLine("[sync] 수집 실패: " + why); return ExitRejected; }
                    obs = obs with { Pin = pin };
                    if (obs.Repo.TagCommit is null) { output.WriteLine($"[sync] 태그 v{sig.Version} 없음 — 기록 거부"); return ExitRejected; }
                    PeerRecord? pr = obs.Peer is { Head: { } ph } po ? new PeerRecord(po.Name ?? "", ph, peerRel, po.ManifestSha256 ?? "", po.RootHash ?? "") : null;
                    if (peer is not null && pr is null) { output.WriteLine("[sync] 피어 저장소 관측 불가 — 기록 거부"); return ExitRejected; }
                    if (obs.TagInterlocks is null) { output.WriteLine("[sync] 태그 소스 인터락 열거 판독 실패 — 기록 거부"); return ExitRejected; }
                    if (obs.TagTightenRules is null) { output.WriteLine("[sync] 태그 소스 강화 전용 행렬 판독 실패 — 기록 거부"); return ExitRejected; }
                    // [다중 저장소 봉인 감사] 인터락 목록 = 태그 소스 열거(실행 바이너리 Enum.GetNames 아님 — 검증 시 같은 출처로 재대조)
                    var m = new ReleaseManifestInput(sig.Version, "v" + sig.Version, obs.Repo.TagCommit, obs.ManifestSigSha256!, sig.MasterSeal, sig.Genesis,
                        obs.ManifestSigArtifacts, sig.ZipName, obs.ZipSha256 ?? "", obs.ShimSha256 ?? "", false, obs.TagInterlocks, pr,
                        obs.TagTightenRules.Count > 0 ? obs.TagTightenRules : null);
                    string json = MultiRepoReleaseManifest.Build(m);
                    // 원자적 기록: 임시 파일 → 즉시 핸드셰이크 → 통과 시에만 교체(실패 = 기존 파일 보존 · 임시 삭제)
                    string tmp = outPath + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    var hv = MultiRepoReleaseManifest.Verify(File.ReadAllText(tmp), obs, out string hd);
                    if (hv != ReleaseHandshakeVerdict.Ok) { File.Delete(tmp); output.WriteLine($"[sync] 기록 직후 핸드셰이크 거부 {hv}: {hd}"); return ExitRejected; }
                    File.Move(tmp, outPath, overwrite: true);
                    output.WriteLine($"[sync] {MultiRepoReleaseManifest.FileName} 기록: {outPath} · {hd}");
                    return ExitOk;
                }
                {
                    string manifestPath = o.GetValueOrDefault("--manifest") ?? Path.Combine(releaseDir, MultiRepoReleaseManifest.FileName);
                    if (!File.Exists(manifestPath)) { output.WriteLine("[handshake] 매니페스트 없음: " + manifestPath); return ExitRejected; }
                    if (!TryCollect(releaseDir, repo, shim, peer, peerRel, out var obs, out _, out string why)) { output.WriteLine("[handshake] 수집 실패: " + why); return ExitRejected; }
                    obs = obs with { Pin = pin };
                    var v = MultiRepoReleaseManifest.Verify(File.ReadAllText(manifestPath), obs, out string d);
                    output.WriteLine($"[handshake] {v}: {d}");
                    return v == ReleaseHandshakeVerdict.Ok ? ExitOk : ExitRejected;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine("[handshake] I/O 실패: " + ex.Message);
                return ExitRejected;
            }
        }

        public readonly record struct SigFacts(string Version, string MasterSeal, string Genesis, string ZipName);

        /// <summary>release 폴더·저장소·쉼 산출물·피어에서 실측값 수집(콜드).</summary>
        public static bool TryCollect(string releaseDir, string repo, string shim, string? peer, string peerRel, out HandshakeObservation obs, out SigFacts sig, out string why)
        {
            obs = null!; sig = default; why = "";
            string sigPath = Path.Combine(releaseDir, "manifest.sig");
            if (!File.Exists(sigPath)) { why = "manifest.sig 없음"; return false; }
            string sigText = File.ReadAllText(sigPath);
            if (!BootIntegrityGate.TryParse(sigText, out var entries, out string seal, out string pw)) { why = "manifest.sig 무효: " + pw; return false; }
            string version, genesis;
            using (var doc = JsonDocument.Parse(sigText))
            {
                version = doc.RootElement.TryGetProperty("version", out var ve) ? ve.GetString() ?? "" : "";
                genesis = doc.RootElement.TryGetProperty("genesisHeadHash", out var ge) ? ge.GetString() ?? "" : "";
            }
            if (version.Length == 0 || !GitRepoProbe.IsSafeRef("v" + version)) { why = "manifest.sig version 무효"; return false; }
            var arts = new List<(string, string)>(entries.Count);
            foreach (var (n, h) in entries) arts.Add((n, Convert.ToHexString(h)));
            string zipName = "release-v" + version + ".zip";
            string zipPath = Path.Combine(releaseDir, zipName);
            string anchorPath = Path.Combine(releaseDir, "master-seal.txt");
            PeerObservation? po = null;
            if (peer is not null)
            {
                string pm = Path.Combine(peer, peerRel.Replace('/', Path.DirectorySeparatorChar));
                string? root = null;
                if (File.Exists(pm))
                {
                    try { using var pd = JsonDocument.Parse(File.ReadAllText(pm)); root = pd.RootElement.TryGetProperty("rootHash", out var rh) ? rh.GetString() : null; }
                    catch (JsonException) { root = null; }
                }
                po = new PeerObservation(GitRepoProbe.Git(peer, "rev-parse", "HEAD"), GitRepoProbe.StatusPaths(peer), File.Exists(pm) ? FileSha(pm) : null, root,
                                         peerRel, Path.GetFileName(Path.GetFullPath(peer).TrimEnd('\\', '/')));
            }
            sig = new SigFacts(version, seal, genesis, zipName);
            var repoObs = GitRepoProbe.Observe(repo, "v" + version);
            obs = new HandshakeObservation(repoObs, FileSha(sigPath), version, seal, arts,
                File.Exists(zipPath) ? FileSha(zipPath) : null, File.Exists(anchorPath) ? File.ReadAllText(anchorPath).Trim() : null,
                File.Exists(shim) ? FileSha(shim) : null, po,
                genesis.Length > 0 ? genesis : null, GitRepoProbe.TagInterlockNames(repo, repoObs.TagCommit),
                TagTightenRules: GitRepoProbe.TagTightenRules(repo, repoObs.TagCommit));
            return true;
        }

        public static string FileSha(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs));
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                d[args[i]] = hasValue ? args[++i].Trim('"') : "";
            }
            return d;
        }

        private static bool IsHex(string? s, int len)
        {
            if (s is null || s.Length != len) return false;
            foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        private static string? Arg(string[] args, int i) => i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i].Trim('"') : null;
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

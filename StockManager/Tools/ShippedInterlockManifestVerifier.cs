// ============================================================================
// ShippedInterlockManifestVerifier — [출하 · v12.18.0-GM · 방향 A] 30대 인터락 봉인 무추출 대조(출하 zip 호스트 DLL ↔ RELEASE_MANIFEST ↔ master-seal.txt)
//
//  기존 [10/10] 핸드셰이크는 매니페스트 interlocks 를 '태그 소스'(git show <태그>:ArmingInterlockBitmask.cs)와 대조한다. 그러나 출하되는 것은
//   소스가 아니라 zip 안 호스트 DLL 이다 — 소스 ↔ 바이너리 사이(빌드 산출물 교체 · 스테일 bin · 다른 커밋 빌드)는 대조되지 않았다.
//   이 검증기는 그 빈틈을 닫는 세 번째 독립 경로다(디스크 추출 0 · 어셈블리 로드 0 · 코드 실행 0):
//    ① 매니페스트 자기 봉인(manifestHash 재계산) · liveArmedDefault=false · masterSeal == master-seal.txt(외부 앵커).
//    ② zip = MemoryMappedFile 읽기 전용 뷰(파일 전체 복사 0) — 항목 이름 정규화('\' → '/' · 대소문자 무시) 후 중복 = 거부(방향 B 가드와 같은 규칙).
//       호스트 DLL 항목만 고정 버퍼 1개로 팽창 → SHA-256 == 매니페스트 artifacts[host/MS2E.StockSuite.dll](판독한 바이트 = 봉인된 바이트).
//    ③ ★대조(타이밍 대상 · 1ms 예산): 팽창 버퍼를 복사 없이 ImmutableArray 로 감싸 PEReader → MetadataReader. MS2E.Core.ArmingInterlockSource
//       TypeDef 정확히 1개 · 리터럴 필드 = 상수 blob(byte) 판독 · 값 오름차순(= Enum.GetNames 순서 = 매니페스트 기록 순서) 정렬 ·
//       이름 i == 매니페스트 interlocks[i](MetadataStringComparer — 문자열 할당 0) · 값 중복/범위 이탈 = 거부 ·
//       정의 비트마스크 == 구조 규칙(0 ~ n-2 연속 + StartupSanity 63) · 개수 == 기대값(스크립트가 고정 — 새 비트 추가 시 스크립트 갱신 강제).
//   하나라도 어긋나면 거부 → 스크립트가 출하 산출물 전량 폐기(Remove-ReleaseOutputs). 판독 실패 = 거부(fail-closed).
//
//  δ=0 범위(정직): 콜드 출하 도구다. ③의 정의 대조 루프(CompareDefinition)는 힙 0(테스트 실측) — PEReader/MetadataReader 객체 생성과
//   ②의 팽창 버퍼(호스트 DLL 크기 1회)는 할당한다. '1ms' 는 ③(PE 헤더 · 메타데이터 판독 + 대조)에 대한 것이며, ②의 Deflate 팽창(수 MB)은
//   1ms 안에 끝나지 않는다 — 보고서가 두 시간을 분리해 기록한다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MS2E.Infrastructure;

namespace StockManager.Tools
{
    public enum InterlockSealVerdict : byte
    {
        Ok = 0, ManifestInvalid = 1, AnchorMismatch = 2, ZipUnreadable = 3, EntryMissing = 4, EntryDuplicate = 5,
        ArtifactHashMismatch = 6, PeUnreadable = 7, EnumMissing = 8, EnumAmbiguous = 9, CountMismatch = 10,
        ValueInvalid = 11, NameMismatch = 12, MaskMismatch = 13,
    }

    public sealed record InterlockSealReport(
        InterlockSealVerdict Verdict, string Detail, int Count, ulong DefinitionMask, ulong ExpectedMask,
        string? HostSha256, long InflateMicros, long CompareMicros);

    public static class ShippedInterlockManifestVerifier
    {
        public const string EnumNamespace = "MS2E.Core", EnumName = "ArmingInterlockSource";
        public const string HostEntry = ShippedBinaryLiveArmedGuard.HostEntry;
        public const int SanityBit = 63, MaxDefinitions = 64;
        public const int MaxHostImageBytes = 256 * 1024 * 1024;

        /// <summary>구조 규칙 마스크 — 비트 0 ~ count-2 연속 + StartupSanity(63). count = 30 → 0x800000001FFFFFFF.</summary>
        public static ulong ExpectedMaskFor(int count)
            => count < 1 || count > MaxDefinitions ? 0 : (count - 1 == 0 ? 0 : (1UL << (count - 1)) - 1) | (1UL << SanityBit);

        /// <summary>출하 zip ↔ 매니페스트(json) ↔ 외부 앵커(master-seal.txt 내용) 3자 대조.</summary>
        public static InterlockSealReport Verify(string zipPath, string manifestJson, string anchorText, int expectedCount)
        {
            ulong expMask = ExpectedMaskFor(expectedCount);
            InterlockSealReport R(InterlockSealVerdict v, string d, int n = 0, ulong mask = 0, string? sha = null, long inf = 0, long cmp = 0)
                => new(v, d, n, mask, expMask, sha, inf, cmp);

            // ① 매니페스트 · 앵커
            if (!MultiRepoReleaseManifest.TryParse(manifestJson ?? "", out var m, out string stored, out string why)) return R(InterlockSealVerdict.ManifestInvalid, why);
            if (!string.Equals(stored, MultiRepoReleaseManifest.CanonicalHash(m), StringComparison.OrdinalIgnoreCase))
                return R(InterlockSealVerdict.ManifestInvalid, "manifestHash ≠ 정규화 재계산(매니페스트 변조)");
            if (m.LiveArmedDefault) return R(InterlockSealVerdict.ManifestInvalid, "liveArmedDefault=true — Fail-Closed 불변식 위반");
            if (expMask == 0) return R(InterlockSealVerdict.CountMismatch, "기대 인터락 수 범위 이탈: " + expectedCount);
            string anchor = (anchorText ?? "").Trim();
            if (!BootIntegrityGate.IsStrictSealHex(anchor) || !string.Equals(anchor, m.MasterSeal, StringComparison.OrdinalIgnoreCase))
                return R(InterlockSealVerdict.AnchorMismatch, "master-seal.txt ≠ 매니페스트 masterSeal(또는 64 hex 아님)");
            if (m.Interlocks.Count != expectedCount)
                return R(InterlockSealVerdict.CountMismatch, $"매니페스트 인터락 {m.Interlocks.Count} ≠ 기대 {expectedCount}");

            // ② MMF 뷰 · 항목 정규화/중복 가드 · 호스트 DLL 팽창 + 봉인 해시
            var lv = LoadSealedHost(zipPath, m, out byte[] image, out string? sha, out long inflateUs, out string lwhy);
            if (lv != InterlockSealVerdict.Ok) return R(lv, lwhy, sha: sha, inf: inflateUs);

            // ③ 정의 대조(복사 없는 PE 뷰)
            long t1 = Stopwatch.GetTimestamp();
            InterlockSealVerdict v; int count; ulong mask; int at;
            try
            {
                using var pe = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(image));
                if (!pe.HasMetadata) return R(InterlockSealVerdict.PeUnreadable, "메타데이터 없음", sha: sha, inf: inflateUs);
                var md = pe.GetMetadataReader();
                v = CompareDefinition(md, m.Interlocks, expMask, out count, out mask, out at);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                return R(InterlockSealVerdict.PeUnreadable, "PE/메타데이터 판독 실패: " + ex.GetType().Name, sha: sha, inf: inflateUs);
            }
            long compareUs = Micros(Stopwatch.GetTimestamp() - t1);
            string detail = v switch
            {
                InterlockSealVerdict.Ok => $"인터락 {count} · 정의 마스크 0x{mask:X16} = 규칙 · 이름 순서 = 매니페스트 · 호스트 DLL = 봉인 해시",
                InterlockSealVerdict.EnumMissing => EnumNamespace + "." + EnumName + " 정의 없음",
                InterlockSealVerdict.EnumAmbiguous => EnumNamespace + "." + EnumName + " 정의 2개 이상",
                InterlockSealVerdict.CountMismatch => $"바이너리 인터락 {count} ≠ 매니페스트 {m.Interlocks.Count}",
                InterlockSealVerdict.ValueInvalid => $"#{at} 값 중복/범위 이탈/비 byte 상수",
                InterlockSealVerdict.NameMismatch => $"#{at} 바이너리 이름 ≠ 매니페스트 {m.Interlocks[at]}",
                InterlockSealVerdict.MaskMismatch => $"정의 마스크 0x{mask:X16} ≠ 규칙 0x{expMask:X16}",
                _ => v.ToString(),
            };
            return R(v, detail, count, mask, sha, inflateUs, compareUs);
        }

        /// <summary>
        /// ② 공용(인터락 · 강화 전용 행렬 검증기): 매니페스트 호스트 DLL 봉인 해시 1개 확정 → zip MMF 뷰 · 항목 정규화/중복 가드 →
        /// 호스트 DLL 팽창 → SHA-256 == 봉인 해시. Ok 일 때만 image 가 '봉인된 바로 그 바이트'. 실패 판정은 3~7(ZipUnreadable ~ PeUnreadable)만.
        /// </summary>
        internal static InterlockSealVerdict LoadSealedHost(string zipPath, ReleaseManifestInput m, out byte[] image, out string? sha, out long inflateUs, out string why)
        {
            image = Array.Empty<byte>(); sha = null; inflateUs = 0; why = "";
            string? sealedSha = null;
            foreach (var a in m.Artifacts)
                if (string.Equals(a.Name.Replace('\\', '/'), HostEntry, StringComparison.OrdinalIgnoreCase))
                {
                    if (sealedSha is not null) { why = "매니페스트 호스트 DLL 항목 중복"; return InterlockSealVerdict.EntryDuplicate; }
                    sealedSha = a.Sha256;
                }
            if (sealedSha is null) { why = "매니페스트 artifacts 에 " + HostEntry + " 없음"; return InterlockSealVerdict.EntryMissing; }
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                long zipLen = new FileInfo(zipPath).Length;
                using var mmf = MemoryMappedFile.CreateFromFile(zipPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var view = mmf.CreateViewStream(0, zipLen, MemoryMappedFileAccess.Read);   // 정확한 길이(페이지 반올림 0 패딩이 EOCD 탐색에 섞이지 않게)
                using var zip = new ZipArchive(view, ZipArchiveMode.Read, leaveOpen: false);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ZipArchiveEntry? host = null;
                foreach (var e in zip.Entries)
                {
                    string norm = e.FullName.Replace('\\', '/');
                    if (!seen.Add(norm)) { why = "zip 항목 중복(정규화 후): " + norm; return InterlockSealVerdict.EntryDuplicate; }   // Windows 해제 시 덮어쓰기 모호
                    if (string.Equals(norm, HostEntry, StringComparison.OrdinalIgnoreCase)) host = e;
                }
                if (host is null) { why = "zip 항목 없음: " + HostEntry; return InterlockSealVerdict.EntryMissing; }
                if (host.Length <= 0 || host.Length > MaxHostImageBytes) { why = "호스트 DLL 크기 이탈: " + host.Length; return InterlockSealVerdict.PeUnreadable; }
                image = GC.AllocateUninitializedArray<byte>((int)host.Length);
                using (var s = host.Open()) s.ReadExactly(image);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                why = "zip 판독 실패: " + ex.GetType().Name;
                return InterlockSealVerdict.ZipUnreadable;
            }
            sha = Convert.ToHexString(SHA256.HashData(image));
            inflateUs = Micros(Stopwatch.GetTimestamp() - t0);
            if (!string.Equals(sha, sealedSha, StringComparison.OrdinalIgnoreCase))
            { why = "zip 호스트 DLL SHA-256 ≠ 매니페스트 봉인 해시(판독 대상 ≠ 봉인 대상)"; return InterlockSealVerdict.ArtifactHashMismatch; }
            return InterlockSealVerdict.Ok;
        }

        /// <summary>
        /// 메타데이터 정의 ↔ 기대 이름 순서 대조(힙 0 — 스택 버퍼 · MetadataStringComparer). at = 첫 불일치 위치(값 오름차순 인덱스).
        /// </summary>
        public static InterlockSealVerdict CompareDefinition(MetadataReader md, IReadOnlyList<string> expectedNames, ulong expectedMask,
                                                             out int count, out ulong mask, out int at)
        {
            count = 0; mask = 0; at = -1;
            TypeDefinitionHandle found = default;
            int defs = 0;
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                if (!md.StringComparer.Equals(td.Name, EnumName) || !md.StringComparer.Equals(td.Namespace, EnumNamespace)) continue;
                found = th; defs++;
            }
            if (defs == 0) return InterlockSealVerdict.EnumMissing;
            if (defs > 1) return InterlockSealVerdict.EnumAmbiguous;

            Span<byte> values = stackalloc byte[MaxDefinitions];
            Span<int> rows = stackalloc int[MaxDefinitions];
            foreach (var fh in md.GetTypeDefinition(found).GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & FieldAttributes.Literal) == 0) continue;               // value__(인스턴스 저장 필드) 제외
                if (count == MaxDefinitions) { at = count; return InterlockSealVerdict.ValueInvalid; }
                var ch = f.GetDefaultValue();
                if (ch.IsNil) { at = count; return InterlockSealVerdict.ValueInvalid; }
                var c = md.GetConstant(ch);
                if (c.TypeCode != ConstantTypeCode.Byte) { at = count; return InterlockSealVerdict.ValueInvalid; }   // 열거 기반 형식 = byte
                byte b = md.GetBlobReader(c.Value).ReadByte();
                if (b >= MaxDefinitions || (mask & (1UL << b)) != 0) { at = count; return InterlockSealVerdict.ValueInvalid; }
                mask |= 1UL << b;
                // 삽입 정렬(값 오름차순 — n ≤ 64 · 선언 순서와 무관하게 Enum.GetNames 순서 재현)
                int i = count++;
                while (i > 0 && values[i - 1] > b) { values[i] = values[i - 1]; rows[i] = rows[i - 1]; i--; }
                values[i] = b; rows[i] = MetadataTokens.GetRowNumber(fh);
            }
            if (count != expectedNames.Count) return InterlockSealVerdict.CountMismatch;
            for (int i = 0; i < count; i++)
            {
                var f = md.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(rows[i]));
                if (!md.StringComparer.Equals(f.Name, expectedNames[i])) { at = i; return InterlockSealVerdict.NameMismatch; }
            }
            return mask == expectedMask ? InterlockSealVerdict.Ok : InterlockSealVerdict.MaskMismatch;
        }

        internal static long Micros(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;
    }
}

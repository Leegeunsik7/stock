// ============================================================================
// ShippedTightenMatrixVerifier — [출하 · v12.18.1-GM · 방향 A] 강화 전용 단조성 행렬 무추출 · 무할당 대조
//                                (출하 zip 호스트 DLL IL ↔ RELEASE_MANIFEST tightenRules ↔ 가드 고정 명세 ↔ master-seal.txt)
//
//  ShippedBinaryLiveArmedGuard ⑦~⑩ 는 '강화 전용 경로가 있으면' 내부 정합(키 ↔ 명세 · 폴백 ↔ 호스트 판독 상수)을 본다. 두 빈틈이 남았다:
//   ⓐ 경로 전체 부재 = 통과(구 바이너리 호환) — 출하물에서 기입기를 통째로 뺀 빌드가 조용히 통과할 수 있다.
//   ⓑ 판독 결과가 출하 기록(매니페스트)에 묶이지 않는다 — '무엇을 출하했는가'의 기록이 행렬 값을 담지 않았다.
//  이 검증기는 그 둘을 닫는다(디스크 추출 0 · 어셈블리 로드 0 · 코드 실행 0):
//   ① 매니페스트 자기 봉인 · liveArmedDefault=false · masterSeal == master-seal.txt · tightenRules 존재 + 개수 == 기대값(스크립트 고정 6) ·
//      각 행 "키|규칙|폴백|하한|상한" 형식 · 키/규칙 == 가드 고정 명세(TightenSpec) — 매니페스트 행렬 자체가 명세 밖이면 거부.
//   ② zip MMF 뷰 · 항목 정규화/중복 가드 · 호스트 DLL 팽창 → SHA-256 == 매니페스트 봉인 해시(ShippedInterlockManifestVerifier 와 같은 로더).
//   ③ ★대조(CompareMatrix · 힙 0): MS2E.Infrastructure.TightenOnlyUiSettingWriter TypeDef 정확히 1개 →
//      .cctor IL 을 스택 버퍼로 해독 — ldstr 키 순서 i == 매니페스트 키 i(#US 힙 UTF-16 을 BlobReader 로 직접 비교 · 문자열 생성 0) ·
//      ldtoken F → stsfld Fallbacks/Minimums/Maximums 의 RVA 블롭(int32 LE) == 매니페스트 열 ·
//      get_Rules 의 RVA 블롭(byte) == 매니페스트 규칙 열 ·
//      IsTightening 비교 방향: 규칙 비트 1/2/4 판독(ldarg.0 · ldc · and) 뒤 TrueOnly → `proposed == 1`(BooleanTrueOnly) ·
//      NonDecreasing → `!(proposed < current)`(GreaterThanOrEqual) · NonIncreasing → `!(proposed > current)`(LessThanOrEqual)
//      정확히 1개씩 · 그 순서 · 상수 true 반환 0 · 미정의 규칙 = false 로 종료.
//   하나라도 어긋나면 거부 → 스크립트가 출하 산출물 전량 폐기(Remove-ReleaseOutputs). 판독 실패 = 거부(fail-closed).
//
//   ④ [v12.18.2-GM · 방향 A] 감사 원장 결선(CheckAuditWiring · 힙 0 · requireAuditLedger 일 때): MS2E.Infrastructure.ITightenAuditSink 정의 1개 ·
//      TightenOnlyUiSettingWriter.TryWrite IL — ITightenAuditSink.Append 호출 정확히 1 · Judge 뒤 · ITightenSettingStore.WriteStored(정확히 1) 앞 ·
//      Append 직후 조건 분기(기록 실패 = 기입 0) · AdminSettingWidgetAdapter.TightenSetting — TryWrite 호출 정확히 1 · 감사 인자(직전 비 nop) ≠ ldnull.
//      ShippedBinaryLiveArmedGuard ⑪과 같은 규칙의 독립 구현(문자열 0 · 스택 해독기) — 가드는 형식이 없으면 ⑪을 생략하지만 여기서는 부재 = 거부.
//
//  δ=0 범위(정직): ③ CompareMatrix 는 힙 0(테스트 실측 — PEMemoryBlock/BlobReader 는 구조체 · 명령 버퍼 stackalloc).
//   ①의 JSON 파싱 · 행 문자열 분해와 ②의 팽창 버퍼 · PEReader 생성은 콜드 경로라 할당한다. 시간도 ②와 ③을 분리해 보고한다.
// ============================================================================
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace StockManager.Tools
{
    public enum TightenSealVerdict : byte
    {
        Ok = 0, ManifestInvalid = 1, AnchorMismatch = 2, ZipUnreadable = 3, EntryMissing = 4, EntryDuplicate = 5,
        ArtifactHashMismatch = 6, PeUnreadable = 7,                      // 3~7 = InterlockSealVerdict 와 같은 값(공용 로더)
        WriterMissing = 8, WriterAmbiguous = 9, CountMismatch = 10, SpecMismatch = 11, KeyMismatch = 12,
        RuleMismatch = 13, FallbackMismatch = 14, MinimumMismatch = 15, MaximumMismatch = 16, DirectionMismatch = 17,
        AuditLedgerMissing = 18,   // [v12.18.2-GM] ITightenAuditSink 정의 · TryWrite 의 Append 호출 · 진입점 없음
        AuditLedgerBypass = 19,    // [v12.18.2-GM] Append 중복 · 순서(Judge < Append < WriteStored) · 직후 분기 · 진입점 ldnull 이탈
    }

    /// <summary>매니페스트 행 1개 — 키 · 규칙 비트(1 TrueOnly · 2 NonDecreasing · 4 NonIncreasing) · 호스트 폴백 · 하한 · 상한.</summary>
    public readonly record struct TightenRuleRow(string Key, byte Rule, int Fallback, int Minimum, int Maximum);

    public sealed record TightenSealReport(TightenSealVerdict Verdict, string Detail, int Count, string? HostSha256, long InflateMicros, long CompareMicros);

    public static class ShippedTightenMatrixVerifier
    {
        public const string WriterNamespace = "MS2E.Infrastructure", WriterName = ShippedBinaryLiveArmedGuard.TightenWriterType;
        public const int MaxRows = 64, MaxIl = 512;

        // 단일/2바이트 옵코드 → 피연산자 바이트 수(0xFF = switch 가변). 콜드 1회 초기화(System.Reflection.Emit.OpCodes 반사).
        private const byte SwitchOperand = 0xFF;
        private static readonly byte[] Operand1 = new byte[0x100], Operand2 = new byte[0x100];
        private static readonly bool[] Known1 = new bool[0x100], Known2 = new bool[0x100];

        // 판독하는 옵코드(값 = ECMA-335 인코딩 · 2바이트는 0xFE00 | 둘째 바이트)
        private const ushort OpLdarg0 = 0x02, OpLdarg1 = 0x03, OpLdarg2 = 0x04, OpLdc0 = 0x16, OpLdc1 = 0x17, OpLdc2 = 0x18,
                             OpLdc4 = 0x1A, OpRet = 0x2A, OpAnd = 0x5F, OpConvI8 = 0x6A, OpLdstr = 0x72, OpStsfld = 0x80, OpLdsflda = 0x7F,
                             OpLdtoken = 0xD0, OpCeq = 0xFE01, OpCgt = 0xFE02, OpClt = 0xFE04, OpNop = 0x00, OpLdnull = 0x14, OpCall = 0x28, OpCallvirt = 0x6F;
        public const string AuditSinkName = ShippedBinaryLiveArmedGuard.TightenAuditSinkType, AdapterName = "AdminSettingWidgetAdapter";

        static ShippedTightenMatrixVerifier()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                byte size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => SwitchOperand,
                    _ => 4,
                };
                ushort v = unchecked((ushort)op.Value);
                if (v < 0x100) { Operand1[v] = size; Known1[v] = true; }
                else if ((v & 0xFF00) == 0xFE00) { Operand2[v & 0xFF] = size; Known2[v & 0xFF] = true; }
            }
        }

        /// <summary>"키|규칙|폴백|하한|상한" → 행. 규칙 ∉ {1,2,4} · 비정수 · 빈 키 · 열 수 이탈 = false.</summary>
        public static bool TryParseRow(string text, out TightenRuleRow row)
        {
            row = default;
            var parts = (text ?? "").Split('|');
            if (parts.Length != 5 || parts[0].Length == 0) return false;
            foreach (char c in parts[0]) if (!(char.IsAsciiLetterOrDigit(c) || c == '.')) return false;
            if (!byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte rule) || rule is not (1 or 2 or 4)) return false;
            if (!int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int fb)
                || !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lo)
                || !int.TryParse(parts[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int hi)) return false;
            row = new TightenRuleRow(parts[0], rule, fb, lo, hi);
            return true;
        }

        /// <summary>출하 zip ↔ 매니페스트(json) tightenRules ↔ 가드 고정 명세 ↔ 외부 앵커(master-seal.txt 내용) 대조.</summary>
        public static TightenSealReport Verify(string zipPath, string manifestJson, string anchorText, int expectedKeys, bool requireAuditLedger = false)
        {
            TightenSealReport R(TightenSealVerdict v, string d, int n = 0, string? sha = null, long inf = 0, long cmp = 0) => new(v, d, n, sha, inf, cmp);

            // ① 매니페스트 · 앵커 · 행렬 형식 · 명세
            if (!MultiRepoReleaseManifest.TryParse(manifestJson ?? "", out var m, out string stored, out string why)) return R(TightenSealVerdict.ManifestInvalid, why);
            if (!string.Equals(stored, MultiRepoReleaseManifest.CanonicalHash(m), StringComparison.OrdinalIgnoreCase))
                return R(TightenSealVerdict.ManifestInvalid, "manifestHash ≠ 정규화 재계산(매니페스트 변조)");
            if (m.LiveArmedDefault) return R(TightenSealVerdict.ManifestInvalid, "liveArmedDefault=true — Fail-Closed 불변식 위반");
            string anchor = (anchorText ?? "").Trim();
            if (!MS2E.Infrastructure.BootIntegrityGate.IsStrictSealHex(anchor) || !string.Equals(anchor, m.MasterSeal, StringComparison.OrdinalIgnoreCase))
                return R(TightenSealVerdict.AnchorMismatch, "master-seal.txt ≠ 매니페스트 masterSeal(또는 64 hex 아님)");
            if (expectedKeys < 1 || expectedKeys > MaxRows || expectedKeys != ShippedBinaryLiveArmedGuard.TightenSpecCount)
                return R(TightenSealVerdict.CountMismatch, $"기대 강화 전용 키 {expectedKeys} ≠ 가드 고정 명세 {ShippedBinaryLiveArmedGuard.TightenSpecCount}");
            if (m.TightenRules is null) return R(TightenSealVerdict.CountMismatch, "매니페스트 tightenRules 없음 — 강화 전용 행렬 미기록(기입기 누락 출하)");
            if (m.TightenRules.Count != expectedKeys) return R(TightenSealVerdict.CountMismatch, $"매니페스트 강화 전용 키 {m.TightenRules.Count} ≠ 기대 {expectedKeys}");
            var rows = new TightenRuleRow[m.TightenRules.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                if (!TryParseRow(m.TightenRules[i], out rows[i])) return R(TightenSealVerdict.ManifestInvalid, $"tightenRules #{i} 형식 이탈: {m.TightenRules[i]}");
                if (!ShippedBinaryLiveArmedGuard.TryTightenSpec(rows[i].Key, out byte specRule) || specRule != rows[i].Rule)
                    return R(TightenSealVerdict.SpecMismatch, $"tightenRules #{i} '{rows[i].Key}' 규칙 {rows[i].Rule} ≠ 가드 고정 명세 — 명세 밖 키 또는 허용 방향 변조");
                for (int j = 0; j < i; j++)
                    if (rows[j].Key == rows[i].Key) return R(TightenSealVerdict.ManifestInvalid, $"tightenRules 키 중복: {rows[i].Key}");
            }

            // ② MMF 뷰 · 항목 정규화/중복 가드 · 호스트 DLL 팽창 + 봉인 해시(공용 로더)
            var lv = ShippedInterlockManifestVerifier.LoadSealedHost(zipPath, m, out byte[] image, out string? sha, out long inflateUs, out string lwhy);
            if (lv != InterlockSealVerdict.Ok) return R((TightenSealVerdict)(byte)lv, lwhy, sha: sha, inf: inflateUs);

            // ③ 행렬 대조(복사 없는 PE 뷰 · 힙 0)
            long t1 = Stopwatch.GetTimestamp();
            TightenSealVerdict v; int at;
            try
            {
                using var pe = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(image));
                if (!pe.HasMetadata) return R(TightenSealVerdict.PeUnreadable, "메타데이터 없음", sha: sha, inf: inflateUs);
                var md = pe.GetMetadataReader();
                v = CompareMatrix(pe, md, rows, out at);
                if (v == TightenSealVerdict.Ok && requireAuditLedger) v = CheckAuditWiring(pe, md);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                return R(TightenSealVerdict.PeUnreadable, "PE/메타데이터 판독 실패: " + ex.GetType().Name, sha: sha, inf: inflateUs);
            }
            long compareUs = ShippedInterlockManifestVerifier.Micros(Stopwatch.GetTimestamp() - t1);
            string Row(int i) => i >= 0 && i < rows.Length ? $"#{i} '{rows[i].Key}'" : $"#{i}";
            string detail = v switch
            {
                TightenSealVerdict.Ok => $"강화 전용 키 {rows.Length} · 키 순서 · 규칙(==1 / ≥ / ≤) · 폴백 · 하한 · 상한 = 매니페스트 = 명세 · 호스트 DLL = 봉인 해시" + (requireAuditLedger ? " · 감사 원장 선기록 결선(⑪)" : ""),
                TightenSealVerdict.WriterMissing => WriterNamespace + "." + WriterName + " 정의 또는 .cctor/get_Rules/IsTightening 본문 없음",
                TightenSealVerdict.WriterAmbiguous => WriterNamespace + "." + WriterName + " 정의/본문 2개 이상",
                TightenSealVerdict.CountMismatch => $"바이너리 키 표 · 정적 데이터 길이 ≠ 매니페스트 {rows.Length}",
                TightenSealVerdict.KeyMismatch => Row(at) + " 바이너리 키 ≠ 매니페스트",
                TightenSealVerdict.RuleMismatch => Row(at) + " 바이너리 규칙 비트 ≠ 매니페스트",
                TightenSealVerdict.FallbackMismatch => Row(at) + " 바이너리 폴백 ≠ 매니페스트",
                TightenSealVerdict.MinimumMismatch => Row(at) + " 바이너리 하한 ≠ 매니페스트",
                TightenSealVerdict.MaximumMismatch => Row(at) + " 바이너리 상한 ≠ 매니페스트",
                TightenSealVerdict.DirectionMismatch => "IsTightening 비교 방향/규칙 비트 대응 이탈(==1 · ≥ · ≤ 정확히 1개씩 · 상수 true 0 · 미정의 = false)",
                TightenSealVerdict.AuditLedgerMissing => "감사 원장 결선 없음(ITightenAuditSink 정의 · TryWrite Append 호출 · TightenSetting 진입점)",
                TightenSealVerdict.AuditLedgerBypass => "감사 원장 우회(Append 정확히 1 · Judge < Append < WriteStored · 직후 조건 분기 · 진입점 감사 인자 ≠ null)",
                _ => v.ToString(),
            };
            return R(v, detail, rows.Length, sha, inflateUs, compareUs);
        }

        /// <summary>
        /// ③ 메타데이터/IL ↔ 매니페스트 행렬 대조(힙 0). at = 첫 불일치 행(행 무관 판정은 -1).
        /// </summary>
        public static TightenSealVerdict CompareMatrix(PEReader pe, MetadataReader md, ReadOnlySpan<TightenRuleRow> rows, out int at)
        {
            at = -1;
            if (rows.Length == 0 || rows.Length > MaxRows) return TightenSealVerdict.CountMismatch;
            TypeDefinitionHandle found = default;
            int defs = 0;
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                if (!md.StringComparer.Equals(td.Name, WriterName) || !md.StringComparer.Equals(td.Namespace, WriterNamespace)) continue;
                found = th; defs++;
            }
            if (defs == 0) return TightenSealVerdict.WriterMissing;
            if (defs > 1) return TightenSealVerdict.WriterAmbiguous;

            int cctorRva = 0, rulesRva = 0, tightRva = 0, nC = 0, nR = 0, nT = 0;
            foreach (var mh in md.GetTypeDefinition(found).GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (md.StringComparer.Equals(m.Name, ".cctor")) { cctorRva = m.RelativeVirtualAddress; nC++; }
                else if (md.StringComparer.Equals(m.Name, "get_Rules")) { rulesRva = m.RelativeVirtualAddress; nR++; }
                else if (md.StringComparer.Equals(m.Name, "IsTightening")) { tightRva = m.RelativeVirtualAddress; nT++; }
            }
            if (nC > 1 || nR > 1 || nT > 1) return TightenSealVerdict.WriterAmbiguous;
            if (cctorRva == 0 || rulesRva == 0 || tightRva == 0) return TightenSealVerdict.WriterMissing;

            Span<ushort> ops = stackalloc ushort[MaxIl];
            Span<int> args = stackalloc int[MaxIl];

            // ── .cctor: 키 표(ldstr 순서) · 폴백/하한/상한(ldtoken F … stsfld 대상 필드) ──
            int n = Decode(pe, cctorRva, ops, args);
            if (n < 0) return TightenSealVerdict.WriterMissing;
            int keys = 0, lastToken = -1;
            bool fb = false, lo = false, hi = false;
            for (int k = 0; k < n; k++)
            {
                if (ops[k] == OpLdstr)
                {
                    if (keys == rows.Length) { at = keys; return TightenSealVerdict.CountMismatch; }
                    if (!UserStringEquals(pe, md, args[k], rows[keys].Key)) { at = keys; return TightenSealVerdict.KeyMismatch; }
                    keys++;
                }
                else if (ops[k] == OpLdtoken) lastToken = args[k];
                else if (ops[k] == OpStsfld && lastToken != -1)
                {
                    var target = MetadataTokens.EntityHandle(args[k]);
                    if (target.Kind != HandleKind.FieldDefinition) continue;
                    var name = md.GetFieldDefinition((FieldDefinitionHandle)target).Name;
                    int column = md.StringComparer.Equals(name, "Fallbacks") ? 0 : md.StringComparer.Equals(name, "Minimums") ? 1 : md.StringComparer.Equals(name, "Maximums") ? 2 : -1;
                    if (column < 0) continue;
                    if (column == 0 ? fb : column == 1 ? lo : hi) return TightenSealVerdict.WriterAmbiguous;
                    var v = CompareIntColumn(pe, md, lastToken, rows, column, out at);
                    if (v != TightenSealVerdict.Ok) return v;
                    if (column == 0) fb = true; else if (column == 1) lo = true; else hi = true;
                    lastToken = -1;
                }
            }
            if (keys != rows.Length) { at = keys; return TightenSealVerdict.CountMismatch; }
            if (!fb || !lo || !hi) return TightenSealVerdict.WriterMissing;

            // ── get_Rules: RVA 바이트 블롭 ──
            n = Decode(pe, rulesRva, ops, args);
            if (n < 0) return TightenSealVerdict.WriterMissing;
            int ruleField = -1;
            for (int k = 0; k < n && ruleField == -1; k++)
                if (ops[k] == OpLdsflda || ops[k] == OpLdtoken) ruleField = args[k];
            if (ruleField == -1 || !TryRvaBlock(pe, md, ruleField, 1, out var rulesBlock, out int ruleBytes)) return TightenSealVerdict.WriterMissing;
            if (ruleBytes != rows.Length) return TightenSealVerdict.CountMismatch;
            var rr = rulesBlock.GetReader(0, ruleBytes);
            for (int i = 0; i < rows.Length; i++)
                if (rr.ReadByte() != rows[i].Rule) { at = i; return TightenSealVerdict.RuleMismatch; }

            // ── IsTightening: 규칙 비트 ↔ 비교 방향 ──
            n = Decode(pe, tightRva, ops, args);
            if (n < 0) return TightenSealVerdict.WriterMissing;
            return CheckDirection(ops[..n]);
        }

        /// <summary>
        /// ④ 감사 원장 결선 판정(힙 0 — 이름 비교 MetadataStringComparer · 명령 버퍼 stackalloc).
        /// </summary>
        public static TightenSealVerdict CheckAuditWiring(PEReader pe, MetadataReader md)
        {
            int sinks = 0, tryWriteRva = 0, entryRva = 0, nTry = 0, nEntry = 0;
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                if (!md.StringComparer.Equals(td.Namespace, WriterNamespace)) continue;
                if (md.StringComparer.Equals(td.Name, AuditSinkName)) { sinks++; continue; }
                bool writer = md.StringComparer.Equals(td.Name, WriterName), adapter = !writer && md.StringComparer.Equals(td.Name, AdapterName);
                if (!writer && !adapter) continue;
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (writer && md.StringComparer.Equals(m.Name, "TryWrite")) { tryWriteRva = m.RelativeVirtualAddress; nTry++; }
                    else if (adapter && md.StringComparer.Equals(m.Name, "TightenSetting")) { entryRva = m.RelativeVirtualAddress; nEntry++; }
                }
            }
            if (sinks == 0 || nTry == 0 || nEntry == 0 || tryWriteRva == 0 || entryRva == 0) return TightenSealVerdict.AuditLedgerMissing;
            if (sinks > 1 || nTry > 1 || nEntry > 1) return TightenSealVerdict.WriterAmbiguous;

            Span<ushort> ops = stackalloc ushort[MaxIl];
            Span<int> args = stackalloc int[MaxIl];
            int n = Decode(pe, tryWriteRva, ops, args);
            if (n < 0) return TightenSealVerdict.AuditLedgerMissing;
            int judge = -1, append = -1, appends = 0, write = -1, writes = 0;
            for (int k = 0; k < n; k++)
            {
                if (ops[k] != OpCall && ops[k] != OpCallvirt) continue;
                if (judge < 0 && IsMethod(md, args[k], WriterName, "Judge")) judge = k;
                else if (IsMethod(md, args[k], AuditSinkName, "Append")) { appends++; append = k; }
                else if (IsMethod(md, args[k], "ITightenSettingStore", "WriteStored")) { writes++; write = k; }
            }
            if (appends == 0) return TightenSealVerdict.AuditLedgerMissing;
            if (appends != 1 || writes != 1 || judge < 0 || !(judge < append && append < write) || append + 1 >= n || !IsCondBranch(ops[append + 1]))
                return TightenSealVerdict.AuditLedgerBypass;

            n = Decode(pe, entryRva, ops, args);
            if (n < 0) return TightenSealVerdict.AuditLedgerMissing;
            int calls = 0;
            for (int k = 0; k < n; k++)
            {
                if ((ops[k] != OpCall && ops[k] != OpCallvirt) || !IsMethod(md, args[k], WriterName, "TryWrite")) continue;
                calls++;
                int a = k - 1;
                while (a > 0 && ops[a] == OpNop) a--;
                if (a < 0 || ops[a] == OpLdnull) return TightenSealVerdict.AuditLedgerBypass;
            }
            return calls == 1 ? TightenSealVerdict.Ok : calls == 0 ? TightenSealVerdict.AuditLedgerMissing : TightenSealVerdict.AuditLedgerBypass;
        }

        // 조건 분기(brfalse/brtrue/beq…blt.un — 짧은 형식 0x2C~0x37 · 긴 형식 0x39~0x44)
        private static bool IsCondBranch(ushort op) => op is >= 0x2C and <= 0x37 or >= 0x39 and <= 0x44;

        /// <summary>호출 토큰 → (선언 형식 이름, 메서드 이름) 일치(MethodDefinition · TypeDef/TypeRef 부모 MemberReference · 문자열 생성 0).</summary>
        private static bool IsMethod(MetadataReader md, int token, string type, string name)
        {
            var h = MetadataTokens.EntityHandle(token);
            if (h.Kind == HandleKind.MethodDefinition)
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return md.StringComparer.Equals(m.Name, name) && md.StringComparer.Equals(md.GetTypeDefinition(m.GetDeclaringType()).Name, type);
            }
            if (h.Kind != HandleKind.MemberReference) return false;
            var r = md.GetMemberReference((MemberReferenceHandle)h);
            if (!md.StringComparer.Equals(r.Name, name)) return false;
            return r.Parent.Kind switch
            {
                HandleKind.TypeDefinition => md.StringComparer.Equals(md.GetTypeDefinition((TypeDefinitionHandle)r.Parent).Name, type),
                HandleKind.TypeReference => md.StringComparer.Equals(md.GetTypeReference((TypeReferenceHandle)r.Parent).Name, type),
                _ => false,
            };
        }

        /// <summary>
        /// IsTightening 본문 판정 — 규칙 비트 판독 3개 · 비교 3개가 정확히 1개씩 · TrueOnly(==1) → NonDecreasing(≥) → NonIncreasing(≤) 순서 ·
        /// 상수 true 반환(ldc.i4.1; ret) 0 · 역방향 적재(ldarg.1; ldarg.2) 0 · 끝 = ldc.i4.0; ret(미정의 규칙 = false).
        /// </summary>
        public static TightenSealVerdict CheckDirection(ReadOnlySpan<ushort> il)
        {
            ReadOnlySpan<ushort> bit1 = stackalloc ushort[] { OpLdarg0, OpLdc1, OpAnd }, bit2 = stackalloc ushort[] { OpLdarg0, OpLdc2, OpAnd },
                                 bit4 = stackalloc ushort[] { OpLdarg0, OpLdc4, OpAnd }, eq1 = stackalloc ushort[] { OpLdarg2, OpLdc1, OpConvI8, OpCeq },
                                 ge = stackalloc ushort[] { OpLdarg2, OpLdarg1, OpClt, OpLdc0, OpCeq }, le = stackalloc ushort[] { OpLdarg2, OpLdarg1, OpCgt, OpLdc0, OpCeq };
            int b1 = Once(il, bit1), q1 = Once(il, eq1), b2 = Once(il, bit2), g = Once(il, ge), b4 = Once(il, bit4), l = Once(il, le);
            if (b1 < 0 || q1 < 0 || b2 < 0 || g < 0 || b4 < 0 || l < 0) return TightenSealVerdict.DirectionMismatch;
            if (!(b1 < q1 && q1 < b2 && b2 < g && g < b4 && b4 < l)) return TightenSealVerdict.DirectionMismatch;
            for (int k = 0; k + 1 < il.Length; k++)
            {
                if (il[k] == OpLdc1 && il[k + 1] == OpRet) return TightenSealVerdict.DirectionMismatch;
                if (il[k] == OpLdarg1 && il[k + 1] == OpLdarg2) return TightenSealVerdict.DirectionMismatch;
            }
            if (il.Length < 2 || il[^2] != OpLdc0 || il[^1] != OpRet) return TightenSealVerdict.DirectionMismatch;
            return TightenSealVerdict.Ok;
        }

        // 패턴이 정확히 1번 나오면 위치 · 0번 = -1 · 2번 이상 = -2
        private static int Once(ReadOnlySpan<ushort> il, ReadOnlySpan<ushort> pattern)
        {
            int first = il.IndexOf(pattern);
            if (first < 0) return -1;
            return il[(first + 1)..].IndexOf(pattern) >= 0 ? -2 : first;
        }

        private static TightenSealVerdict CompareIntColumn(PEReader pe, MetadataReader md, int fieldToken, ReadOnlySpan<TightenRuleRow> rows, int column, out int at)
        {
            at = -1;
            if (!TryRvaBlock(pe, md, fieldToken, 4, out var block, out int size)) return TightenSealVerdict.WriterMissing;
            if (size != rows.Length * 4) return TightenSealVerdict.CountMismatch;
            var r = block.GetReader(0, size);
            for (int i = 0; i < rows.Length; i++)
            {
                int value = r.ReadInt32();
                int expect = column == 0 ? rows[i].Fallback : column == 1 ? rows[i].Minimum : rows[i].Maximum;
                if (value != expect)
                {
                    at = i;
                    return column == 0 ? TightenSealVerdict.FallbackMismatch : column == 1 ? TightenSealVerdict.MinimumMismatch : TightenSealVerdict.MaximumMismatch;
                }
            }
            return TightenSealVerdict.Ok;
        }

        /// <summary>필드 토큰 → RVA 정적 데이터 블록 · 크기(필드 형식 ClassLayout 또는 원시형 크기). elem 배수 아님 · RVA 0 = false.</summary>
        private static bool TryRvaBlock(PEReader pe, MetadataReader md, int fieldToken, int elem, out PEMemoryBlock block, out int size)
        {
            block = default; size = 0;
            var h = MetadataTokens.EntityHandle(fieldToken);
            if (h.Kind != HandleKind.FieldDefinition) return false;
            var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
            int rva = f.GetRelativeVirtualAddress();
            if (rva == 0) return false;
            var sig = md.GetBlobReader(f.Signature);
            if (sig.ReadSignatureHeader().Kind != SignatureKind.Field) return false;
            var code = sig.ReadSignatureTypeCode();
            if (code == SignatureTypeCode.TypeHandle)
            {
                var th = sig.ReadTypeHandle();
                if (th.Kind != HandleKind.TypeDefinition) return false;
                var layout = md.GetTypeDefinition((TypeDefinitionHandle)th).GetLayout();
                size = layout.IsDefault ? 0 : layout.Size;
            }
            else size = code switch
            {
                SignatureTypeCode.Byte or SignatureTypeCode.SByte => 1,
                SignatureTypeCode.Int16 or SignatureTypeCode.UInt16 => 2,
                SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 => 4,
                SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 => 8,
                _ => 0,
            };
            if (size <= 0 || size % elem != 0) return false;
            block = pe.GetSectionData(rva);
            return block.Length >= size;
        }

        /// <summary>#US 힙 항목(압축 바이트 길이 · UTF-16LE · 종결 바이트)을 문자열 생성 없이 기대 키와 비교.</summary>
        private static bool UserStringEquals(PEReader pe, MetadataReader md, int token, string expected)
        {
            if ((token >> 24) != 0x70) return false;                                   // ldstr 피연산자 = #US 토큰(0x70)
            var meta = pe.GetMetadata();
            int start = md.GetHeapMetadataOffset(HeapIndex.UserString) + (token & 0x00FFFFFF);
            if (start >= meta.Length) return false;
            var r = meta.GetReader(start, meta.Length - start);
            int bytes = r.ReadCompressedInteger();
            if (bytes != expected.Length * 2 + 1 || r.RemainingBytes < bytes) return false;
            for (int i = 0; i < expected.Length; i++)
                if (r.ReadUInt16() != expected[i]) return false;
            return true;
        }

        /// <summary>
        /// 메서드 본문 해독(힙 0) — tiny/fat 헤더 → 코드 바이트 → (옵코드, 4바이트 피연산자) 열. 미지 옵코드 · 버퍼 초과 · 잘린 본문 = -1.
        /// 피연산자는 토큰/정수(4바이트)만 보존한다 — 이 판독에 필요한 건 ldstr · ldtoken · stsfld · ldsflda 토큰뿐.
        /// </summary>
        private static int Decode(PEReader pe, int rva, Span<ushort> ops, Span<int> args)
        {
            var block = pe.GetSectionData(rva);
            if (block.Length < 1) return -1;
            var head = block.GetReader();
            byte b0 = head.ReadByte();
            int codeStart, codeSize;
            if ((b0 & 0x3) == 0x2) { codeStart = 1; codeSize = b0 >> 2; }
            else if ((b0 & 0x3) == 0x3)
            {
                if (block.Length < 12) return -1;
                byte b1 = head.ReadByte();
                codeStart = (b1 >> 4) * 4;
                head.ReadUInt16();                                                     // max stack
                codeSize = head.ReadInt32();
                if (codeStart < 12) return -1;
            }
            else return -1;
            if (codeSize < 0 || codeStart + codeSize > block.Length) return -1;
            var r = block.GetReader(codeStart, codeSize);
            int n = 0;
            while (r.RemainingBytes > 0)
            {
                if (n == ops.Length) return -1;
                byte b = r.ReadByte();
                ushort op; byte size;
                if (b == 0xFE)
                {
                    if (r.RemainingBytes < 1) return -1;
                    byte b2 = r.ReadByte();
                    if (!Known2[b2]) return -1;
                    op = (ushort)(0xFE00 | b2); size = Operand2[b2];
                }
                else
                {
                    if (!Known1[b]) return -1;
                    op = b; size = Operand1[b];
                }
                int arg = 0;
                if (size == SwitchOperand)
                {
                    if (r.RemainingBytes < 4) return -1;
                    int count = r.ReadInt32();
                    if (count < 0 || (long)count * 4 > r.RemainingBytes) return -1;
                    r.Offset += count * 4;
                }
                else if (size > 0)
                {
                    if (r.RemainingBytes < size) return -1;
                    if (size == 4) arg = r.ReadInt32();
                    else if (size == 1) arg = r.ReadByte();
                    else r.Offset += size;
                }
                ops[n] = op; args[n] = arg; n++;
            }
            return n;
        }
    }
}

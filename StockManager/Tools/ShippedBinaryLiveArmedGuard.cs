// ============================================================================
// ShippedBinaryLiveArmedGuard — [출하 · v12.16.1-GM · 방향 B] 출하 zip 안 호스트 바이너리의 liveArmed 기본값 '바이너리 레벨' 대조
//
//  기존 [7/10] IL 가드(GoldenMasterFreezeTests 파이프라인 모드)는 테스트 프로세스가 '로드한' 어셈블리를 리플렉션으로 검사하고 해시로
//  출하본과 결속한다. 이 검증기는 그와 독립된 두 번째 경로다: 출하 zip 의 항목 스트림을 메모리로만 읽어(디스크 추출 0 · 어셈블리 로드 0 ·
//  코드 실행 0) System.Reflection.Metadata 로 메서드 본문 IL 을 직접 판독한다.
//   범위: 어셈블리 전체 메서드(기존 [7] 가드는 BlazorHomeForm 트리만 — 이 판독은 RuntimeConfigSnapshot 등 다른 조회 지점까지 포함).
//   조회 지점: 키 ldstr 이후 창(16 명령) 안에서 다른 ldstr · stelem · ret · throw 를 만나기 전에 call/callvirt *.GetBool 이 나오는 곳
//     (키 이름 배열 초기화 ldstr→stelem.ref 같은 '조회 아닌 사용'은 지점이 아니다).
//   판정: 지점마다 ldstr key → ldc.i4.0(리터럴 false) → GetBool 바로 이어짐이어야 한다. 그 외(ldc.i4.1 · 변수/식/메서드 경유) = 위반.
//   비공허성: 두 키 각각 지점 ≥1 — 하나라도 0 = 위반(구조 변경으로 가드 대상 소실 · 판독 불가 = 거부).
//  [v12.17.1-GM · 방향 A 확장] ★마스터 플래그 판독 — 위 키 조회만 보던 판독기는 실 마스터 플래그(ShadowMetricsBridge(..., liveArmed: false) ·
//   BlazorHomeForm)를 보지 않았다(소스 가드 [6/10]도 GetBool 만 대조). 리터럴 true 로 바뀌어도 출하 통과 = 실결함. 추가 판정:
//   ① newobj 대상 생성자가 bool 형식 'liveArmed'/'isLiveArmed' 매개변수(Func<bool> 술어 제외)를 가지면 그 인자는 리터럴 false(ldc.i4.0)여야 한다 — 매개변수는 마지막
//      위치여야 직전 명령으로 판독 가능(아니면 판독 불가 = 위반 · 관례 강제). 생성 지점 수 = MasterSites(≥1 = 비공허성).
//   ② 일반 call/callvirt 대상 메서드의 마지막 'liveArmed' 인자가 리터럴 true(ldc.i4.1) = 위반(검증기 입력처럼 런타임 값 전달은 허용).
//   ③ get_LiveArmed/get_IsLiveArmed 본문이 'ldc.i4.1; ret'(항상 true) = 위반.
//  콜드 경로(출하 검증 1회) — 할당 무관. BCL 만 사용(System.Reflection.Metadata · System.Reflection.Emit.OpCodes 표).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace StockManager.Tools
{
    public static class ShippedBinaryLiveArmedGuard
    {
        public const string HostEntry = "host/MS2E.StockSuite.dll";
        private static readonly string[] Keys = { "Dispatch.ValveWired", "Dispatch.LiveArmingWired" };

        public readonly record struct Result(int Sites, IReadOnlyList<string> Violations, int MasterSites = 0)
        {
            public bool Ok => Sites > 0 && MasterSites > 0 && Violations.Count == 0;
        }

        private static readonly OpCode[] One = new OpCode[0x100], Two = new OpCode[0x100];
        static ShippedBinaryLiveArmedGuard()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                ushort v = unchecked((ushort)op.Value);
                if (v < 0x100) One[v] = op; else if ((v & 0xFF00) == 0xFE00) Two[v & 0xFF] = op;
            }
        }

        /// <summary>출하 zip 의 항목(기본 = 호스트 DLL)을 메모리로만 읽어 판독. 항목 없음 · zip 손상 = 거부(Ok=false).</summary>
        public static Result ScanZip(string zipPath, string entryName = HostEntry)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                // ★PS 5.1 Compress-Archive 는 항목 이름을 '\' 로 기록(host\MS2E.StockSuite.dll) — ZipSealDryRunVerifier 와 같은 정규화로 대조.
                //   정규화 후 같은 이름 2개 이상 = 어느 쪽이 실행될지 모호 → 거부.
                ZipArchiveEntry? entry = null;
                foreach (var e in zip.Entries)
                {
                    if (!string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase)) continue;   // Windows 대소문자 무시 — 대소문자 변형도 같은 파일(모호 = 중복 거부)
                    if (entry is not null) return Fail("zip 항목 중복(정규화 후): " + entryName);
                    entry = e;
                }
                if (entry is null) return Fail("zip 항목 없음: " + entryName);
                using var src = entry.Open();
                var ms = new MemoryStream(checked((int)entry.Length));
                src.CopyTo(ms);
                ms.Position = 0;
                return Scan(ms);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                return Fail("zip 판독 실패: " + ex.GetType().Name);
            }
        }

        // 판독 불가 = 조회 지점 0 + 사유 = 거부(fail-closed — '못 읽었다'를 '위반 없음'으로 취급하지 않는다)
        private static Result Fail(string why) => new Result(0, new[] { why });

        /// <summary>PE 이미지(스트림 · 로드 없음) 판독. 손상 PE · 비정상 IL = 거부.</summary>
        public static Result Scan(Stream peImage)
        {
            try { return ScanCore(peImage); }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
                return Fail("PE/IL 판독 실패: " + ex.GetType().Name);
            }
        }

        private static Result ScanCore(Stream peImage)
        {
            using var pe = new PEReader(peImage, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage);
            var md = pe.GetMetadataReader();
            int sites = 0, masterSites = 0;
            var perKey = new int[Keys.Length];
            var violations = new List<string>();
            var armedParam = LiveArmedParameterIndex(md);                       // 메서드 행 번호 → (liveArmed 매개변수 위치, 매개변수 수)
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                byte[]? il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                if (il is null) continue;
                var ins = Decode(il);
                string mname = md.GetString(m.Name);
                if ((mname == "get_LiveArmed" || mname == "get_IsLiveArmed") && ins.Count == 2 && ins[0].Op == OpCodes.Ldc_I4_1 && ins[1].Op == OpCodes.Ret)
                    violations.Add(TypeName(md, m) + "." + mname + ": 항상 true 반환(ldc.i4.1; ret)");
                for (int k = 0; k < ins.Count; k++)
                {
                    var op = ins[k].Op;
                    if ((op == OpCodes.Newobj || op == OpCodes.Call || op == OpCodes.Callvirt)
                        && MetadataTokens.EntityHandle(ins[k].Operand) is { Kind: HandleKind.MethodDefinition } th
                        && armedParam.TryGetValue(MetadataTokens.GetRowNumber(th), out var ap))
                    {
                        string target = TypeName(md, md.GetMethodDefinition((MethodDefinitionHandle)th)) + "." + md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)th).Name);
                        bool last = ap.Index == ap.Count;
                        var prev = k > 0 ? ins[k - 1].Op : OpCodes.Nop;
                        if (op == OpCodes.Newobj)
                        {
                            masterSites++;
                            if (!last) violations.Add(TypeName(md, m) + "." + mname + ": " + target + " liveArmed 매개변수가 마지막이 아님 — 정적 판독 불가(관례 위반)");
                            else if (prev != OpCodes.Ldc_I4_0) violations.Add(TypeName(md, m) + "." + mname + ": " + target + " 마스터 플래그 liveArmed 인자가 리터럴 false 가 아님(직전 옵코드 " + prev.Name + ")");
                        }
                        else if (last && prev == OpCodes.Ldc_I4_1)
                            violations.Add(TypeName(md, m) + "." + mname + ": " + target + " liveArmed 인자 리터럴 true");
                    }
                    if (op != OpCodes.Ldstr) continue;
                    string s = md.GetUserString(MetadataTokens.UserStringHandle(ins[k].Operand & 0x00FFFFFF));
                    int key = Array.IndexOf(Keys, s);
                    if (key < 0) continue;
                    int call = ConsumingGetBool(md, ins, k);
                    if (call < 0) continue;                                     // 조회 아닌 사용(배열 초기화 · 비교 등)
                    sites++;
                    perKey[key]++;
                    if (!(call == k + 2 && ins[k + 1].Op == OpCodes.Ldc_I4_0))
                        violations.Add(TypeName(md, m) + "." + mname + ": \"" + s + "\" 기본값이 리터럴 false 가 아님(다음 옵코드 " + ins[k + 1].Op.Name + ")");
                }
            }
            for (int i = 0; i < Keys.Length; i++)
                if (perKey[i] == 0) violations.Add("\"" + Keys[i] + "\" GetBool 조회 지점 0 — 가드 대상 소실(비공허성 실패)");
            if (masterSites == 0) violations.Add("마스터 플래그(liveArmed 생성자 인자) 생성 지점 0 — 가드 대상 소실(비공허성 실패)");
            return new Result(sites, violations, masterSites);
        }

        private static string TypeName(MetadataReader md, MethodDefinition m) => md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name);

        /// <summary>시그니처 형식 → 'bool 인가'만 판독하는 최소 디코더(System.Reflection.Metadata · BCL).</summary>
        private sealed class IsBoolProvider : ISignatureTypeProvider<bool, object?>
        {
            public static readonly IsBoolProvider Instance = new();
            public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode == PrimitiveTypeCode.Boolean;
            public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;
            public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => false;
            public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => false;
            public bool GetSZArrayType(bool elementType) => false;
            public bool GetArrayType(bool elementType, ArrayShape shape) => false;
            public bool GetByReferenceType(bool elementType) => false;
            public bool GetPointerType(bool elementType) => false;
            public bool GetGenericInstantiation(bool genericType, System.Collections.Immutable.ImmutableArray<bool> typeArguments) => false;
            public bool GetGenericMethodParameter(object? genericContext, int index) => false;
            public bool GetGenericTypeParameter(object? genericContext, int index) => false;
            public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
            public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
            public bool GetPinnedType(bool elementType) => elementType;
        }

        /// <summary>'liveArmed'/'isLiveArmed' 매개변수를 가진 메서드 정의 → (1-기반 위치, 매개변수 수). 시그니처 블롭에서 매개변수 수만 판독.</summary>
        private static Dictionary<int, (int Index, int Count)> LiveArmedParameterIndex(MetadataReader md)
        {
            var map = new Dictionary<int, (int, int)>();
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                foreach (var ph in m.GetParameters())
                {
                    var p = md.GetParameter(ph);
                    if (p.SequenceNumber == 0) continue;                                 // 반환값 행
                    string n = md.GetString(p.Name);
                    if (!string.Equals(n, "liveArmed", StringComparison.OrdinalIgnoreCase) && !string.Equals(n, "isLiveArmed", StringComparison.OrdinalIgnoreCase)) continue;
                    // ★bool 형식 매개변수만(Func<bool> 술어 · 검증기 입력 등은 '플래그'가 아니다 — SecurityAssetVault/LiveArmedEgressSentinel)
                    var sig = m.DecodeSignature(IsBoolProvider.Instance, null);
                    int idx = p.SequenceNumber - 1;
                    if ((uint)idx >= (uint)sig.ParameterTypes.Length || !sig.ParameterTypes[idx]) break;
                    map[MetadataTokens.GetRowNumber(mh)] = (p.SequenceNumber, sig.ParameterTypes.Length);
                    break;
                }
            }
            return map;
        }

        private const int Window = 16;

        /// <summary>키 ldstr(k) 이후 창 안의 첫 GetBool 호출 인덱스(-1 = 조회 아님: 다른 ldstr · stelem · ret · throw 가 먼저 · 창 초과).</summary>
        private static int ConsumingGetBool(MetadataReader md, List<(OpCode Op, int Operand)> ins, int k)
        {
            for (int j = k + 1; j < ins.Count && j <= k + Window; j++)
            {
                var op = ins[j].Op;
                if ((op == OpCodes.Call || op == OpCodes.Callvirt) && MethodName(md, ins[j].Operand) == "GetBool") return j;
                if (op == OpCodes.Ldstr || op == OpCodes.Ret || op == OpCodes.Throw || op.Name!.StartsWith("stelem", StringComparison.Ordinal)) return -1;
            }
            return -1;
        }

        private static string? MethodName(MetadataReader md, int token)
        {
            var h = MetadataTokens.EntityHandle(token);
            return h.Kind switch
            {
                HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name),
                HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name),
                HandleKind.MethodSpecification => MethodName(md, MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method)),
                _ => null,
            };
        }

        private static List<(OpCode Op, int Operand)> Decode(byte[] il)
        {
            var list = new List<(OpCode, int)>();
            int i = 0;
            while (i < il.Length)
            {
                OpCode op = il[i] == 0xFE ? Two[il[++i]] : One[il[i]];
                if (op.Size == 0) throw new InvalidDataException("미정의 옵코드 0x" + il[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                i++;
                int size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4,
                };
                int operand = size == 4 ? BitConverter.ToInt32(il, i) : size == 1 ? il[i] : 0;
                list.Add((op, operand));
                i += size;
            }
            return list;
        }
    }
}

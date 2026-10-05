// ============================================================================
// ZeroAllocIlInspector — [Phase 1] Think Beyond 방향 B: Zero-Alloc Hot-Path Static Allocator Guard(IL 정적 검사기)
//
//   [ZeroAllocHotPath] 표지 메서드의 IL 바이트를 직접 해독(System.Reflection.Emit.OpCodes 표로 옵코드/피연산자 크기
//   결정)해 관리 힙 할당을 유발하는 지점을 찾는다:
//     · box                         — 값 타입 박싱(인터페이스/object 전달, 열거형 HasFlag 등 숨은 박싱 포함)
//     · newarr                      — 배열 할당(params 배열 암시적 생성 포함)
//     · newobj(참조 형식)           — 클래스 생성, 람다 캡처 클로저(<>c__DisplayClass), 델리게이트 생성
//     · call/callvirt 알려진 할당 API — string 생성(Concat/Format/Join/Substring/Replace/ToUpper…/ToString),
//                                     보간 문자열 ToStringAndClear, System.Linq.Enumerable.*, ToArray/ToList
//   허용: 값 형식 newobj(스택 생성), Exception 파생 newobj(throw 전용 실패 경로).
//   ★정직 한계: "직접 본문"만 본다(전이 호출 내부 할당은 GC 실측 테스트가 담당). 제네릭 공유 코드의 제약 호출
//   박싱 여부 등 JIT 최적화 결과는 IL 만으로 확정 불가 — 보수적으로 box 옵코드는 전부 위반으로 본다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace StockManager.Tests
{
    public readonly record struct IlAllocationSite(string Method, int IlOffset, string Kind, string Detail)
    {
        public override string ToString() => $"{Method} @IL_{IlOffset:X4}: {Kind} {Detail}";
    }

    public static class ZeroAllocIlInspector
    {
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];

        private static readonly HashSet<string> StringAllocatingMethods = new(StringComparer.Ordinal)
        {
            "Concat", "Format", "Join", "Substring", "Replace", "Insert", "Remove", "PadLeft", "PadRight",
            "ToUpper", "ToLower", "ToUpperInvariant", "ToLowerInvariant", "Trim", "TrimStart", "TrimEnd", "Split", "Create",
        };

        static ZeroAllocIlInspector()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                ushort v = unchecked((ushort)op.Value);
                if (v < 0x100) OneByte[v] = op;
                else if ((v & 0xFF00) == 0xFE00) TwoByte[v & 0xFF] = op;
            }
        }

        public static IReadOnlyList<IlAllocationSite> Inspect(MethodBase method)
        {
            var sites = new List<IlAllocationSite>();
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) return sites;

            Module module = method.Module;
            Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
            Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            string name = $"{method.DeclaringType?.Name}.{method.Name}";

            int i = 0;
            while (i < il.Length)
            {
                int offset = i;
                OpCode op = il[i] == 0xFE ? TwoByte[il[++i]] : OneByte[il[i]];
                i++;
                int operandStart = i;
                i += OperandSize(op, il, operandStart);

                if (op == OpCodes.Box)
                {
                    sites.Add(new(name, offset, "box", Describe(() => module.ResolveType(Token(il, operandStart), typeArgs, methodArgs))));
                }
                else if (op == OpCodes.Newarr)
                {
                    sites.Add(new(name, offset, "newarr", Describe(() => module.ResolveType(Token(il, operandStart), typeArgs, methodArgs))));
                }
                else if (op == OpCodes.Newobj)
                {
                    var ctor = SafeResolveMethod(module, Token(il, operandStart), typeArgs, methodArgs);
                    Type? t = ctor?.DeclaringType;
                    if (t is null) sites.Add(new(name, offset, "newobj", "<unresolved>"));
                    else if (!t.IsValueType && !typeof(Exception).IsAssignableFrom(t))
                        sites.Add(new(name, offset, typeof(Delegate).IsAssignableFrom(t) ? "newobj-delegate" : "newobj", t.FullName ?? t.Name));
                }
                else if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    var callee = SafeResolveMethod(module, Token(il, operandStart), typeArgs, methodArgs);
                    if (callee is not null && IsKnownAllocating(callee, out string why))
                        sites.Add(new(name, offset, "alloc-call", why));
                }
            }
            return sites;
        }

        private static bool IsKnownAllocating(MethodBase m, out string why)
        {
            Type? t = m.DeclaringType;
            why = $"{t?.Name}.{m.Name}";
            if (t == typeof(string) && StringAllocatingMethods.Contains(m.Name)) return true;
            if (m.Name == "ToString") return true;
            if (t == typeof(Enumerable)) return true;
            if (m.Name is "ToArray" or "ToList" or "ToStringAndClear") return true;
            return false;
        }

        private static MethodBase? SafeResolveMethod(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
        {
            try { return module.ResolveMethod(token, typeArgs, methodArgs); }
            catch (ArgumentException) { return null; }
        }

        private static string Describe(Func<Type> resolve)
        {
            try { return resolve().FullName ?? "?"; }
            catch (ArgumentException) { return "<unresolved>"; }
        }

        private static int Token(byte[] il, int at) => BitConverter.ToInt32(il, at);

        private static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
            _ => 4,   // InlineBrTarget/Field/I/Method/Sig/String/Tok/Type/ShortInlineR
        };

        /// <summary>어셈블리 내 [ZeroAllocHotPath] 표지 메서드 전체(선언 기준·비공개 포함).</summary>
        public static IReadOnlyList<MethodInfo> FindMarkedMethods(Assembly assembly, Type attributeType)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                   | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            return types.SelectMany(t => t.GetMethods(All)).Where(m => m.IsDefined(attributeType, inherit: false)).ToList();
        }
    }
}

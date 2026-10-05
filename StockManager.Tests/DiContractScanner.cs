// ============================================================================
// DiContractScanner — [Phase 3 §2] 멀티 호스트 DI "Orphan Inject" 교차 스캐너(테스트 전용)
//
//   · InjectPoints: 컴포넌트 형식의 [Inject] 프로퍼티(상속 포함 · 키드 주입 Key 포함) 전수.
//   · ReachableComponents: 루트 컴포넌트의 BuildRenderTree IL 에서 RenderTreeBuilder.OpenComponent<T> 제네릭 인자와
//     OpenComponent(int, Type) 의 ldtoken 대상을 따라가 "그 호스트가 실제로 렌더할 수 있는" 컴포넌트 폐포를 계산한다.
//     (모바일 호스트는 MobileStudioDeck 1개를 루트로 마운트 — 컴파일된 전체 컴포넌트가 아니라 도달 집합이 계약 대상.)
//   · MobileRegistrations: MAUI 진입점(MauiProgram.cs / MobileServiceRegistry.cs)은 net8.0-android/ios TFM 이라 테스트가
//     로드할 수 없다 — 원문에서 등록 서비스 형식을 파싱해 데스크톱 어셈블리 형식으로 사상한다(#if 분기는 전부 포함 =
//     어느 한 플랫폼에라도 등록되면 등록으로 본다 — Android/iOS 양쪽 모두 등록되는지는 별도 판정).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace StockManager.Tests;

public readonly record struct InjectPoint(Type Component, string Property, Type ServiceType, object? Key);

public static class DiContractScanner
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static IEnumerable<InjectPoint> InjectPoints(Type component)
    {
        for (Type? t = component; t is not null && t != typeof(ComponentBase) && t != typeof(object); t = t.BaseType)
            foreach (var p in t.GetProperties(All))
            {
                var inj = p.GetCustomAttribute<InjectAttribute>();
                if (inj is not null) yield return new(component, p.Name, p.PropertyType, inj.Key);
            }
    }

    public static IReadOnlyCollection<Type> ReachableComponents(Type root)
    {
        var seen = new HashSet<Type>();
        var work = new Stack<Type>();
        work.Push(root);
        while (work.Count > 0)
        {
            var t = work.Pop();
            if (!seen.Add(t)) continue;
            foreach (var child in ChildComponents(t))
                if (!seen.Contains(child)) work.Push(child);
        }
        return seen;
    }

    /// <summary>해당 컴포넌트가 자기 BuildRenderTree(및 RenderFragment 람다)에서 "직접" 여는 자식 컴포넌트(비전이).</summary>
    public static IEnumerable<Type> ChildComponents(Type component)
    {
        var result = new HashSet<Type>();
        void Scan(Type x)
        {
            foreach (var m in x.GetMethods(All).Cast<MethodBase>().Concat(x.GetConstructors(All)))
            {
                byte[]? il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch (InvalidOperationException) { il = null; }
                if (il is null) continue;
                foreach (var (op, token) in Tokens(il))
                {
                    try
                    {
                        if (op == OpCodes.Call || op == OpCodes.Callvirt)
                        {
                            var callee = m.Module.ResolveMethod(token, x.IsGenericType ? x.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null);
                            if (callee is MethodInfo { IsGenericMethod: true, Name: "OpenComponent" } mi)
                            {
                                var arg = mi.GetGenericArguments()[0];
                                if (!arg.IsGenericParameter) result.Add(arg.IsGenericType ? arg.GetGenericTypeDefinition() : arg);
                            }
                        }
                        else if (op == OpCodes.Ldtoken)
                        {
                            var mem = m.Module.ResolveMember(token);
                            if (mem is Type tt && typeof(IComponent).IsAssignableFrom(tt) && !tt.IsAbstract) result.Add(tt);
                        }
                    }
                    catch (ArgumentException) { }
                }
            }
            foreach (var n in x.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) Scan(n);
        }
        Scan(component);
        return result;
    }

    private static readonly OpCode[] One = new OpCode[0x100], Two = new OpCode[0x100];
    static DiContractScanner()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            ushort v = unchecked((ushort)op.Value);
            if (v < 0x100) One[v] = op; else if ((v & 0xFF00) == 0xFE00) Two[v & 0xFF] = op;
        }
    }

    private static IEnumerable<(OpCode Op, int Token)> Tokens(byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            OpCode op = il[i] == 0xFE ? Two[il[++i]] : One[il[i]];
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
            int token = op.OperandType is OperandType.InlineMethod or OperandType.InlineTok ? BitConverter.ToInt32(il, i) : 0;
            yield return (op, token);
            i += size;
        }
    }

    // ── 모바일 원문 파서 ─────────────────────────────────────────────────────
    private static readonly Regex GenericReg = new(@"\.Add(?:Singleton|Scoped|Transient)<\s*([\w\.]+)\s*(?:,\s*([\w\.]+)\s*)?>", RegexOptions.Compiled);
    private static readonly Regex FactoryNewReg = new(@"\.Add(?:Singleton|Scoped|Transient)\(\s*\w+\s*=>\s*new\s+([\w\.]+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex InstanceMemberReg = new(@"\.Add(?:Singleton|Scoped|Transient)\(\s*([\w\.]+\.[A-Z]\w*)\s*\)", RegexOptions.Compiled);

    /// <summary>원문 등록 → (서비스 형식명, 구현 형식명). 구현 미상(팩토리/인스턴스)은 구현=서비스.</summary>
    public static IEnumerable<(string Service, string Implementation)> ParseRegistrations(string source)
    {
        // 주석 줄 제거(// … 뒤 예시 등록이 파싱되지 않도록)
        var code = string.Join('\n', source.Split('\n').Select(l => { int c = l.IndexOf("//", StringComparison.Ordinal); return c >= 0 ? l[..c] : l; }));
        foreach (Match m in GenericReg.Matches(code))
            yield return (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        foreach (Match m in FactoryNewReg.Matches(code))
            yield return (m.Groups[1].Value, m.Groups[1].Value);
        foreach (Match m in InstanceMemberReg.Matches(code))
            yield return ("member:" + m.Groups[1].Value, "member:" + m.Groups[1].Value);
    }

    /// <summary>원문 형식명(전체/단순)을 어셈블리 형식으로 사상. 모바일 전용 형식(Android*/Maui* 등)은 null.</summary>
    public static Type? ResolveTypeName(string name, IReadOnlyList<Type> universe)
    {
        if (name.StartsWith("member:", StringComparison.Ordinal))
        {
            string path = name[7..];
            int dot = path.LastIndexOf('.');
            var owner = ResolveTypeName(path[..dot], universe);
            var member = owner?.GetMember(path[(dot + 1)..], BindingFlags.Public | BindingFlags.Static).FirstOrDefault();
            return member switch { PropertyInfo p => p.PropertyType, FieldInfo f => f.FieldType, _ => null };
        }
        var full = universe.FirstOrDefault(t => t.FullName == name);
        if (full is not null) return full;
        var simple = universe.Where(t => t.Name == name).ToList();
        return simple.Count == 1 ? simple[0] : null;
    }

    // ── StockManager: 컴포지션 루트 IL 등록 추출기(실행 0) ──────────────────────
    //   BlazorHomeForm 생성자는 등록과 동시에 백그라운드 서비스 ~20종(스레드·소켓·텔레메트리 서버)을 기동하므로 테스트에서
    //   실행할 수 없다. 대신 IL 의 제네릭 메서드 인스턴스(MethodSpec)에서 등록 형식을 "컴파일러가 확정한 그대로" 읽는다 —
    //   `services.AddSingleton(value)` 도 IL 에는 AddSingleton<TStatic>(…) 로 정적 형식이 박혀 있다.
    //   IServiceCollection 을 인자로 받는 자체 확장/헬퍼(비프레임워크)는 재귀 추적한다.
    //   ★한계(정직): 조건부 등록(if 분기 내부)도 "등록됨"으로 본다 — 정적 존재 ≠ 런타임 실행.
    public readonly record struct IlRegistration(Type Service, Type? Implementation, string Method);

    public static IReadOnlyList<IlRegistration> ExtractRegistrations(MethodBase root)
    {
        var result = new List<IlRegistration>();
        var seen = new HashSet<MethodBase>();
        void Walk(MethodBase m, int depth)
        {
            if (depth > 6 || !seen.Add(m)) return;
            byte[]? il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch (InvalidOperationException) { il = null; }
            if (il is null) return;
            foreach (var (op, token) in Tokens(il))
            {
                if (op != OpCodes.Call && op != OpCodes.Callvirt) continue;
                MethodBase? callee;
                try { callee = m.Module.ResolveMethod(token, m.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null); }
                catch (ArgumentException) { continue; }
                if (callee is not MethodInfo mi) continue;
                string? ns = mi.DeclaringType?.Namespace;
                bool framework = ns is not null && (ns.StartsWith("Microsoft.", StringComparison.Ordinal) || ns.StartsWith("System", StringComparison.Ordinal));
                var ps = mi.GetParameters();
                if (framework && mi.IsGenericMethod && (mi.Name.StartsWith("Add", StringComparison.Ordinal) || mi.Name.StartsWith("TryAdd", StringComparison.Ordinal))
                    && ps.Length >= 1 && ps[0].ParameterType == typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection))
                {
                    var ga = mi.GetGenericArguments();
                    Type? impl = ga.Length == 2 ? ga[1] : ps.Length == 1 ? ga[0] : null;   // 형식 기반 등록만 구현 형식 확정
                    result.Add(new(ga[0], impl, mi.Name));
                }
                else if (!framework && ps.Any(p => p.ParameterType == typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection)))
                {
                    Walk(mi, depth + 1);   // 자체 등록 헬퍼(예: RegisterXxx(services))
                }
            }
            if (m.DeclaringType is { } owner)
                foreach (var n in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => t.Name.Contains('<')))
                    foreach (var lm in n.GetMethods(All)) Walk(lm, depth + 1);   // 루트의 람다/로컬 함수 본문
        }
        Walk(root, 0);
        return result;
    }

    public static string FindRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("리포 루트(StockManager.sln) 탐색 실패");
        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }
}

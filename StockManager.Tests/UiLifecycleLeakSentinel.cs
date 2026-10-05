// ============================================================================
// UiLifecycleLeakSentinel — [Phase 3 §3 방향 A] Automated Reflection-Based UI Event Leak Sentinel
//
//   Razor 컴포넌트(IComponent 구현 형식)의 컴파일된 IL 을 직접 해독해 수명주기 누수를 정적으로 적발한다.
//   컴포넌트 1개 = 선언 메서드 + 컴파일러 생성 중첩 형식(람다 <>c·클로저 DisplayClass·async 상태기계) 전부.
//
//   규칙 ① 이벤트 구독: 어떤 메서드에서든 `add_E(handler)` 가 있으면, Dispose/DisposeAsync 에서 "전이적으로 도달 가능한"
//          메서드 집합 안에 같은 이벤트·같은 핸들러의 `remove_E(handler)` 가 있어야 한다.
//            · 핸들러 식별: 델리게이트 생성 직전의 ldftn(메서드) 또는 ldfld/ldsfld(델리게이트 필드 — 람다 캐시 <>9__ 제외).
//            · ★람다 함정 적발: `X += () => …; X -= () => …;` 는 IL 상 서로 다른 합성 메서드라 해제되지 않는다(실누수).
//            · IDisposable/IAsyncDisposable 미구현 컴포넌트는 도달 집합이 비어 전 구독이 위반.
//   규칙 ② 관리 핸들: CancellationTokenSource / PeriodicTimer / System.Threading.Timer / System.Timers.Timer 를
//          생성(newobj 또는 CreateLinkedTokenSource)해 "필드에 보관"하면, Dispose 도달 집합에 해당 형식의 Dispose(Async) 호출이
//          있어야 한다(지역 using 은 같은 메서드 안에서 처분되므로 대상 아님).
//
//   규칙 ③ async void: 컴포넌트(람다 포함)의 async void 메서드는 본문 전체가 catch-all 로 감싸이지 않으면 위반.
//
//   ★정직 한계: 정적 근사다. 같은 형식의 서로 다른 인스턴스(A.OnChange 와 B.OnChange)는 구분하지 않는다. 컴포넌트 자신이
//   소유·생성한 객체(수명이 컴포넌트와 같음)의 이벤트는 누수가 아니지만 IL 만으로는 판정할 수 없어 호출측 허용목록으로 처리한다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace StockManager.Tests;

public readonly record struct UiLeakFinding(string Component, string Rule, string Detail)
{
    public override string ToString() => $"{Component}: [{Rule}] {Detail}";
}

public static class UiLifecycleLeakSentinel
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                   | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    private static readonly HashSet<string> HandleTypes = new(StringComparer.Ordinal)
    {
        "System.Threading.CancellationTokenSource",
        "System.Threading.PeriodicTimer",
        "System.Threading.Timer",
        "System.Timers.Timer",
    };

    static UiLifecycleLeakSentinel()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            ushort v = unchecked((ushort)op.Value);
            if (v < 0x100) OneByte[v] = op;
            else if ((v & 0xFF00) == 0xFE00) TwoByte[v & 0xFF] = op;
        }
    }

    private readonly record struct Instr(int Offset, OpCode Op, int Token);

    private readonly record struct Subscription(string Event, string? Handler, string Site);

    /// <summary>어셈블리의 모든 Razor 컴포넌트 형식(추상·컴파일러 생성 제외).</summary>
    public static IReadOnlyList<Type> ComponentTypes(Assembly assembly, Type componentInterface)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        return types.Where(t => t.IsClass && !t.IsAbstract && componentInterface.IsAssignableFrom(t)
                                && !t.Name.Contains('<') && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                    .OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<UiLeakFinding> Inspect(Type component) => Inspect(component, out _);

    /// <param name="subscriptions">관측한 add_ 구독 지점 수(센티널이 실제로 IL 을 보고 있다는 계측 증거).</param>
    public static IReadOnlyList<UiLeakFinding> Inspect(Type component, out int subscriptions)
    {
        var findings = new List<UiLeakFinding>();
        var methods = CollectMethods(component);
        var bodies = new Dictionary<MethodBase, List<Instr>>();
        foreach (var m in methods) bodies[m] = Decode(m);

        // Dispose 도달 집합(컴포넌트 소유 메서드 안에서만 전이 추적)
        var owned = new HashSet<MethodBase>(methods);
        var roots = methods.Where(m => m.DeclaringType == component && m.Name is "Dispose" or "DisposeAsync"
                                        or "System.IDisposable.Dispose" or "System.IAsyncDisposable.DisposeAsync").ToList();
        // async DisposeAsync 는 상태기계 MoveNext 가 본문이다 — 해당 중첩 형식의 MoveNext 를 루트에 합류
        foreach (var r in roots.ToList())
        {
            var sm = r.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
            var mn = sm?.GetMethod("MoveNext", All);
            if (mn is not null && owned.Contains(mn)) roots.Add(mn);
        }
        var reachable = new HashSet<MethodBase>();
        var work = new Stack<MethodBase>(roots);
        while (work.Count > 0)
        {
            var m = work.Pop();
            if (!reachable.Add(m)) continue;
            foreach (var ins in bodies[m])
            {
                if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Ldftn) continue;
                var callee = Resolve(m, ins.Token);
                if (callee is null) continue;
                if (owned.Contains(callee)) work.Push(callee);
                var sm = callee.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
                var mn = sm?.GetMethod("MoveNext", All);
                if (mn is not null && owned.Contains(mn)) work.Push(mn);
            }
        }

        var adds = new List<Subscription>();
        var removes = new List<Subscription>();
        var storedHandles = new List<(string Type, string Site)>();
        var disposedHandleTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in methods)
        {
            var body = bodies[m];
            for (int k = 0; k < body.Count; k++)
            {
                var ins = body[k];
                if (ins.Op == OpCodes.Call || ins.Op == OpCodes.Callvirt)
                {
                    var callee = Resolve(m, ins.Token);
                    if (callee is null) continue;
                    string site = $"{Short(m)}@IL_{ins.Offset:X4}";
                    if (callee.IsSpecialName && (callee.Name.StartsWith("add_", StringComparison.Ordinal) || callee.Name.StartsWith("remove_", StringComparison.Ordinal))
                        && callee.DeclaringType is { } et && IsEvent(et, callee.Name))
                    {
                        bool isAdd = callee.Name[0] == 'a';
                        string evt = $"{et.Name}.{callee.Name[(isAdd ? 4 : 7)..]}";
                        var sub = new Subscription(evt, HandlerBefore(m, body, k), site);
                        (isAdd ? adds : removes).Add(sub);
                        if (!isAdd && !reachable.Contains(m)) removes.RemoveAt(removes.Count - 1);   // Dispose 밖의 해제는 수명 종료 해제가 아님
                    }
                    else if (callee.Name is "Dispose" or "DisposeAsync" && callee.DeclaringType?.FullName is { } dt && HandleTypes.Contains(dt) && reachable.Contains(m))
                    {
                        disposedHandleTypes.Add(dt);
                    }
                    else if (callee.Name == "CreateLinkedTokenSource" && k + 1 < body.Count && IsStoreField(body[k + 1].Op))
                    {
                        storedHandles.Add(("System.Threading.CancellationTokenSource", site));
                    }
                }
                else if (ins.Op == OpCodes.Newobj)
                {
                    var ctor = Resolve(m, ins.Token);
                    string? tn = ctor?.DeclaringType?.FullName;
                    if (tn is not null && HandleTypes.Contains(tn) && k + 1 < body.Count && IsStoreField(body[k + 1].Op))
                        storedHandles.Add((tn, $"{Short(m)}@IL_{ins.Offset:X4}"));
                }
            }
        }

        subscriptions = adds.Count;
        foreach (var a in adds)
        {
            bool released = removes.Any(r => r.Event == a.Event && (a.Handler is null || r.Handler is null || r.Handler == a.Handler));
            if (released) continue;
            string why = removes.Any(r => r.Event == a.Event)
                ? "Dispose 해제 핸들러 불일치(람다/다른 델리게이트 — 실제로 해제되지 않음)"
                : roots.Count == 0 ? "IDisposable 미구현 — 해제 경로 없음" : "Dispose 에 구독 해제(-=) 누락";
            findings.Add(new(component.FullName!, "event-leak", $"{a.Event} += {a.Handler ?? "?"} ({a.Site}) — {why}"));
        }
        // 규칙 ③ async void — 예외가 AsyncVoidMethodBuilder 를 거쳐 스레드풀에 재던져져 프로세스를 종료시킨다(타이머 콜백
        //        `async _ => …` 가 전형). 본문 전체를 사용자 catch(Exception/object) 로 감싼 경우만 허용.
        foreach (var m in methods.OfType<MethodInfo>())
        {
            if (m.ReturnType != typeof(void)) continue;
            var smType = m.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
            var moveNext = smType?.GetMethod("MoveNext", All);
            if (moveNext is null) continue;
            var clauses = moveNext.GetMethodBody()?.ExceptionHandlingClauses ?? (IList<ExceptionHandlingClause>)Array.Empty<ExceptionHandlingClause>();
            int catchAll = clauses.Count(c => c.Flags == ExceptionHandlingClauseOptions.Clause
                                              && (c.CatchType == typeof(Exception) || c.CatchType == typeof(object)));
            if (catchAll < 2)   // 1개는 컴파일러가 넣는 SetException 경로
                findings.Add(new(component.FullName!, "async-void", $"{Short(m)} — 미포착 예외가 프로세스를 종료(Task 반환 + 호출측 관찰로 교정)"));
        }
        foreach (var (type, site) in storedHandles.Distinct())
        {
            if (!disposedHandleTypes.Contains(type))
                findings.Add(new(component.FullName!, "handle-leak", $"{type} 필드 보관({site}) — Dispose 에서 처분 누락{(roots.Count == 0 ? "(IDisposable 미구현)" : "")}"));
        }
        return findings;
    }

    private static bool IsStoreField(OpCode op) => op == OpCodes.Stfld || op == OpCodes.Stsfld;

    private static bool IsEvent(Type t, string accessor)
    {
        string name = accessor[(accessor[0] == 'a' ? 4 : 7)..];
        for (Type? c = t; c is not null; c = c.BaseType)
            if (c.GetEvent(name, All) is not null) return true;
        return t.GetInterfaces().Any(i => i.GetEvent(name) is not null);
    }

    /// <summary>add_/remove_ 직전 창에서 핸들러 식별자(ldftn 대상 메서드 또는 델리게이트 필드)를 찾는다.</summary>
    private static string? HandlerBefore(MethodBase m, List<Instr> body, int k)
    {
        for (int j = k - 1; j >= 0 && j >= k - 14; j--)
        {
            var ins = body[j];
            if (ins.Op == OpCodes.Ldftn || ins.Op == OpCodes.Ldvirtftn)
                return Resolve(m, ins.Token) is { } h ? $"{h.DeclaringType?.Name}.{h.Name}" : null;
            if (ins.Op == OpCodes.Ldfld || ins.Op == OpCodes.Ldsfld)
            {
                FieldInfo? f = ResolveField(m, ins.Token);
                if (f is null) continue;
                if (f.Name.StartsWith("<>9__", StringComparison.Ordinal) || f.Name == "<>9") continue;   // 정적 람다 캐시 — 뒤이은 ldftn 이 실체
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) return $"field:{f.DeclaringType?.Name}.{f.Name}";
                return null;   // 이벤트 원천(서비스 필드) 로드 — 창을 넘어감
            }
            if (ins.Op == OpCodes.Call || ins.Op == OpCodes.Callvirt)
            {
                var c = Resolve(m, ins.Token);
                if (c is not null && c.Name.StartsWith("get_", StringComparison.Ordinal)) return null;   // 원천 프로퍼티 로드
            }
        }
        return null;
    }

    private static List<MethodBase> CollectMethods(Type t)
    {
        var list = new List<MethodBase>();
        void Walk(Type x)
        {
            list.AddRange(x.GetMethods(All));
            list.AddRange(x.GetConstructors(All));
            foreach (var n in x.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) Walk(n);
        }
        Walk(t);
        return list;
    }

    private static List<Instr> Decode(MethodBase method)
    {
        var list = new List<Instr>();
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (InvalidOperationException) { il = null; }
        if (il is null) return list;
        int i = 0;
        while (i < il.Length)
        {
            int offset = i;
            OpCode op = il[i] == 0xFE ? TwoByte[il[++i]] : OneByte[il[i]];
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
            int token = op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok or OperandType.InlineType
                ? BitConverter.ToInt32(il, i) : 0;
            list.Add(new(offset, op, token));
            i += size;
        }
        return list;
    }

    private static MethodBase? Resolve(MethodBase context, int token)
    {
        if (token == 0) return null;
        try
        {
            return context.Module.ResolveMethod(token,
                context.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null,
                context.IsGenericMethod ? context.GetGenericArguments() : null);
        }
        catch (ArgumentException) { return null; }
    }

    private static FieldInfo? ResolveField(MethodBase context, int token)
    {
        try
        {
            return context.Module.ResolveField(token,
                context.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null,
                context.IsGenericMethod ? context.GetGenericArguments() : null);
        }
        catch (ArgumentException) { return null; }
    }

    private static string Short(MethodBase m) => $"{m.DeclaringType?.Name}.{m.Name}";
}

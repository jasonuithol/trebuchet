using System.Collections;
using System.Reflection;
using Trebuchet.Compiler.Runtime;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Compiler.Syntax;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;

/// <summary>
/// Binds every extern with a csharp symbol to that static method by reflection, so the dev
/// server can run a program whose externs point at the BCL or at an assembly it has loaded.
/// Externs whose symbol cannot be found keep the interpreter's "no implementation" panic.
/// </summary>
public static class ReflectionExterns
{
    public static int Bind(Interpreter it, ModuleSet modules)
    {
        var bound = 0;
        foreach (var m in modules.Modules)
        {
            foreach (var ex in m.Decls.OfType<ExternDecl>())
            {
                var binding = ex.Bindings.FirstOrDefault(b => b.Target == "csharp");
                if (binding is null) continue;
                var dot = binding.Symbol.LastIndexOf('.');
                if (dot < 0) continue;
                var typeName = binding.Symbol[..dot];
                var methodName = binding.Symbol[(dot + 1)..];
                var type = FindType(typeName);
                var method = type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(mi => mi.Name == methodName && mi.GetParameters().Length == ex.Signature.Params.Count);
                if (method is null) continue;
                var parameters = method.GetParameters();
                it.Externs[$"{m.Name}.{ex.Signature.Name}"] = args =>
                    ToValue(Unwrap(method.Invoke(null, args.Select((v, i) => FromValue(v, parameters[i].ParameterType)).ToArray())));
                bound++;
            }
        }
        return bound;
    }

    /// <summary>Looks in the loaded assemblies first, then loads framework assemblies whose name shares the type's namespace (System.Net.Dns lives in System.Net.NameResolution).</summary>
    private static Type? FindType(string typeName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t is not null) ?? Type.GetType(typeName, false);
        if (loaded is not null) return loaded;
        var ns = typeName[..Math.Max(0, typeName.LastIndexOf('.'))];
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in tpa)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!(name.StartsWith(ns, StringComparison.Ordinal) || ns.StartsWith(name, StringComparison.Ordinal))) continue;
            try
            {
                var found = Assembly.LoadFrom(path).GetType(typeName, false);
                if (found is not null) return found;
            }
            catch (Exception) { /* not a managed assembly we can load; keep looking */ }
        }
        return null;
    }

    private static object? Unwrap(object? result)
    {
        if (result is Task task)
        {
            task.Wait();
            return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
        }
        var t = result?.GetType();
        if (t is not null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return Unwrap(t.GetMethod("AsTask")!.Invoke(result, null));
        return result;
    }

    private static object? FromValue(Value v, Type target) => v switch
    {
        StringValue s => s.V,
        IntValue i => target == typeof(int) ? (object)(int)i.V : i.V,
        FloatValue f => f.V,
        BoolValue b => b.V,
        InstantValue t => target == typeof(DateTime) ? (object)t.V.UtcDateTime : t.V,
        UnitValue => null,
        ListValue l when target.IsArray => ToArray(l, target.GetElementType()!),
        ListValue l => l.Items.Select(x => FromValue(x, typeof(object))).ToArray(),
        _ => throw new TrebPanic($"extern: cannot pass {v.Show()} to a host parameter of type {target.Name}"),
    };

    private static Array ToArray(ListValue l, Type elem)
    {
        var arr = Array.CreateInstance(elem, l.Items.Count);
        for (var i = 0; i < l.Items.Count; i++) arr.SetValue(FromValue(l.Items.Get(i), elem), i);
        return arr;
    }

    private static Value ToValue(object? o) => o switch
    {
        null => UnitValue.Instance,
        string s => new StringValue(s),
        int i => new IntValue(i),
        long l => new IntValue(l),
        double d => new FloatValue(d),
        float f => new FloatValue(f),
        bool b => BoolValue.Of(b),
        DateTimeOffset t => new InstantValue(t),
        DateTime dt => new InstantValue(new DateTimeOffset(dt.ToUniversalTime())),
        IEnumerable items => new ListValue(Vector<Value>.From(items.Cast<object?>().Select(ToValue))),
        _ => new StringValue(o.ToString() ?? ""),
    };
}

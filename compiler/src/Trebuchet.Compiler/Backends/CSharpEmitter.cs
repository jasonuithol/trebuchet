using System.Text;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Compiler.Syntax;
using static Trebuchet.Compiler.Semantics.Unifier;

namespace Trebuchet.Compiler.Backends;

/// <summary>
/// Lowers a checked program to C# source against Trebuchet.Runtime. One namespace
/// (<c>Generated</c>) holds every type; each module's functions live in a static class
/// named after the module path. Effects are not lowered: Suspend runs synchronously,
/// matching the interpreter. if and match become statements with a typed temporary,
/// and <c>?</c> becomes an early return, so every function body is emitted as statements.
/// </summary>
public sealed class CSharpEmitter
{
    private readonly ModuleSet _modules;
    private readonly TypeChecker _checker;
    private readonly List<ShapeT> _shapes = new();
    /// <summary>Service methods emitted as async because a shape they implement declares the member Suspend.</summary>
    private readonly HashSet<FnT> _forcedAsync = new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<string> Keywords = new()
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default",
        "delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if",
        "implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override",
        "params","private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static",
        "string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual",
        "void","volatile","while",
    };

    public CSharpEmitter(ModuleSet modules, TypeChecker checker)
    {
        _modules = modules;
        _checker = checker;
        foreach (var m in modules.Modules)
            foreach (var d in m.Decls)
                if (d is ShapeDecl sh) _shapes.Add(checker.ShapeTypeOf(sh));
        foreach (var m in modules.Modules)
            foreach (var d in m.Decls)
                if (d is ServiceDecl s)
                {
                    var st = checker.ServiceTypeOf(s);
                    foreach (var shape in _shapes.Where(h => checker.Satisfies(st, h)))
                        foreach (var (name, member) in shape.Members)
                            if (Suspends(member) && st.Methods.TryGetValue(name, out var impl)) _forcedAsync.Add(impl);
                }
    }

    /// <summary>
    /// While emitting the Async twin of an effect-polymorphic function, function-typed
    /// parameters without an effect clause are treated as suspending.
    /// </summary>
    private bool _polyAsync;

    /// <summary>Whether calling this function may suspend, and therefore whether it is emitted async and awaited.</summary>
    public bool Suspends(FnT f)
    {
        var o = f.Origin ?? f;
        if (_forcedAsync.Contains(o)) return true;
        if (o.Effects is null && !_checker.IsBuiltin(o) && !HasFrame(o)) return _polyAsync; // an unspecified function value
        return _checker.EffectsOf(f).Contains("Suspend");
    }

    private bool HasFrame(FnT f) => _checker.HasFrame(f);

    private static bool IsPoly(TypeChecker checker, FnT f) => checker.PolyParamsOf(f).Count > 0;

    private string Ret(FnT f) => Suspends(f) ? $"ValueTask<{CsType(f.Return)}>" : CsType(f.Return);

    /// <summary>
    /// Emits one .cs file per module plus a project file. Keys are file names. With
    /// <paramref name="host"/>, also emits IServiceCollection registration for every root,
    /// which needs the ASP.NET shared framework for the DI abstractions.
    /// </summary>
    private bool _lineDirectives;

    /// <summary>Version of the Trebuchet.Runtime package a generated project references when no project path is given.</summary>
    public static string RuntimeVersion =>
        (typeof(CSharpEmitter).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public IReadOnlyDictionary<string, string> Emit(string? runtimeProjectPath, bool host = false, bool lineDirectives = true)
    {
        _lineDirectives = lineDirectives;
        var files = new Dictionary<string, string>();
        var runtimeRef = runtimeProjectPath is not null
            ? $"<ProjectReference Include=\"{runtimeProjectPath}\" />"
            : $"<PackageReference Include=\"Trebuchet.Runtime\" Version=\"{RuntimeVersion}\" />";
        foreach (var m in _modules.Modules)
            files[ClassName(m.Name) + ".cs"] = EmitModule(m);
        if (host) files["TrebuchetHost.cs"] = EmitHost();
        var framework = host ? "\n              <ItemGroup>\n                <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n              </ItemGroup>" : "";
        files["Generated.csproj"] = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <NoWarn>CS8632;CS0108;CS0109;CS1998</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                {runtimeRef}
              </ItemGroup>{framework}
            </Project>
            """;
        return files;
    }

    /// <summary>
    /// For each composition root, an extension method that registers every entry as a keyed
    /// factory (keyed by entry name) and, under its own type and each shape it satisfies, as an
    /// unkeyed TryAdd registration. Dependencies resolve through the container by declared type
    /// first, so a host that registered its own implementation before calling this wins; the
    /// root's entries are the defaults. The container owns lifetimes and disposal.
    /// </summary>
    private string EmitHost()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by treb emit --host </auto-generated>");
        sb.AppendLine("using System;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine("using Trebuchet.Runtime;");
        sb.AppendLine("using static Trebuchet.Runtime.Prelude;");
        var rootModules = _modules.Modules.Where(m => m.Decls.OfType<RootDecl>().Any()).ToList();
        foreach (var name in rootModules.SelectMany(m => m.Imports.Select(i => i.Name).Prepend(m.Name)).Distinct())
            sb.AppendLine($"using static Generated.{ClassName(name)};");
        sb.AppendLine();
        sb.AppendLine("namespace Generated;");
        sb.AppendLine();
        sb.AppendLine("public static class TrebuchetHost");
        sb.AppendLine("{");
        foreach (var m in _modules.Modules)
        {
            foreach (var root in m.Decls.OfType<RootDecl>())
                EmitHostRoot(sb, root, m);
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private void EmitHostRoot(StringBuilder sb, RootDecl root, Module m)
    {
        var recordName = $"Root_{root.Name}";
        var entries = root.Entries.Where(e => e.Name is not null).ToDictionary(e => e.Name!, e => e);
        var entryTypes = entries.Keys.ToDictionary(n => n, n => _checker.RootEntryTypes[(root, n)]);
        var scope = _checker.ScopeOf(m);
        sb.AppendLine($"    /// <summary>Registers the entries of root '{root.Name}' from {m.Name}. Host registrations made before this call take precedence.</summary>");
        sb.AppendLine($"    public static IServiceCollection AddTrebuchet_{root.Name}(this IServiceCollection services)");
        sb.AppendLine("    {");

        // A dependency resolves by its declared type from the container (host or default), falling back to the named entry.
        string Resolve(string depName, TType depType)
        {
            var cs = CsType(depType);
            var byType = $"sp.GetService<{cs}>()";
            if (entries.ContainsKey(depName)) return $"({byType} ?? sp.GetRequiredKeyedService<{cs}>(\"{depName}\"))";
            var typeName = (Prune(depType) as ServiceT)?.Name ?? (Prune(depType) as ShapeT)?.Name;
            var byTypeEntry = entries.FirstOrDefault(e => e.Value.Value is TypeNameExpr te && te.Name == typeName);
            if (byTypeEntry.Key is not null) return $"({byType} ?? sp.GetRequiredKeyedService<{cs}>(\"{byTypeEntry.Key}\"))";
            if (Prune(depType) is ServiceT implicitService) return $"({byType} ?? {Construct(implicitService, new Dictionary<string, string>())})";
            if (Prune(depType) is FnT factory && Prune(factory.Return) is ServiceT target && TypeChecker.PlanFactory(factory, target) is { } plan)
            {
                var ps = factory.Params.Select((_, i) => $"__p{i}").ToList();
                var fill = new Dictionary<string, string>();
                for (var i = 0; i < plan.Count; i++) fill[target.Dependencies[plan[i]].Name] = ps[i];
                return $"({byType} ?? (({cs})(({string.Join(", ", ps)}) => {Construct(target, fill)})))";
            }
            return $"sp.GetRequiredService<{cs}>()";
        }

        string Construct(ServiceT st, IReadOnlyDictionary<string, string> overrides) =>
            $"new {st.Name}({string.Join(", ", st.Dependencies.Select(d => overrides.TryGetValue(d.Name, out var o) ? o : Resolve(d.Name, d.Type)))})";

        foreach (var (name, entry) in entries)
        {
            var t = entryTypes[name];
            var cs = CsType(t);
            string factory;
            var lifetime = "Singleton";
            if (entry.Value is TypeNameExpr tn && scope.LookupType(tn.Name) is ServiceT st)
            {
                factory = Construct(st, new Dictionary<string, string>());
                if (st.Scoped) lifetime = "Scoped";
            }
            else if (entry.Value is CallExpr { Callee: TypeNameExpr ct, HasParens: false } c && scope.LookupType(ct.Name) is ServiceT cst)
            {
                var fe = new FnEmitter(this, m, null, 3);
                var overrides = new Dictionary<string, string>();
                foreach (var a in c.BlockArgs.Where(a => a.Name is not null)) overrides[a.Name!] = fe.EmitExpr(a.Value, null);
                factory = Construct(cst, overrides);
                if (cst.Scoped) lifetime = "Scoped";
            }
            else
            {
                var fe = new FnEmitter(this, m, null, 3);
                var text = fe.EmitExpr(entry.Value, t);
                if (fe.Text.Length > 0) throw new InvalidOperationException($"root entry '{name}' needs statements; host registration supports expression entries only");
                factory = text.Contains("await ") ? $"System.Threading.Tasks.Task.Run(async () => {text}).GetAwaiter().GetResult()" : text;
            }
            sb.AppendLine($"        services.AddKeyed{lifetime}<{cs}>(\"{name}\", (sp, _) => {factory});");
            sb.AppendLine($"        services.TryAdd{lifetime}<{cs}>(sp => sp.GetRequiredKeyedService<{cs}>(\"{name}\"));");
            if (Prune(t) is ServiceT svc)
                foreach (var shape in _shapes.Where(h => _checker.Satisfies(svc, h)))
                    sb.AppendLine($"        services.TryAdd{lifetime}<{shape.Name}>(sp => sp.GetRequiredKeyedService<{cs}>(\"{name}\"));");
        }
        var ctorArgs = string.Join(", ", entries.Keys.Select(n => $"sp.GetRequiredKeyedService<{CsType(entryTypes[n])}>(\"{n}\")"));
        sb.AppendLine($"        services.AddSingleton<{recordName}>(sp => new {recordName}({ctorArgs}));");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// Locates Trebuchet.Runtime.csproj relative to the compiler assembly, for generated project
    /// references in a source checkout. Null when running from an installed tool, in which case
    /// generated projects take a package reference instead.
    /// </summary>
    public static string? FindRuntimeProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Trebuchet.Runtime", "Trebuchet.Runtime.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ------------------------------------------------------------ naming

    public static string ClassName(string moduleName) =>
        string.Join("_", moduleName.Split('.').Select(Pascal));

    private static string Pascal(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Id(string name) => Keywords.Contains(name) ? "@" + name : name;

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\0' => "\\0",
                _ => ch.ToString(),
            });
        return sb.Append('"').ToString();
    }

    public string CsType(TType t)
    {
        t = Prune(t);
        return t switch
        {
            PrimT p => p.Name switch
            {
                "Int" => "long", "Float" => "double", "Bool" => "bool", "String" => "string", "Unit" => "Unit",
                "Instant" => "DateTimeOffset", "Never" => "Unit", _ => "object",
            },
            AppT a => $"{a.Ctor}<{string.Join(", ", a.Args.Select(CsType))}>",
            TupleT tt => $"({string.Join(", ", tt.Items.Select(CsType))})",
            RecordT r => r.TypeArgs.Count == 0 ? r.Name : $"{r.Name}<{string.Join(", ", r.TypeArgs.Select(CsType))}>",
            UnionT u => u.TypeArgs.Count == 0 ? u.Name : $"{u.Name}<{string.Join(", ", u.TypeArgs.Select(CsType))}>",
            ParamT p => p.Name,
            ServiceT s => s.Name,
            ShapeT h => h.Name,
            FnT f => f.Params.Count == 0 ? $"Func<{Ret(f)}>" : $"Func<{string.Join(", ", f.Params.Select(CsType))}, {Ret(f)}>",
            VarT => "object",
            _ => "object",
        };
    }

    // ------------------------------------------------------------ modules

    private string EmitModule(Module m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by treb emit. Source: " + Path.GetFileName(m.File) + " </auto-generated>");
        sb.AppendLine("using System;");
        sb.AppendLine("using Trebuchet.Runtime;");
        sb.AppendLine("using static Trebuchet.Runtime.Prelude;");
        sb.AppendLine($"using static Generated.{ClassName(m.Name)};");
        foreach (var imp in m.Imports) sb.AppendLine($"using static Generated.{ClassName(imp.Name)};");
        sb.AppendLine();
        sb.AppendLine("namespace Generated;");
        sb.AppendLine();

        var fns = new StringBuilder();
        foreach (var d in m.Decls)
        {
            switch (d)
            {
                case RecordDecl r: EmitRecord(sb, r, m); break;
                case UnionDecl u: EmitUnion(sb, u); break;
                case ShapeDecl sh: EmitShape(sb, sh); break;
                case InstanceDecl inst: EmitInstance(sb, inst, m); break;
                case ServiceDecl s: EmitService(sb, s, m); break;
                case FnDecl fn: EmitFunction(fns, fn, m, 1, isMethod: false); break;
                case ExternDecl ex: EmitExtern(fns, ex, m); break;
                case RootDecl root: EmitRoot(sb, fns, root, m); break;
            }
        }
        sb.AppendLine($"public static partial class {ClassName(m.Name)}");
        sb.AppendLine("{");
        sb.Append(fns);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Generic(IReadOnlyList<string> typeParams) => typeParams.Count == 0 ? "" : $"<{string.Join(", ", typeParams)}>";

    private void EmitRecord(StringBuilder sb, RecordDecl r, Module m)
    {
        var rt = _checker.RecordTypeOf(r);
        var fields = rt.Fields;
        var hasInit = r.Fields.Any(f => f.Init is not null);
        var name = r.Name + Generic(r.TypeParams);
        if (r.IsEntity)
        {
            sb.AppendLine($"public sealed class {name}");
        }
        else if (!hasInit)
        {
            sb.AppendLine($"[TrebuchetRecord] public sealed record {name}({string.Join(", ", fields.Select(f => $"{CsType(f.Type)} {Id(f.Name)}"))});");
            sb.AppendLine();
            return;
        }
        else sb.AppendLine($"[TrebuchetRecord] public sealed record {name}");
        sb.AppendLine("{");
        foreach (var f in fields) sb.AppendLine($"    public {CsType(f.Type)} {Id(f.Name)} {{ get; init; }}");
        sb.AppendLine($"    public {r.Name}({string.Join(", ", fields.Select(f => $"{CsType(f.Type)} {Id(f.Name)}"))})");
        sb.AppendLine("    {");
        foreach (var f in r.Fields)
        {
            var ft = rt.Field(f.Name)!;
            if (f.Init is not null)
            {
                sb.AppendLine("        {");
                sb.AppendLine($"            var value = {Id(f.Name)};");
                var fe = new FnEmitter(this, m, ft, 3);
                fe.EmitInto(f.Init, FnEmitter.Target.Assign(Id(f.Name)), ft);
                sb.Append(fe.Text);
                sb.AppendLine("        }");
            }
            sb.AppendLine($"        this.{Id(f.Name)} = {Id(f.Name)};");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private void EmitUnion(StringBuilder sb, UnionDecl u)
    {
        var ut = _checker.UnionTypeOf(u);
        var g = Generic(u.TypeParams);
        sb.AppendLine($"[TrebuchetUnion] public abstract record {u.Name}{g};");
        foreach (var v in ut.Variants)
        {
            if (v.Fields.Count == 0)
                sb.AppendLine($"[TrebuchetVariant] public sealed record {v.Name}{g} : {u.Name}{g} {{ public static readonly {v.Name}{g} Instance = new(); public override string ToString() => \"{v.Name}\"; }}");
            else
                sb.AppendLine($"[TrebuchetVariant] public sealed record {v.Name}{g}({string.Join(", ", v.Fields.Select(f => $"{CsType(f.Type)} {Id(f.Name)}"))}) : {u.Name}{g};");
        }
        sb.AppendLine();
    }

    /// <summary>The expression that produces the dictionary for a shape at a type.</summary>
    private string DictExpr(string shape, TType t)
    {
        t = Prune(t);
        if (t is ParamT p) return $"__{shape}_{p.Name}";
        if (BuiltinSignatures.HasBuiltinInstance(shape, t)) return $"Ord_{((PrimT)t).Name}.Instance";
        return $"{shape}_{TypeChecker.TypeKey(t)}.Instance";
    }

    /// <summary>Hidden parameters a constrained function takes: one dictionary per (parameter, shape), after the declared parameters.</summary>
    private string DictParams(FnT type, string declared)
    {
        var parts = new List<string>();
        foreach (var param in type.TypeParams)
            if (type.Constraints.TryGetValue(param, out var shapes))
                foreach (var shape in shapes) parts.Add($"{shape}<{param}> __{shape}_{param}");
        if (parts.Count == 0) return declared;
        return declared.Length == 0 ? string.Join(", ", parts) : declared + ", " + string.Join(", ", parts);
    }

    private void EmitInstance(StringBuilder sb, InstanceDecl inst, Module m)
    {
        var target = _checker.InstanceTargets[inst];
        var name = $"{inst.Shape}_{TypeChecker.TypeKey(target)}";
        sb.AppendLine($"public sealed class {name} : {inst.Shape}<{CsType(target)}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public static readonly {name} Instance = new();");
        foreach (var method in inst.Methods)
        {
            var mt = _checker.InstanceMemberTypes[(inst, method.Signature.Name)];
            EmitFunctionVariant(sb, method, m, mt, "    ", isMethod: true, method.Signature.Name, polyAsync: false);
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private void EmitShape(StringBuilder sb, ShapeDecl sh)
    {
        var ht = _checker.ShapeTypeOf(sh);
        sb.AppendLine($"public interface {sh.Name}{Generic(ht.TypeParams)}");
        sb.AppendLine("{");
        foreach (var member in sh.Members)
        {
            var mt = ht.Members[member.Name];
            sb.AppendLine($"    {Ret(mt)} {Id(member.Name)}({Params(member.Params, mt)});");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private string Params(IReadOnlyList<Param> ps, FnT t) =>
        string.Join(", ", ps.Select((p, i) => $"{CsType(t.Params[i])} {Id(p.Name)}"));

    private void EmitService(StringBuilder sb, ServiceDecl s, Module m)
    {
        var st = _checker.ServiceTypeOf(s);
        var interfaces = _shapes.Where(h => _checker.Satisfies(st, h)).Select(h => h.Name).ToList();
        var release = _checker.ReleaseOf(st);
        var releaseAsync = release is not null && Suspends(release);
        if (release is not null) interfaces.Add(releaseAsync ? "IAsyncDisposable" : "IDisposable");
        sb.AppendLine($"public sealed class {s.Name}{(interfaces.Count > 0 ? " : " + string.Join(", ", interfaces) : "")}");
        sb.AppendLine("{");
        if (release is not null)
            sb.AppendLine(releaseAsync
                ? "    public async ValueTask DisposeAsync() { await release(); }"
                : "    public void Dispose() { release(); }");
        foreach (var (name, type) in st.Dependencies) sb.AppendLine($"    private readonly {CsType(type)} {Id(name)};");
        if (st.Dependencies.Count > 0)
        {
            sb.AppendLine($"    public {s.Name}({string.Join(", ", st.Dependencies.Select(d => $"{CsType(d.Type)} {Id(d.Name)}"))})");
            sb.AppendLine("    {");
            foreach (var (name, _) in st.Dependencies) sb.AppendLine($"        this.{Id(name)} = {Id(name)};");
            sb.AppendLine("    }");
        }
        foreach (var method in s.Methods) EmitFunction(sb, method, m, 1, isMethod: true);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private void EmitFunction(StringBuilder sb, FnDecl fn, Module m, int indent, bool isMethod)
    {
        var scope = _checker.ScopeOf(m);
        FnT type;
        if (isMethod)
        {
            var svc = _modules.Modules.SelectMany(mm => mm.Decls).OfType<ServiceDecl>().First(s => s.Methods.Contains(fn));
            type = _checker.ServiceTypeOf(svc).Methods[fn.Signature.Name];
        }
        else type = (FnT)((ValueSym)scope.LookupLocalValue(fn.Signature.Name)!).Type;
        var pad = new string(' ', indent * 4);
        EmitFunctionVariant(sb, fn, m, type, pad, isMethod, fn.Signature.Name, polyAsync: false);
        if (IsPoly(_checker, type))
            EmitFunctionVariant(sb, fn, m, type, pad, isMethod, fn.Signature.Name + "Async", polyAsync: true);
    }

    private void EmitFunctionVariant(StringBuilder sb, FnDecl fn, Module m, FnT type, string pad, bool isMethod, string name, bool polyAsync)
    {
        var saved = _polyAsync;
        _polyAsync = polyAsync;
        var isAsync = Suspends(type) || polyAsync;
        var ret = isAsync ? $"ValueTask<{CsType(type.Return)}>" : CsType(type.Return);
        sb.AppendLine($"{pad}public {(isMethod ? "" : "static ")}{(isAsync ? "async " : "")}{ret} {Id(name)}{Generic(fn.Signature.TypeParams)}({DictParams(type, Params(fn.Signature.Params, type))})");
        sb.AppendLine($"{pad}{{");
        var fe = new FnEmitter(this, m, type.Return, pad.Length / 4 + 1);
        fe.EmitBlockInto(fn.Body, FnEmitter.Target.Return, type.Return);
        sb.Append(fe.Text);
        sb.AppendLine($"{pad}}}");
        sb.AppendLine();
        _polyAsync = saved;
    }

    /// <summary>
    /// An extern becomes a wrapper around the host symbol. If the extern returns a Result, the
    /// host value is wrapped in ok and each catch line becomes a catch clause producing the
    /// variant; any other exception becomes a panic. A Suspend extern awaits the host call.
    /// </summary>
    private void EmitExtern(StringBuilder sb, ExternDecl ex, Module m)
    {
        var type = (FnT)((ValueSym)_checker.ScopeOf(m).LookupLocalValue(ex.Signature.Name)!).Type;
        var binding = ex.Bindings.FirstOrDefault(b => b.Target == "csharp");
        var suspends = Suspends(type);
        sb.AppendLine($"    public static {(suspends ? "async " : "")}{Ret(type)} {Id(ex.Signature.Name)}({Params(ex.Signature.Params, type)})");
        sb.AppendLine("    {");
        if (binding is null)
        {
            sb.AppendLine($"        throw new TrebPanic(\"extern {ex.Signature.Name} has no csharp binding\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            return;
        }
        var args = string.Join(", ", ex.Signature.Params.Select(p => Id(p.Name)));
        var call = $"{binding.Symbol}({args})";
        if (suspends) call = $"(await {call})";
        var isResult = Prune(type.Return) is AppT { Ctor: "Result" };
        var payloadType = isResult ? ((AppT)Prune(type.Return)).Args[0] : type.Return;
        var converted = $"Boundary.To<{CsType(payloadType)}>({call})";
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine(isResult ? $"            return ({CsType(type.Return)})ok({converted});" : $"            return {converted};");
        sb.AppendLine("        }");
        foreach (var c in ex.Catches.Where(c => c.Target == "csharp"))
        {
            var unionType = CsType(((AppT)Prune(type.Return)).Args[1]);
            sb.AppendLine($"        catch ({c.ExceptionType} ex) {{ return ({CsType(type.Return)})error(({unionType})new {c.Variant}(ex.Message)); }}");
        }
        sb.AppendLine("        catch (TrebPanic) { throw; }");
        sb.AppendLine($"        catch (Exception ex) {{ throw new TrebPanic(\"extern {ex.Signature.Name}: \" + ex.Message, ex); }}");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private void EmitRoot(StringBuilder types, StringBuilder fns, RootDecl root, Module m)
    {
        var recordName = $"Root_{root.Name}";
        var entries = root.Entries.Where(e => e.Name is not null).ToDictionary(e => e.Name!, e => e);
        var entryTypes = entries.Keys.ToDictionary(n => n, n => _checker.RootEntryTypes[(root, n)]);
        types.AppendLine($"public sealed record {recordName}({string.Join(", ", entries.Keys.Select(n => $"{CsType(entryTypes[n])} {Id(n)}"))});");
        types.AppendLine();

        var fe = new FnEmitter(this, m, null, 2);
        var done = new HashSet<string>();
        var scope = _checker.ScopeOf(m);

        string Construct(ServiceT st, IReadOnlyDictionary<string, string> overrides)
        {
            var args = new List<string>();
            foreach (var (depName, depType) in st.Dependencies)
            {
                if (overrides.TryGetValue(depName, out var o)) { args.Add(o); continue; }
                if (entries.ContainsKey(depName)) { Emit(depName); args.Add(Id(depName)); continue; }
                var typeName = (Prune(depType) as ServiceT)?.Name ?? (Prune(depType) as ShapeT)?.Name;
                var byType = entries.FirstOrDefault(e => e.Value.Value is TypeNameExpr te && te.Name == typeName);
                if (byType.Key is not null) { Emit(byType.Key); args.Add(Id(byType.Key)); continue; }
                if (Prune(depType) is ServiceT implicitService) { args.Add(Construct(implicitService, new Dictionary<string, string>())); continue; }
                if (Prune(depType) is FnT factory && Prune(factory.Return) is ServiceT target && TypeChecker.PlanFactory(factory, target) is { } plan)
                {
                    // synthesised factory: lambda parameters fill the target's constructor by type, the rest come from the root
                    var ps = factory.Params.Select((_, i) => $"__p{i}").ToList();
                    var fill = new Dictionary<string, string>();
                    for (var i = 0; i < plan.Count; i++) fill[target.Dependencies[plan[i]].Name] = ps[i];
                    args.Add($"(({string.Join(", ", ps)}) => {Construct(target, fill)})");
                    continue;
                }
                args.Add("default");
            }
            return $"new {st.Name}({string.Join(", ", args)})";
        }

        void Emit(string name)
        {
            if (!done.Add(name)) return;
            var entry = entries[name];
            string value;
            if (entry.Value is TypeNameExpr tn && scope.LookupType(tn.Name) is ServiceT st)
                value = Construct(st, new Dictionary<string, string>());
            else if (entry.Value is CallExpr { Callee: TypeNameExpr ct, HasParens: false } c && scope.LookupType(ct.Name) is ServiceT cst)
            {
                var overrides = new Dictionary<string, string>();
                foreach (var a in c.BlockArgs.Where(a => a.Name is not null)) overrides[a.Name!] = fe.EmitExpr(a.Value, null);
                value = Construct(cst, overrides);
            }
            else value = fe.EmitExpr(entry.Value, entryTypes[name]);
            fe.Line($"{CsType(entryTypes[name])} {Id(name)} = {value};");
        }

        foreach (var name in entries.Keys) Emit(name);
        fe.Line($"return new {recordName}({string.Join(", ", entries.Keys.Select(Id))});");
        var rootAsync = _checker.RootEffects(root).Contains("Suspend");
        fns.AppendLine($"    public static {(rootAsync ? "async ValueTask<" + recordName + ">" : recordName)} {Id(root.Name)}()");
        fns.AppendLine("    {");
        fns.Append(fe.Text);
        fns.AppendLine("    }");
        fns.AppendLine();
    }

    // ------------------------------------------------------------ function bodies

    private sealed class FnEmitter
    {
        public readonly record struct Target(string Kind, string? Name)
        {
            public static readonly Target Return = new("return", null);
            public static readonly Target Discard = new("discard", null);
            public static Target Assign(string name) => new("assign", name);
        }

        private readonly CSharpEmitter _e;
        private readonly Module _m;
        private readonly TType? _returnType;
        private readonly StringBuilder _sb = new();
        private int _indent;
        private int _tmp;

        public FnEmitter(CSharpEmitter e, Module m, TType? returnType, int indent)
        {
            _e = e;
            _m = m;
            _returnType = returnType;
            _indent = indent;
        }

        public string Text => _sb.ToString();

        public void Line(string s) => _sb.Append(new string(' ', _indent * 4)).AppendLine(s);

        private string Tmp(string hint = "t") => $"__{hint}{++_tmp}";

        private TType TypeOf(Expr e) => _e._checker.ExprTypes.TryGetValue(e, out var t) ? t : PrimT.Unknown;

        private FnT? CalleeOf(CallExpr c) => _e._checker.CalleeOf.GetValueOrDefault(c);

        // ---- statements

        public void EmitBlockInto(Block block, Target target, TType? expected)
        {
            for (var i = 0; i < block.Stmts.Count; i++)
            {
                var s = block.Stmts[i];
                var last = i == block.Stmts.Count - 1;
                if (_e._lineDirectives && s.Pos.Line > 0)
                    _sb.AppendLine($"#line {s.Pos.Line} \"{Path.GetFullPath(_m.File).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                switch (s)
                {
                    case BindingStmt b:
                    {
                        var t = TypeOf(b.Value);
                        if (b.Value is IfExpr or MatchExpr)
                        {
                            Line($"{_e.CsType(t)} {Id(b.Name)};");
                            EmitInto(b.Value, Target.Assign(Id(b.Name)), t);
                        }
                        else
                        {
                            var v = EmitExpr(b.Value, t);
                            Line($"{_e.CsType(t)} {Id(b.Name)} = {v};");
                        }
                        if (last) EmitTarget(target, "Unit.Value", PrimT.Unit);
                        break;
                    }
                    case DestructureStmt ds:
                    {
                        var t = TypeOf(ds.Value);
                        var tmp = Tmp("d");
                        Line($"var {tmp} = {EmitExpr(ds.Value, t)};");
                        Destructure(ds.Pattern, t, tmp);
                        if (last) EmitTarget(target, "Unit.Value", PrimT.Unit);
                        break;
                    }
                    case UseStmt u:
                    {
                        var t = TypeOf(u.Value);
                        var v = EmitExpr(u.Value, t);
                        var release = Prune(t) is ServiceT st ? _e._checker.ReleaseOf(st) : null;
                        var prefix = release is not null && _e.Suspends(release) ? "await using " : "using ";
                        Line($"{prefix}{_e.CsType(t)} {Id(u.Name)} = {v};");
                        if (last) EmitTarget(target, "Unit.Value", PrimT.Unit);
                        break;
                    }
                    case ExprStmt es:
                        EmitInto(es.Value, last ? target : Target.Discard, last ? expected : null);
                        break;
                }
            }
            if (block.Stmts.Count == 0) EmitTarget(target, "Unit.Value", PrimT.Unit);
            else if (_e._lineDirectives) _sb.AppendLine("#line default");
        }

        private void EmitTarget(Target target, string value, TType? type)
        {
            switch (target.Kind)
            {
                case "return":
                    Line($"return {Coerce(value, type, _returnType)};");
                    break;
                case "assign":
                    Line($"{target.Name} = {value};");
                    break;
                default:
                    Line($"_ = {value};");
                    break;
            }
        }

        /// <summary>Casts the untyped Result/Option constructors when the destination type is known.</summary>
        private string Coerce(string value, TType? valueType, TType? destination)
        {
            if (destination is null) return value;
            var d = Prune(destination);
            if (d is AppT { Ctor: "Result" or "Option" } && !value.StartsWith("(")) return $"({_e.CsType(d)})({value})";
            return value;
        }

        public void EmitInto(Expr e, Target target, TType? expected)
        {
            switch (e)
            {
                case IfExpr ifx:
                {
                    var cond = EmitExpr(ifx.Condition, PrimT.Bool);
                    Line($"if ({cond})");
                    Line("{");
                    _indent++;
                    EmitBlockInto(ifx.Then, ifx.Else is null ? Target.Discard : target, expected);
                    _indent--;
                    Line("}");
                    if (ifx.Else is not null)
                    {
                        Line("else");
                        Line("{");
                        _indent++;
                        EmitBlockInto(ifx.Else, target, expected);
                        _indent--;
                        Line("}");
                    }
                    else EmitTarget(target, "Unit.Value", PrimT.Unit);
                    break;
                }
                case MatchExpr mx:
                {
                    var scrutineeType = TypeOf(mx.Scrutinee);
                    var scrutinee = EmitExpr(mx.Scrutinee, null);
                    Line($"switch ({scrutinee})");
                    Line("{");
                    _indent++;
                    foreach (var arm in mx.Arms)
                    {
                        _patternGuards.Clear();
                        _patternPrelude.Clear();
                        var pattern = Pattern(arm.Pattern, scrutineeType);
                        if (pattern == "_") pattern = "var _"; // a bare discard is not a valid switch-statement label
                        var guards = new List<string>(_patternGuards);
                        var prelude = new List<string>(_patternPrelude);
                        if (arm.Guard is not null) guards.Add(EmitExpr(arm.Guard, PrimT.Bool));
                        Line($"case {pattern}{(guards.Count > 0 ? " when " + string.Join(" && ", guards) : "")}:");
                        Line("{");
                        foreach (var pre in prelude) Line("    " + pre);
                        _indent++;
                        EmitBlockInto(arm.Body, target, expected);
                        if (target.Kind != "return") Line("break;");
                        _indent--;
                        Line("}");
                    }
                    Line("default: throw new TrebPanic(\"no match arm\");");
                    _indent--;
                    Line("}");
                    break;
                }
                default:
                {
                    var v = EmitExpr(e, expected);
                    EmitTarget(target, v, TypeOf(e));
                    break;
                }
            }
        }

        // ---- patterns

        /// <summary>Extra <c>when</c> conditions a pattern needs that C# patterns cannot express (an as-pattern over a literal).</summary>
        private readonly List<string> _patternGuards = new();
        /// <summary>Statements to run at the start of an arm body: names an as-pattern over a bare name binds twice.</summary>
        private readonly List<string> _patternPrelude = new();

        /// <summary>Bindings for an irrefutable pattern over a value already held in <paramref name="access"/>.</summary>
        private void Destructure(Syntax.Pattern p, TType t, string access)
        {
            t = Prune(t);
            switch (p)
            {
                case WildcardPattern: break;
                case BindPattern b: Line($"var {Id(b.Name)} = {access};"); break;
                case AsPattern ap:
                    Line($"var {Id(ap.Name)} = {access};");
                    Destructure(ap.Inner, t, access);
                    break;
                case TuplePattern tp when t is TupleT tt:
                    for (var i = 0; i < tp.Items.Count; i++) Destructure(tp.Items[i], tt.Items[i], $"{access}.Item{i + 1}");
                    break;
                case ListPattern { Items.Count: 0, Rest: { } rest }: Destructure(rest, t, access); break;
                default: throw new InvalidOperationException($"refutable pattern {p.GetType().Name} in a binding");
            }
        }

        private string Pattern(Syntax.Pattern p, TType scrutinee)
        {
            scrutinee = Prune(scrutinee);
            switch (p)
            {
                case WildcardPattern: return "_";
                case BindPattern b: return $"var {Id(b.Name)}";
                case LiteralPattern lit: return EmitExpr(lit.Literal, null);
                case AsPattern ap:
                {
                    // a designation may follow a positional, property, or list pattern; anything else becomes var + guard
                    var inner = Pattern(ap.Inner, scrutinee);
                    if (ap.Inner is TuplePattern or ListPattern || (ap.Inner is VariantPattern vpi && vpi.Args.Count > 0))
                        return $"{inner} {Id(ap.Name)}";
                    if (ap.Inner is BindPattern ib) _patternPrelude.Add($"var {Id(ib.Name)} = {Id(ap.Name)};");
                    else if (ap.Inner is not WildcardPattern) _patternGuards.Add($"{Id(ap.Name)} is {inner}");
                    return $"var {Id(ap.Name)}";
                }
                case TuplePattern tp when scrutinee is TupleT tt:
                    return $"({string.Join(", ", tp.Items.Select((item, i) => Pattern(item, tt.Items[i])))})";
                case ListPattern lp when scrutinee is AppT { Ctor: "Vector" } vec:
                {
                    var parts = lp.Items.Select(item => Pattern(item, vec.Args[0])).ToList();
                    if (lp.Rest is WildcardPattern) parts.Add("..");
                    else if (lp.Rest is BindPattern rb) parts.Add($".. var {Id(rb.Name)}");
                    return $"[{string.Join(", ", parts)}]";
                }
                case VariantPattern vp:
                {
                    string typeName;
                    IReadOnlyList<(string Name, TType Type)> fields;
                    switch (scrutinee)
                    {
                        case AppT { Ctor: "Option" } o:
                            typeName = $"{vp.Name}<{_e.CsType(o.Args[0])}>";
                            fields = vp.Name == "Some" ? new[] { ("value", o.Args[0]) } : Array.Empty<(string, TType)>();
                            break;
                        case AppT { Ctor: "Result" } r:
                            typeName = $"{vp.Name}<{_e.CsType(r.Args[0])}, {_e.CsType(r.Args[1])}>";
                            fields = vp.Name == "Ok" ? new[] { ("value", r.Args[0]) } : new[] { ("error", r.Args[1]) };
                            break;
                        case UnionT u:
                            typeName = vp.Name + GenericArgs(u.TypeArgs);
                            fields = u.Variant(vp.Name)?.Fields.ToList() ?? new List<(string, TType)>();
                            break;
                        case RecordT { Union: { } u2 }:
                            typeName = vp.Name + GenericArgs(u2.TypeArgs);
                            fields = u2.Variant(vp.Name)?.Fields.ToList() ?? new List<(string, TType)>();
                            break;
                        default:
                            typeName = vp.Name;
                            fields = Array.Empty<(string, TType)>();
                            break;
                    }
                    if (vp.Args.Count == 0) return typeName;
                    var parts = vp.Args.Select((a, i) => $"{Id(fields[i].Name)}: {Pattern(a, fields[i].Type)}");
                    return $"{typeName} {{ {string.Join(", ", parts)} }}";
                }
                default: throw new InvalidOperationException(p.GetType().Name);
            }
        }

        // ---- expressions

        public string EmitExpr(Expr e, TType? expected)
        {
            switch (e)
            {
                case IntLit i: return i.Text + "L";
                case FloatLit f: return f.Text;
                case StringLit s: return Quote(s.Value);
                case BoolLit b: return b.Value ? "true" : "false";
                case NameExpr n: return Id(n.Name);
                case TypeNameExpr tn:
                {
                    var t = Prune(TypeOf(tn));
                    if (t is UnionT u) return $"(({_e.CsType(u)}){tn.Name}{GenericArgs(u.TypeArgs)}.Instance)";
                    return tn.Name;
                }
                case TupleLit tl:
                {
                    var tt = Prune(TypeOf(tl)) as TupleT;
                    return $"({string.Join(", ", tl.Items.Select((x, i) => EmitExpr(x, tt?.Items[i])))})";
                }
                case ListLit l:
                {
                    var t = Prune(expected ?? TypeOf(l));
                    var elem = t is AppT { Ctor: "Vector" } a ? a.Args[0] : PrimT.Unknown;
                    if (l.Items.Count == 0) return $"Vector<{_e.CsType(elem)}>.Empty";
                    return $"vector<{_e.CsType(elem)}>({string.Join(", ", l.Items.Select(x => EmitExpr(x, elem)))})";
                }
                case MapLit ml:
                {
                    var t = Prune(expected ?? TypeOf(ml));
                    var (k, v) = t is AppT { Ctor: "Map" } a ? (a.Args[0], a.Args[1]) : (PrimT.Unknown, (TType)PrimT.Unknown);
                    var sb = new StringBuilder($"Map<{_e.CsType(k)}, {_e.CsType(v)}>.Empty");
                    foreach (var en in ml.Entries) sb.Append($".Set({EmitExpr(en.Key, k)}, {EmitExpr(en.Value, v)})");
                    return sb.ToString();
                }
                case MemberExpr mem: return Member(mem, null);
                case CallExpr c: return Call(c, expected);
                case UnaryExpr u when u.Op != "supervise":
                    return u.Op == "not" ? $"(!{EmitExpr(u.Operand, PrimT.Bool)})" : $"(-{EmitExpr(u.Operand, null)})";
                case BinaryExpr b:
                {
                    var op = b.Op switch { "and" => "&&", "or" => "||", _ => b.Op };
                    var lt = TypeOf(b.Left);
                    if (_e._checker.OrdComparisons.TryGetValue(b, out var ordParam))
                        return $"(__Ord_{ordParam}.compare({EmitExpr(b.Left, null)}, {EmitExpr(b.Right, lt)}) {op} 0)";
                    // == on a collection or a boxed value must be structural; C# operators on classes are reference equality
                    if (b.Op is "==" or "!=" && Prune(lt) is not PrimT)
                        return $"({(b.Op == "!=" ? "!" : "")}Equals({EmitExpr(b.Left, null)}, {EmitExpr(b.Right, lt)}))";
                    return $"({EmitExpr(b.Left, null)} {op} {EmitExpr(b.Right, lt)})";
                }
                case UnaryExpr { Op: "supervise" } sv:
                {
                    var bodyType = TypeOf(sv.Operand);
                    var tv = Tmp("s");
                    var pv = Tmp("p");
                    var resultType = $"Result<{_e.CsType(bodyType)}, Panic>";
                    Line($"{resultType} {tv};");
                    Line("try");
                    Line("{");
                    _indent++;
                    var body = EmitExpr(sv.Operand, bodyType);
                    Line($"{tv} = ok({body});");
                    _indent--;
                    Line("}");
                    Line($"catch (TrebPanic {pv}) {{ {tv} = error(new Panic({pv}.Message)); }}");
                    return tv;
                }
                case PropagateExpr p:
                {
                    var inner = EmitExpr(p.Inner, null);
                    var rt = Prune(TypeOf(p.Inner)) as AppT ?? throw new InvalidOperationException("? on non-Result");
                    var tv = Tmp("r");
                    var ev = Tmp("e");
                    if (rt.Ctor == "Option")
                    {
                        Line($"var {tv} = {inner};");
                        Line($"if ({tv} is None<{_e.CsType(rt.Args[0])}>) return {Coerce("None", null, _returnType)};");
                        return $"((Some<{_e.CsType(rt.Args[0])}>){tv}).value";
                    }
                    Line($"var {tv} = {inner};");
                    Line($"if ({tv} is Error<{_e.CsType(rt.Args[0])}, {_e.CsType(rt.Args[1])}> {ev}) return {Coerce($"error({ev}.error)", null, _returnType)};");
                    return $"((Ok<{_e.CsType(rt.Args[0])}, {_e.CsType(rt.Args[1])}>){tv}).value";
                }
                case LambdaExpr lam:
                {
                    var ft = Prune(TypeOf(lam)) as FnT ?? throw new InvalidOperationException("untyped lambda");
                    var ps = string.Join(", ", lam.Params.Select((p, i) => $"{_e.CsType(ft.Params[i])} {Id(p.Name)}"));
                    var inner = new FnEmitter(_e, _m, ft.Return, _indent + 1);
                    inner.EmitBlockInto(lam.Body, Target.Return, ft.Return);
                    var body = inner.Text;
                    var isAsync = _e._checker.LambdaEffects(lam)?.Contains("Suspend") == true;
                    return $"({(isAsync ? "async " : "")}({ps}) =>\n{Pad()}{{\n{body}{Pad()}}})";
                }
                case IfExpr or MatchExpr:
                {
                    var t = expected ?? TypeOf(e);
                    var tv = Tmp("v");
                    Line($"{_e.CsType(t)} {tv};");
                    EmitInto(e, Target.Assign(tv), t);
                    return tv;
                }
                case WithExpr w:
                {
                    var target = EmitExpr(w.Target, null);
                    var rt = Prune(TypeOf(w.Target)) as RecordT;
                    var result = target;
                    foreach (var f in w.Fields)
                        result = WithPath(result, rt, f.Path, 0, f.Value);
                    return result;
                }
                default:
                    throw new InvalidOperationException($"cannot emit {e.GetType().Name}");
            }
        }

        private string Pad() => new string(' ', _indent * 4);

        private string WithPath(string target, RecordT? rt, IReadOnlyList<string> path, int i, Expr value)
        {
            var field = path[i];
            var ft = rt?.Field(field);
            if (i == path.Count - 1)
                return $"({target} with {{ {Id(field)} = {EmitExpr(value, ft)} }})";
            var innerRecord = Prune(ft ?? PrimT.Unknown) as RecordT;
            var inner = WithPath($"{target}.{Id(field)}", innerRecord, path, i + 1, value);
            return $"({target} with {{ {Id(field)} = {inner} }})";
        }

        private string Member(MemberExpr mem, IReadOnlyList<string>? callArgs)
        {
            var kind = _e._checker.MemberKinds.GetValueOrDefault(mem, TypeChecker.MemberKind.Sugar);
            switch (kind)
            {
                case TypeChecker.MemberKind.Namespace:
                {
                    var ns = Prune(TypeOf(mem.Target)) as NamespaceT ?? throw new InvalidOperationException("namespace member without namespace");
                    var label = ns.Scope.Label;
                    if (_e._modules.Find(label) is not null)
                        return $"{ClassName(label)}.{Id(mem.Name)}";
                    if (label is "Cell") return "Cell." + (mem.Name == "new" ? "create" : mem.Name);
                    if (label is "Seq") return $"Seq.{mem.Name}";
                    if (label is "Instant" or "sys" or "env" or "json") return $"{label}.{mem.Name}";
                    // a union namespace: variant reference
                    return Prune(TypeOf(mem)) is UnionT u ? $"(({_e.CsType(u)}){mem.Name}{GenericArgs(u.TypeArgs)}.Instance)" : mem.Name;
                }
                case TypeChecker.MemberKind.Field when Prune(TypeOf(mem.Target)) is TupleT && int.TryParse(mem.Name, out var tupleIndex):
                    return $"{EmitExpr(mem.Target, null)}.Item{tupleIndex + 1}";
                case TypeChecker.MemberKind.Field:
                case TypeChecker.MemberKind.Method:
                case TypeChecker.MemberKind.ShapeMember:
                    return $"{EmitExpr(mem.Target, null)}.{Id(mem.Name)}";
                default:
                {
                    // sugar: name(target) when not a call; the call case prepends the receiver itself
                    var target = EmitExpr(mem.Target, null);
                    return callArgs is null ? $"{Id(mem.Name)}({target})" : Id(mem.Name);
                }
            }
        }

        private string Call(CallExpr c, TType? expected)
        {
            var callee = CalleeOf(c);
            var args = c.Args.Concat(c.BlockArgs).ToList();
            var typeArgs = c.TypeArgs.Count > 0
                ? "<" + string.Join(", ", c.TypeArgs.Select(t => _e.CsType(ResolveTypeRef(t)))) + ">"
                : "";

            // constructors
            if (callee is { Constructs: not null } || callee is { ConstructsService: not null })
            {
                var typeName = callee.Constructs?.Name ?? callee.ConstructsService!.Name;
                var returned = Prune(callee.Return);
                var targs = returned switch { RecordT rr => rr.TypeArgs, UnionT uu => uu.TypeArgs, _ => Array.Empty<TType>() };
                var argList = args.Select((a, i) => ArgText(a, callee, i)).ToList();
                var construction = $"new {typeName}{GenericArgs(targs)}({string.Join(", ", argList)})";
                return callee.Constructs is { Union: not null } && returned is UnionT ru ? $"(({_e.CsType(ru)}){construction})" : construction;
            }
            if (_e._checker.CallTypeArgs.TryGetValue(c, out var callTypeArgs) && c.TypeArgs.Count == 0)
                typeArgs = GenericArgs(callTypeArgs);

            var argTexts = new List<string>();
            string head;
            if (_e._checker.ClassCalls.TryGetValue(c, out var classCall))
            {
                // Shape.member(args) goes through the resolved instance, which is an ordinary object
                head = $"{_e.DictExpr(classCall.Shape, classCall.Type)}.{Id(classCall.Member)}";
                typeArgs = "";
            }
            else if (c.Callee is MemberExpr mem)
            {
                var kind = _e._checker.MemberKinds.GetValueOrDefault(mem, TypeChecker.MemberKind.Sugar);
                if (kind == TypeChecker.MemberKind.Sugar)
                {
                    argTexts.Add(EmitExpr(mem.Target, null));
                    head = Id(mem.Name);
                }
                else head = Member(mem, argTexts);
            }
            else if (c.Callee is NameExpr n) head = Id(n.Name);
            else head = EmitExpr(c.Callee, null);

            var offset = argTexts.Count;
            for (var i = 0; i < args.Count; i++) argTexts.Add(ArgText(args[i], callee, i + offset));

            if (head == "fail")
            {
                var t = expected is null || Prune(expected) is PrimT { Name: "Never" or "?" } ? (TType)PrimT.Unit : expected;
                return $"fail<{_e.CsType(t)}>({string.Join(", ", argTexts)})";
            }

            // Suspension: a builtin suspends when a function argument does; anything else when its effects say so.
            var suspends = false;
            if (callee is not null && _e._checker.IsBuiltin(callee.Origin ?? callee))
            {
                var fnArgSuspends = args.Select(a => a.Value).Concat(c.Callee is MemberExpr sm && _e._checker.MemberKinds.GetValueOrDefault(sm) == TypeChecker.MemberKind.Sugar ? new[] { sm.Target } : Array.Empty<Expr>())
                    .Any(ArgSuspends);
                if (fnArgSuspends) { head += "Async"; suspends = true; }
                // a builtin that suspends by declaration, such as sleep
                else if (callee.Effects is not null && callee.Effects.Contains("Suspend")) suspends = true;
            }
            else if (callee is not null)
            {
                suspends = _e.Suspends(callee);
                var poly = _e._checker.PolyParamsOf(callee);
                if (poly.Count > 0)
                {
                    var offsetPoly = argTexts.Count - args.Count; // receiver, if any, occupies parameter 0
                    var anySuspends = poly.Any(i => i - offsetPoly >= 0 && i - offsetPoly < args.Count && ArgSuspends(args[i - offsetPoly].Value));
                    if (anySuspends) { head += "Async"; suspends = true; }
                }
            }
            if (_e._checker.CallDictionaries.TryGetValue(c, out var dictionaries))
                foreach (var (shape, dictType) in dictionaries) argTexts.Add(_e.DictExpr(shape, dictType));
            var text = $"{head}{typeArgs}({string.Join(", ", argTexts)})";
            return suspends ? $"(await {text})" : text;
        }

        private bool ArgSuspends(Expr arg)
        {
            if (arg is LambdaExpr lam) return _e._checker.LambdaEffects(lam)?.Contains("Suspend") == true;
            return Prune(TypeOf(arg)) is FnT f && _e.Suspends(f);
        }

        private string ArgText(Arg a, FnT? callee, int paramIndex)
        {
            TType? paramType = callee is not null && paramIndex < callee.Params.Count ? callee.Params[paramIndex] : null;
            if (a.Name is not null && callee is not null)
            {
                var idx = callee.ParamNames.ToList().IndexOf(a.Name);
                if (idx >= 0) paramType = callee.Params[idx];
            }
            var text = EmitExpr(a.Value, paramType);
            if (paramType is not null && Prune(paramType) is AppT { Ctor: "Result" or "Option" } && a.Value is CallExpr { Callee: NameExpr { Name: "ok" or "error" or "Some" } })
                text = $"({_e.CsType(paramType)})({text})";
            return a.Name is null ? text : $"{Id(a.Name)}: {text}";
        }

        private string GenericArgs(IReadOnlyList<TType> args) => args.Count == 0 ? "" : $"<{string.Join(", ", args.Select(_e.CsType))}>";

        private TType ResolveTypeRef(TypeRef t)
        {
            var scope = _e._checker.ScopeOf(_m);
            return t switch
            {
                NamedType n when AppT.Arities.ContainsKey(AppT.Normalize(n.Name)) => new AppT(AppT.Normalize(n.Name), n.Args.Select(ResolveTypeRef).ToList()),
                NamedType n => scope.LookupType(n.Name) ?? PrimT.Unknown,
                FnType f => new FnT(f.Params.Select(ResolveTypeRef).ToList(), ResolveTypeRef(f.Return)),
                _ => PrimT.Unknown,
            };
        }
    }
}

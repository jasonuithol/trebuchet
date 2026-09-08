using System.Collections.Immutable;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;
using Trebuchet.Compiler.Syntax;

namespace Trebuchet.Compiler.Runtime;

/// <summary>
/// Tree-walking evaluator. Effects are checked elsewhere; here everything runs
/// synchronously (the "blocking" lowering from the strategy document).
/// </summary>
public sealed class Interpreter
{
    public ModuleSet Modules { get; }
    public Env Global { get; }

    /// <summary>Host implementations of extern functions, keyed by "module.name". A host process fills this before running.</summary>
    public Dictionary<string, Func<IReadOnlyList<Value>, Value>> Externs { get; } = new();
    private readonly Dictionary<Module, Env> _moduleEnvs = new();

    public Interpreter(ModuleSet modules)
    {
        Modules = modules;
        Global = Builtins.CreateGlobalEnv();

        // Pass 1: each module gets [own decls] -> [imports] -> Global.
        var importEnvs = new Dictionary<Module, Env>();
        foreach (var m in modules.Modules)
        {
            var imports = new Env(Global, $"imports of {m.Name}");
            var own = new Env(imports, m.Name);
            importEnvs[m] = imports;
            _moduleEnvs[m] = own;
            DefineDeclarations(m, own);
        }
        // Pass 2: copy imported declarations, and expose each import under its short name.
        foreach (var m in modules.Modules)
        {
            var imports = importEnvs[m];
            foreach (var imp in m.Imports)
            {
                foreach (var kv in _moduleEnvs[imp].Locals)
                    imports.Define(kv.Key, kv.Value);
                imports.Define(imp.ShortName, new NamespaceValue(imp.Name, _moduleEnvs[imp]));
            }
        }
    }

    public Env EnvOf(Module m) => _moduleEnvs[m];

    private void DefineDeclarations(Module m, Env env)
    {
        foreach (var d in m.Decls)
        {
            switch (d)
            {
                case RecordDecl r:
                    env.Define(r.Name, new ConstructorValue(r.Name, null, r.Fields, env));
                    break;
                case UnionDecl u:
                {
                    var members = new Env(null, u.Name);
                    foreach (var v in u.Variants)
                    {
                        Value val;
                        if (v.Params.Count == 0)
                            val = new RecordValue(v.Name, u.Name, ImmutableArray<string>.Empty, ImmutableArray<Value>.Empty);
                        else
                            val = new ConstructorValue(v.Name, u.Name, v.Params.Select(p => new FieldDecl(p.Pos, p.Name, p.Type, null)).ToList(), env);
                        members.Define(v.Name, val);
                        env.Define(v.Name, val);
                    }
                    env.Define(u.Name, new NamespaceValue(u.Name, members));
                    break;
                }
                case FnDecl fn:
                    env.Define(fn.Signature.Name, new Closure(fn.Signature.Name, fn.Signature.Params.Select(p => p.Name).ToList(), fn.Body, env));
                    break;
                case ExternDecl ex:
                {
                    var key = $"{m.Name}.{ex.Signature.Name}";
                    env.Define(ex.Signature.Name, new Builtin(key, (it, args) =>
                    {
                        if (!it.Externs.TryGetValue(key, out var impl))
                            throw new TrebPanic($"extern '{key}' has no implementation registered in the interpreter");
                        var returnsResult = ex.Signature.Return is NamedType { Name: "Result" };
                        try
                        {
                            var result = impl(args);
                            return returnsResult && result is not RecordValue { Union: "Result" } ? Builtins.Ok(result) : result;
                        }
                        catch (TrebPanic) { throw; }
                        catch (PropagateSignal) { throw; }
                        catch (Exception e)
                        {
                            foreach (var c in ex.Catches.Where(c => c.Target == "csharp"))
                                for (var t = e.GetType(); t is not null; t = t.BaseType)
                                    if (t.FullName == c.ExceptionType || t.Name == c.ExceptionType)
                                        return Builtins.Error(new RecordValue(c.Variant, "extern", System.Collections.Immutable.ImmutableArray.Create("message"), System.Collections.Immutable.ImmutableArray.Create<Value>(new StringValue(e.Message))));
                            throw new TrebPanic($"extern '{key}': {e.GetType().Name}: {e.Message}");
                        }
                    }));
                    break;
                }
                case ServiceDecl s:
                    env.Define(s.Name, new ServiceType(s, env));
                    break;
                case ShapeDecl:
                case RootDecl:
                    break;
            }
        }
    }

    // ------------------------------------------------------------ evaluation

    public Value Eval(Expr e, Env env)
    {
        switch (e)
        {
            case NameExpr n:
                return env.TryGet(n.Name, out var v) ? v : throw new TrebPanic($"{n.Pos}: unknown name '{n.Name}'");
            case TypeNameExpr t:
                return env.TryGet(t.Name, out var tv) ? tv : throw new TrebPanic($"{t.Pos}: unknown type or constructor '{t.Name}'");
            case IntLit i: return new IntValue(long.Parse(i.Text));
            case FloatLit f: return new FloatValue(double.Parse(f.Text, System.Globalization.CultureInfo.InvariantCulture));
            case StringLit s: return new StringValue(s.Value);
            case BoolLit b: return BoolValue.Of(b.Value);
            case ListLit l:
                return new ListValue(Vector<Value>.From(l.Items.Select(i => Eval(i, env))));
            case MapLit m:
            {
                var b = Map<Value, Value>.Empty;
                foreach (var en in m.Entries) b = b.Set(Eval(en.Key, env), Eval(en.Value, env));
                return new MapValue(b);
            }
            case MemberExpr mem:
                return Member(Eval(mem.Target, env), mem.Name, env, null, mem.Pos);
            case CallExpr c:
                return EvalCall(c, env);
            case UnaryExpr { Op: "supervise" } sv:
            {
                try { return Builtins.Ok(Eval(sv.Operand, env)); }
                catch (TrebPanic p) { return Builtins.Error(new RecordValue("Panic", null, ImmutableArray.Create("message"), ImmutableArray.Create<Value>(new StringValue(p.Message)))); }
            }
            case UnaryExpr u:
            {
                var operand = Eval(u.Operand, env);
                return u.Op switch
                {
                    "not" => operand is BoolValue bv ? BoolValue.Of(!bv.V) : throw new TrebPanic($"{u.Pos}: 'not' applied to {operand.Show()}"),
                    "-" => operand switch
                    {
                        IntValue iv => new IntValue(-iv.V),
                        FloatValue fv => new FloatValue(-fv.V),
                        _ => throw new TrebPanic($"{u.Pos}: unary '-' applied to {operand.Show()}"),
                    },
                    _ => throw new TrebPanic($"unknown unary operator {u.Op}"),
                };
            }
            case BinaryExpr bin:
                return EvalBinary(bin, env);
            case PropagateExpr p:
            {
                var inner = Eval(p.Inner, env);
                if (inner is RecordValue { Union: "Result" } r)
                {
                    if (r.TypeName == "Ok") return r.FieldValues[0];
                    throw new PropagateSignal(r);
                }
                throw new TrebPanic($"{p.Pos}: '?' applied to a non-Result value {inner.Show()}");
            }
            case LambdaExpr lam:
                return new Closure("lambda", lam.Params.Select(p => p.Name).ToList(), lam.Body, env);
            case IfExpr ifx:
            {
                var cond = Eval(ifx.Condition, env);
                if (cond is not BoolValue cb) throw new TrebPanic($"{ifx.Pos}: if condition is not a Bool: {cond.Show()}");
                if (cb.V) return ExecBlock(ifx.Then, env);
                return ifx.Else is null ? UnitValue.Instance : ExecBlock(ifx.Else, env);
            }
            case MatchExpr mx:
            {
                var scrutinee = Eval(mx.Scrutinee, env);
                foreach (var arm in mx.Arms)
                {
                    var armEnv = new Env(env, "arm");
                    if (TryMatch(arm.Pattern, scrutinee, armEnv))
                        return ExecBlock(arm.Body, armEnv);
                }
                throw new TrebPanic($"{mx.Pos}: no match arm for {scrutinee.Show()}");
            }
            case WithExpr w:
            {
                var target = Eval(w.Target, env);
                foreach (var f in w.Fields)
                    target = UpdatePath(target, f.Path, 0, Eval(f.Value, env), w.Pos);
                return target;
            }
            default:
                throw new TrebPanic($"{e.Pos}: cannot evaluate {e.GetType().Name}");
        }
    }

    private Value UpdatePath(Value target, IReadOnlyList<string> path, int i, Value value, Position pos)
    {
        if (target is not RecordValue r) throw new TrebPanic($"{pos}: 'with' on a non-record value {target.Show()}");
        var field = path[i];
        if (i == path.Count - 1) return r.With(field, value);
        var inner = r.Get(field) ?? throw new TrebPanic($"{pos}: {r.TypeName} has no field '{field}'");
        return r.With(field, UpdatePath(inner, path, i + 1, value, pos));
    }

    private Value EvalBinary(BinaryExpr b, Env env)
    {
        if (b.Op == "and")
        {
            var l = Eval(b.Left, env) as BoolValue ?? throw new TrebPanic($"{b.Pos}: 'and' on non-Bool");
            return !l.V ? BoolValue.False : Eval(b.Right, env);
        }
        if (b.Op == "or")
        {
            var l = Eval(b.Left, env) as BoolValue ?? throw new TrebPanic($"{b.Pos}: 'or' on non-Bool");
            return l.V ? BoolValue.True : Eval(b.Right, env);
        }
        var left = Eval(b.Left, env);
        var right = Eval(b.Right, env);
        return Builtins.BinaryOp(b.Op, left, right, b.Pos);
    }

    public Value ExecBlock(Block block, Env env)
    {
        var local = new Env(env, "block");
        Value last = UnitValue.Instance;
        var resources = new List<ServiceInstance>();
        try
        {
            foreach (var s in block.Stmts)
            {
                switch (s)
                {
                    case BindingStmt bind:
                        local.Define(bind.Name, Eval(bind.Value, local));
                        last = UnitValue.Instance;
                        break;
                    case UseStmt use:
                    {
                        var v = Eval(use.Value, local);
                        if (v is ServiceInstance si) resources.Add(si);
                        local.Define(use.Name, v);
                        last = UnitValue.Instance;
                        break;
                    }
                    case ExprStmt es:
                        last = Eval(es.Value, local);
                        break;
                }
            }
            return last;
        }
        finally
        {
            for (var i = resources.Count - 1; i >= 0; i--) Release(resources[i]);
        }
    }

    /// <summary>Calls a resource service's release method.</summary>
    public void Release(ServiceInstance si)
    {
        if (si.Env.TryGet("release", out var release)) Call(release, Array.Empty<Value>());
    }

    private Value EvalCall(CallExpr c, Env env)
    {
        var positional = new List<Value>();
        var named = new List<(string, Value)>();
        foreach (var a in c.Args.Concat(c.BlockArgs))
        {
            var v = Eval(a.Value, env);
            if (a.Name is null) positional.Add(v);
            else named.Add((a.Name, v));
        }
        if (c.Callee is MemberExpr mem)
            return Member(Eval(mem.Target, env), mem.Name, env, positional, mem.Pos, named);
        var callee = Eval(c.Callee, env);
        return Call(callee, positional, named, c.Pos);
    }

    /// <summary>
    /// Resolves <c>target.name</c>, and calls it with <paramref name="args"/> when non-null.
    /// Real members (namespace entries, service methods, record fields) take priority;
    /// otherwise <c>x.f(a)</c> is sugar for <c>f(x, a)</c>.
    /// </summary>
    public Value Member(Value target, string name, Env env, IReadOnlyList<Value>? args, Position pos, IReadOnlyList<(string, Value)>? named = null)
    {
        switch (target)
        {
            case NamespaceValue ns when ns.Members.TryGet(name, out var member):
                return args is null ? member : Call(member, args, named, pos);
            case ServiceInstance si when si.Env.HasLocal(name):
                si.Env.TryGet(name, out var method);
                return args is null ? method : Call(method, args, named, pos);
            case RecordValue r when r.Get(name) is { } field:
                return args is null ? field : Call(field, args, named, pos);
        }
        if (!env.TryGet(name, out var fn))
            throw new TrebPanic($"{pos}: {Describe(target)} has no member '{name}' and no function '{name}' is in scope");
        var all = new List<Value> { target };
        if (args is not null) all.AddRange(args);
        return Call(fn, all, named, pos);
    }

    private static string Describe(Value v) => v switch
    {
        RecordValue r => r.TypeName,
        ServiceInstance s => $"service {s.Type.Name}",
        NamespaceValue n => $"namespace {n.Name}",
        _ => v.GetType().Name.Replace("Value", ""),
    };

    public Value Call(Value callee, IReadOnlyList<Value> args, IReadOnlyList<(string, Value)>? named = null, Position pos = default)
    {
        switch (callee)
        {
            case Closure c:
            {
                if (named is { Count: > 0 }) throw new TrebPanic($"{pos}: named arguments are only allowed on constructors and services");
                if (c.Params.Count != args.Count)
                    throw new TrebPanic($"{pos}: {c.Name} expects {c.Params.Count} argument(s) but got {args.Count}");
                var local = new Env(c.Env, c.Name);
                for (var i = 0; i < args.Count; i++) local.Define(c.Params[i], args[i]);
                try
                {
                    return ExecBlock(c.Body, local);
                }
                catch (PropagateSignal p)
                {
                    return p.Error;
                }
            }
            case Builtin b:
                if (named is { Count: > 0 }) throw new TrebPanic($"{pos}: named arguments are not accepted by {b.Name}");
                return b.Impl(this, args);
            case ConstructorValue ctor:
                return Construct(ctor, args, named, pos);
            case ServiceType st:
                return ConstructService(st, args, named, pos);
            default:
                throw new TrebPanic($"{pos}: {callee.Show()} is not callable");
        }
    }

    public RecordValue Construct(ConstructorValue ctor, IReadOnlyList<Value> args, IReadOnlyList<(string, Value)>? named, Position pos)
    {
        var fields = ctor.Fields;
        var given = new Value?[fields.Count];
        if (args.Count > fields.Count)
            throw new TrebPanic($"{pos}: {ctor.Name} has {fields.Count} field(s) but {args.Count} argument(s) were given");
        for (var i = 0; i < args.Count; i++) given[i] = args[i];
        if (named is not null)
        {
            foreach (var (n, v) in named)
            {
                var idx = fields.ToList().FindIndex(f => f.Name == n);
                if (idx < 0) throw new TrebPanic($"{pos}: {ctor.Name} has no field '{n}'");
                given[idx] = v;
            }
        }
        var env = new Env(ctor.Env, $"init {ctor.Name}");
        var values = ImmutableArray.CreateBuilder<Value>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var v = given[i] ?? throw new TrebPanic($"{pos}: {ctor.Name} is missing field '{f.Name}'");
            if (f.Init is not null)
            {
                env.Define("value", v);
                v = Eval(f.Init, env);
            }
            env.Define(f.Name, v);
            values.Add(v);
        }
        return new RecordValue(ctor.Name, ctor.Union, fields.Select(f => f.Name).ToImmutableArray(), values.MoveToImmutable());
    }

    public ServiceInstance ConstructService(ServiceType st, IReadOnlyList<Value> args, IReadOnlyList<(string, Value)>? named, Position pos)
    {
        var deps = st.Decl.Dependencies;
        var given = new Value?[deps.Count];
        for (var i = 0; i < args.Count && i < deps.Count; i++) given[i] = args[i];
        if (named is not null)
            foreach (var (n, v) in named)
            {
                var idx = deps.ToList().FindIndex(d => d.Name == n);
                if (idx < 0) throw new TrebPanic($"{pos}: service {st.Name} has no dependency '{n}'");
                given[idx] = v;
            }
        var env = new Env(st.ModuleEnv, $"service {st.Name}");
        for (var i = 0; i < deps.Count; i++)
            env.Define(deps[i].Name, given[i] ?? throw new TrebPanic($"{pos}: service {st.Name} is missing dependency '{deps[i].Name}'"));
        var instance = new ServiceInstance(st, env);
        foreach (var m in st.Decl.Methods)
            env.Define(m.Signature.Name, new Closure($"{st.Name}.{m.Signature.Name}", m.Signature.Params.Select(p => p.Name).ToList(), m.Body, env));
        return instance;
    }

    // ------------------------------------------------------------ patterns

    private bool TryMatch(Pattern p, Value v, Env env)
    {
        switch (p)
        {
            case WildcardPattern: return true;
            case BindPattern b:
                env.Define(b.Name, v);
                return true;
            case LiteralPattern lit:
                return Eval(lit.Literal, env).Equals(v);
            case VariantPattern vp:
            {
                if (v is not RecordValue r || r.TypeName != vp.Name) return false;
                if (vp.Args.Count == 0) return true;
                if (vp.Args.Count != r.FieldValues.Length)
                    throw new TrebPanic($"{vp.Pos}: pattern {vp.Name} has {vp.Args.Count} binder(s) but the variant has {r.FieldValues.Length} field(s)");
                for (var i = 0; i < vp.Args.Count; i++)
                    if (!TryMatch(vp.Args[i], r.FieldValues[i], env)) return false;
                return true;
            }
            default:
                throw new TrebPanic($"unknown pattern {p.GetType().Name}");
        }
    }

    // ------------------------------------------------------------ composition roots

    /// <summary>Resolves every entry of a composition root, constructing services with their dependencies.</summary>
    public IReadOnlyDictionary<string, Value> Compose(Module module, RootDecl root)
    {
        var env = _moduleEnvs[module];
        var entries = root.Entries.ToDictionary(e => e.Name ?? throw new TrebPanic($"{e.Pos}: root entries must be named"), e => e.Value);
        var resolved = new Dictionary<string, Value>();
        var resolving = new HashSet<string>();

        Value Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var done)) return done;
            if (!resolving.Add(name)) throw new TrebPanic($"root {root.Name}: dependency cycle through '{name}'");
            var expr = entries[name];
            Value value;
            if (expr is TypeNameExpr t && env.TryGet(t.Name, out var tv) && tv is ServiceType st)
                value = ConstructWithDeps(st, new Dictionary<string, Value>(), expr.Pos);
            else if (expr is CallExpr { Callee: TypeNameExpr ct, HasParens: false } c && env.TryGet(ct.Name, out var cv) && cv is ServiceType cst)
            {
                var overrides = new Dictionary<string, Value>();
                foreach (var a in c.BlockArgs)
                    overrides[a.Name ?? throw new TrebPanic($"{a.Pos}: children of a service entry must be named")] = Eval(a.Value, env);
                value = ConstructWithDeps(cst, overrides, expr.Pos);
            }
            else value = Eval(expr, env);
            resolved[name] = value;
            resolving.Remove(name);
            return value;
        }

        Value ConstructWithDeps(ServiceType st, Dictionary<string, Value> overrides, Position pos)
        {
            var named = new List<(string, Value)>();
            foreach (var dep in st.Decl.Dependencies)
                named.Add((dep.Name, overrides.TryGetValue(dep.Name, out var o) ? o : ResolveDependency(dep, st, pos)));
            return ConstructService(st, Array.Empty<Value>(), named, pos);
        }

        // by entry name, then by an entry of the dependency's type, then an implicit service, then a synthesised factory
        Value ResolveDependency(Param dep, ServiceType st, Position pos)
        {
            if (entries.ContainsKey(dep.Name)) return Resolve(dep.Name);
            var typeName = (dep.Type as NamedType)?.Name;
            var byType = entries.FirstOrDefault(e => e.Value is TypeNameExpr te && te.Name == typeName);
            if (byType.Key is not null) return Resolve(byType.Key);
            if (typeName is not null && env.TryGet(typeName, out var implicitType) && implicitType is ServiceType ist)
                return ConstructWithDeps(ist, new Dictionary<string, Value>(), pos);
            if (dep.Type is FnType ft && ft.Return is NamedType rn && env.TryGet(rn.Name, out var targetType) && targetType is ServiceType target)
            {
                // synthesised factory: parameters fill the target's constructor by type, in order; the rest resolve here
                var targetDeps = target.Decl.Dependencies;
                var plan = new List<int>();
                foreach (var p in ft.Params)
                {
                    var idx = -1;
                    for (var i = 0; i < targetDeps.Count; i++)
                        if (!plan.Contains(i) && Printer.PrintType(targetDeps[i].Type) == Printer.PrintType(p)) { idx = i; break; }
                    if (idx < 0) throw new TrebPanic($"{pos}: root {root.Name} cannot synthesise '{dep.Name}' for service {st.Name}");
                    plan.Add(idx);
                }
                var fixedArgs = new List<(string, Value)>();
                for (var i = 0; i < targetDeps.Count; i++)
                    if (!plan.Contains(i)) fixedArgs.Add((targetDeps[i].Name, ResolveDependency(targetDeps[i], target, pos)));
                return new Builtin($"{st.Name}.{dep.Name}", (_, args) =>
                {
                    var all = new List<(string, Value)>(fixedArgs);
                    for (var i = 0; i < plan.Count; i++) all.Add((targetDeps[plan[i]].Name, args[i]));
                    return ConstructService(target, Array.Empty<Value>(), all, pos);
                });
            }
            throw new TrebPanic($"{pos}: root {root.Name} cannot resolve dependency '{dep.Name}: {Printer.PrintType(dep.Type)}' of service {st.Name}");
        }

        foreach (var name in entries.Keys) Resolve(name);
        return resolved;
    }
}

using Trebuchet.Compiler.Syntax;
using static Trebuchet.Compiler.Semantics.Unifier;

namespace Trebuchet.Compiler.Semantics;

/// <summary>
/// Static checker for a <see cref="ModuleSet"/>. Resolves names across modules, types
/// every expression with local inference, and enforces the rules that give the language
/// its shape: deep immutability of records, exhaustive matching, the Write boundary
/// between fn and handler, and structural satisfaction of shapes at composition roots.
/// Effects are carried on signatures but only the Write rule is enforced here; full
/// effect inference is milestone 3.
/// </summary>
public sealed class TypeChecker
{
    private readonly ModuleSet _modules;
    private readonly Scope _builtins = BuiltinSignatures.CreateBuiltinScope();
    private readonly Dictionary<Module, Scope> _own = new();
    private readonly Dictionary<Module, Scope> _imports = new();
    private readonly Dictionary<RecordDecl, RecordT> _records = new();
    private readonly Dictionary<UnionDecl, UnionT> _unions = new();
    private readonly Dictionary<ServiceDecl, ServiceT> _services = new();
    private readonly Dictionary<ShapeDecl, ShapeT> _shapes = new();
    private readonly Dictionary<FnT, ExternDecl> _externs = new(ReferenceEqualityComparer.Instance);
    public ExternDecl? ExternOf(FnT f) => _externs.GetValueOrDefault(f.Origin ?? f);
    private readonly HashSet<FnT> _builtinFns = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FnT, Frame> _frameOf = new(ReferenceEqualityComparer.Instance);
    private readonly List<Frame> _frames = new();
    private readonly List<Obligation> _obligations = new();
    private static readonly IReadOnlySet<string> AllEffects = new HashSet<string> { "Nondet", "Write", "Suspend" };

    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>Side tables for backends: the type of every expression, how members resolved, which function each call chose.</summary>
    public Dictionary<Expr, TType> ExprTypes { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<MemberExpr, MemberKind> MemberKinds { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<CallExpr, FnT> CalleeOf { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>For calls to generic user functions or constructors, the type arguments chosen (inference variables, pruned later).</summary>
    public Dictionary<CallExpr, IReadOnlyList<TType>> CallTypeArgs { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<(RootDecl, string), TType> RootEntryTypes { get; } = new();
    public Scope BuiltinScope => _builtins;
    public RecordT RecordTypeOf(RecordDecl d) => _records[d];
    public UnionT UnionTypeOf(UnionDecl d) => _unions[d];
    public ServiceT ServiceTypeOf(ServiceDecl d) => _services[d];
    public ShapeT ShapeTypeOf(ShapeDecl d) => _shapes[d];
    public Scope ScopeOf(Module m) => _own[m];
    public bool IsBuiltin(FnT f) => _builtinFns.Contains(f);

    public enum MemberKind { Namespace, Field, Method, ShapeMember, Sugar }

    /// <summary>Dictionaries a call to a constrained function must pass, in the callee's constraint order: (shape, the type the constraint resolved to).</summary>
    public Dictionary<CallExpr, IReadOnlyList<(string Shape, TType Type)>> CallDictionaries { get; } = new();
    /// <summary>A call through a shape over a type, <c>Monoid.combine(a, b)</c>: the shape, the member, and the type the parameter resolved to.</summary>
    public Dictionary<CallExpr, (string Shape, string Member, TType Type)> ClassCalls { get; } = new();
    /// <summary>A comparison on a type parameter constrained by Ord: the parameter name.</summary>
    public Dictionary<BinaryExpr, string> OrdComparisons { get; } = new();
    /// <summary>Instances declared anywhere in the program, by shape and the key of the instance's type.</summary>
    public Dictionary<(string Shape, string TypeKey), (InstanceDecl Decl, Module Module)> Instances { get; } = new();
    /// <summary>The name that identifies a type for instance lookup and emitted instance names.</summary>
    public static string TypeKey(TType t) => Prune(t) switch
    {
        PrimT p => p.Name,
        RecordT r => r.Union?.Name ?? r.Name,
        UnionT u => u.Name,
        var other => other.Show(),
    };
    private readonly Dictionary<FnT, (string Shape, string Member)> _classMembers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(InstanceDecl, string), FnT> _instanceMethods = new();
    /// <summary>For each instance method, its type with the shape member's declared effects: what the emitted method must look like.</summary>
    public Dictionary<(InstanceDecl Decl, string Member), FnT> InstanceMemberTypes { get; } = new();
    /// <summary>The resolved type each instance is for.</summary>
    public Dictionary<InstanceDecl, TType> InstanceTargets { get; } = new();
    private readonly Dictionary<ShapeT, Scope> _classScopes = new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> _deferred = new();

    /// <summary>The effects a caller is charged for calling this function: declared if present, else inferred, else builtin (pure), else all.</summary>
    public IReadOnlySet<string> EffectsOf(FnT f)
    {
        var o = f.Origin ?? f;
        if (o.Effects is not null) return o.Effects;
        if (_frameOf.TryGetValue(o, out var frame)) return frame.Solved;
        if (_builtinFns.Contains(o)) return new HashSet<string>();
        return AllEffects;
    }

    public IReadOnlySet<string>? LambdaEffects(LambdaExpr lam) => _lambdaFrames.TryGetValue(lam, out var f) ? f.Solved : null;

    public bool HasFrame(FnT f) => _frameOf.ContainsKey(f.Origin ?? f);

    /// <summary>The release method of a resource service, or null.</summary>
    public FnT? ReleaseOf(ServiceT s) => s.IsResource && s.Methods.TryGetValue("release", out var r) ? r : null;

    /// <summary>Indices of function-typed parameters whose effects the function inherits from its caller (effect polymorphism).</summary>
    public IReadOnlySet<int> PolyParamsOf(FnT f) => _frameOf.TryGetValue(f.Origin ?? f, out var frame) ? frame.PolyParams : new HashSet<int>();

    public IReadOnlySet<string> RootEffects(RootDecl root) => _rootFrames.TryGetValue(root, out var f) ? f.Solved : new HashSet<string>();

    private readonly Dictionary<RootDecl, Frame> _rootFrames = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when a service satisfies a shape structurally: every member present with a compatible type and effects.</summary>
    public bool Satisfies(ServiceT service, ShapeT shape)
    {
        foreach (var (name, member) in shape.Members)
        {
            if (!service.Methods.TryGetValue(name, out var impl)) return false;
            if (!TryUnify(impl, member)) return false;
            var implEffects = impl.Effects ?? (_frameOf.TryGetValue(impl, out var fr) ? fr.Solved : null);
            if (implEffects is not null && member.Effects is not null && !implEffects.IsSubsetOf(member.Effects)) return false;
        }
        return true;
    }

    /// <summary>Inferred effects of every named function and service method, keyed by qualified name.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> InferredEffects =>
        _frames.Where(f => f.Kind is FrameKind.Fn or FrameKind.Handler or FrameKind.Method)
            .ToDictionary(f => f.Name, f => (IReadOnlySet<string>)f.Solved);

    private enum FrameKind { Fn, Handler, Method, Lambda, Init, Root }

    /// <summary>One function body. Sites are the calls it makes; Solved is filled by the fixpoint.</summary>
    private sealed class Frame
    {
        public required string Name { get; init; }
        public required Position Pos { get; init; }
        public required FrameKind Kind { get; init; }
        public required Module Module { get; init; }
        public IReadOnlySet<string>? Declared { get; init; }
        public List<Site> Sites { get; } = new();
        public HashSet<string> Solved { get; } = new();
        /// <summary>Indices of function-typed parameters (without an effect clause) that the body calls or passes on. Callers supply their effects.</summary>
        public HashSet<int> PolyParams { get; } = new();
    }

    /// <summary>The effects an argument value contributes when the callee calls it: literal flags plus referenced frames.</summary>
    private sealed record EffectSet(IReadOnlySet<string> Flags, IReadOnlyList<Frame> Refs);

    /// <summary>
    /// One call. Flags and Refs are what the callee itself contributes. CalleeFrame (for user
    /// functions) plus FnArgs let the fixpoint add the effects of function arguments the callee
    /// calls; AllFnArgs does that for every function argument, which is the rule for builtins.
    /// </summary>
    private sealed record Site(Position Pos, string Callee, IReadOnlySet<string> Flags, IReadOnlyList<Frame> Refs,
        Frame? CalleeFrame = null, IReadOnlyDictionary<int, EffectSet>? FnArgs = null, bool AllFnArgs = false);

    /// <summary>A function value used where a function type with declared effects is expected.</summary>
    private sealed record Obligation(Frame Actual, IReadOnlySet<string> Allowed, Position Pos, string What, Module Module);

    private TypeChecker(ModuleSet modules)
    {
        _modules = modules;
        CollectBuiltins(_builtins);
    }

    private void CollectBuiltins(Scope scope)
    {
        foreach (var (_, sym) in scope.LocalValues)
        {
            switch (sym)
            {
                case ValueSym { Type: FnT f }: _builtinFns.Add(f); break;
                case ValueSym { Type: OverloadT o }: foreach (var f in o.Alternatives) _builtinFns.Add(f); break;
                case NamespaceSym ns: CollectBuiltins(ns.Scope); break;
            }
        }
    }

    public static IReadOnlyList<Diagnostic> Check(ModuleSet modules) => CheckWithEffects(modules).Diagnostics;

    public static TypeChecker CheckWithEffects(ModuleSet modules)
    {
        var c = new TypeChecker(modules);
        c.Run();
        return c;
    }

    private sealed class Ctx
    {
        public required Scope Scope { get; init; }
        public required Module Module { get; init; }
        public required Frame Frame { get; init; }
        public TType? ReturnType { get; init; }
        public string Where { get; init; } = "";
        /// <summary>The service whose method body is being checked, for private-method access.</summary>
        public ServiceT? Service { get; init; }
        /// <summary>Constraints of the enclosing function's type parameters, for class calls and comparisons on them.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Constraints { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
        /// <summary>Parameter names to indices for the enclosing named function, for effect polymorphism.</summary>
        public IReadOnlyDictionary<string, int> ParamIndex { get; init; } = new Dictionary<string, int>();
        public Ctx With(Scope scope) => new() { Scope = scope, Module = Module, Frame = Frame, ReturnType = ReturnType, Where = Where, ParamIndex = ParamIndex, Service = Service, Constraints = Constraints };
    }

    private TType Error(Ctx ctx, Position pos, string message)
    {
        Diagnostics.Add(new Diagnostic(ctx.Module.File, pos, message));
        return PrimT.Unknown;
    }

    private void Run()
    {
        foreach (var m in _modules.Modules)
        {
            var imports = new Scope(_builtins, $"imports of {m.Name}");
            var own = new Scope(imports, m.Name);
            _imports[m] = imports;
            _own[m] = own;
            DeclareTypes(m, own);
        }
        foreach (var m in _modules.Modules)
        {
            foreach (var imp in m.Imports)
            {
                // a module exports everything not marked private; private unions hide their variants too
                var view = _own[imp].ExportView(PrivateNames(imp));
                _imports[m].Includes.Add(view);
                _imports[m].DefineValue(imp.ShortName, new NamespaceSym(view));
            }
        }
        foreach (var m in _modules.Modules) ResolveSignatures(m);
        foreach (var m in _modules.Modules) CheckImmutability(m);
        foreach (var m in _modules.Modules) CheckBodies(m);
        foreach (var check in _deferred) check();
        _deferred.Clear();
        SolveEffects();
        CheckEffects();
    }

    private static IEnumerable<string> PrivateNames(Module m)
    {
        foreach (var d in m.Decls)
        {
            if (!d.IsPrivate) continue;
            switch (d)
            {
                case RecordDecl r: yield return r.Name; break;
                case UnionDecl u:
                    yield return u.Name;
                    foreach (var v in u.Variants) yield return v.Name;
                    break;
                case FnDecl f: yield return f.Signature.Name; break;
                case ServiceDecl s: yield return s.Name; break;
                case ShapeDecl h: yield return h.Name; break;
                case RootDecl root: yield return root.Name; break;
            }
        }
    }

    // ------------------------------------------------------------ pass 1: declare type shells

    private void DeclareTypes(Module m, Scope own)
    {
        foreach (var d in m.Decls)
        {
            switch (d)
            {
                case RecordDecl r:
                    if (own.LookupType(r.Name) is not null && own.LocalTypes.Any(t => t.Key == r.Name))
                        Diagnostics.Add(new Diagnostic(m.File, r.Pos, $"'{r.Name}' is declared more than once in this module"));
                    var rt = new RecordT(r.Name, r.IsEntity, r.TypeParams);
                    _records[r] = rt;
                    own.DefineType(r.Name, rt);
                    break;
                case UnionDecl u:
                {
                    var ut = new UnionT(u.Name, u.TypeParams);
                    _unions[u] = ut;
                    own.DefineType(u.Name, ut);
                    var members = new Scope(null, u.Name);
                    foreach (var v in u.Variants)
                    {
                        var vt = new RecordT(v.Name, false, u.TypeParams) { Union = ut };
                        ut.Variants.Add(vt);
                        if (v.Params.Count == 0)
                        {
                            members.DefineValue(v.Name, new ValueSym(ut, Generic: u.TypeParams.Count > 0));
                            own.DefineValue(v.Name, new ValueSym(ut, Generic: u.TypeParams.Count > 0));
                        }
                    }
                    own.DefineValue(u.Name, new NamespaceSym(members));
                    break;
                }
                case ServiceDecl s:
                    var st = new ServiceT(s.Name, s.Scoped, s.IsResource);
                    _services[s] = st;
                    own.DefineType(s.Name, st);
                    break;
                case ShapeDecl sh:
                    var ht = new ShapeT(sh.Name) { TypeParams = sh.TypeParams ?? Array.Empty<string>() };
                    _shapes[sh] = ht;
                    own.DefineType(sh.Name, ht);
                    break;
            }
        }
    }

    // ------------------------------------------------------------ pass 2: resolve signatures

    private TType ResolveType(TypeRef t, Scope scope, Module m)
    {
        switch (t)
        {
            case NamedType n:
            {
                var name = AppT.Normalize(n.Name);
                if (AppT.Arities.TryGetValue(name, out var arity))
                {
                    if (n.Args.Count != arity)
                    {
                        Diagnostics.Add(new Diagnostic(m.File, n.Pos, $"{name} takes {arity} type argument(s) but {n.Args.Count} were given"));
                        return PrimT.Unknown;
                    }
                    return new AppT(name, n.Args.Select(a => ResolveType(a, scope, m)).ToList());
                }
                var found = scope.LookupType(name);
                if (found is null)
                {
                    Diagnostics.Add(new Diagnostic(m.File, n.Pos, $"unknown type '{n.Name}'"));
                    return PrimT.Unknown;
                }
                var typeParams = found switch { RecordT r => r.TypeParams, UnionT u => u.TypeParams, _ => Array.Empty<string>() };
                if (typeParams.Count == 0 && n.Args.Count > 0)
                {
                    Diagnostics.Add(new Diagnostic(m.File, n.Pos, $"'{n.Name}' does not take type arguments"));
                    return found;
                }
                if (typeParams.Count > 0)
                {
                    if (n.Args.Count != typeParams.Count)
                    {
                        Diagnostics.Add(new Diagnostic(m.File, n.Pos, $"{n.Name} takes {typeParams.Count} type argument(s) but {n.Args.Count} were given"));
                        return PrimT.Unknown;
                    }
                    var args = n.Args.Select(a => ResolveType(a, scope, m)).ToList();
                    return found is RecordT rd ? InstantiateRecord(rd, args) : InstantiateUnion((UnionT)found, args);
                }
                return found;
            }
            case TupleType tt:
                return new TupleT(tt.Items.Select(i => ResolveType(i, scope, m)).ToList());
            case FnType f:
                return new FnT(
                    f.Params.Select(p => ResolveType(p, scope, m)).ToList(),
                    ResolveType(f.Return, scope, m),
                    f.Effects is null ? null : new HashSet<string>(f.Effects));
            default:
                throw new InvalidOperationException(t.GetType().Name);
        }
    }

    private FnT ResolveSignature(FnSignature sig, Scope scope, Module m, bool isHandler)
    {
        scope = WithTypeParams(scope, sig.TypeParams);
        var ps = sig.Params.Select(p => ResolveType(p.Type, scope, m)).ToList();
        var names = sig.Params.Select(p => (string?)p.Name).ToList();
        var ret = ResolveType(sig.Return, scope, m);
        IReadOnlySet<string>? effects = sig.Effects is null ? null : new HashSet<string>(sig.Effects);
        if (sig.TypeConstraints is { } constraints)
            foreach (var (param, shapes) in constraints)
                foreach (var shapeName in shapes)
                    if (scope.LookupType(shapeName) is not ShapeT { IsClass: true })
                        Diagnostics.Add(new Diagnostic(m.File, sig.Pos, $"'{shapeName}' in '{param}: {shapeName}' is not a shape over a type"));
        return new FnT(ps, ret, effects, names, isHandler, sig.TypeParams.ToList(), constraints: sig.TypeConstraints);
    }

    /// <summary>A child scope in which each type parameter name resolves to a rigid ParamT.</summary>
    private static Scope WithTypeParams(Scope scope, IReadOnlyList<string> typeParams)
    {
        if (typeParams.Count == 0) return scope;
        var s = new Scope(scope, "type params");
        foreach (var p in typeParams) s.DefineType(p, new ParamT(p));
        return s;
    }

    private void ResolveSignatures(Module m)
    {
        var own = _own[m];
        foreach (var d in m.Decls)
        {
            switch (d)
            {
                case RecordDecl r:
                {
                    var rt = _records[r];
                    var rscope = WithTypeParams(own, r.TypeParams);
                    foreach (var f in r.Fields) rt.Fields.Add((f.Name, ResolveType(f.Type, rscope, m)));
                    own.DefineValue(r.Name, new ValueSym(new FnT(
                        rt.Fields.Select(f => f.Type).ToList(), rt, new HashSet<string>(),
                        rt.Fields.Select(f => (string?)f.Name).ToList(), typeParams: r.TypeParams.ToList(), constructs: rt)));
                    break;
                }
                case UnionDecl u:
                {
                    var ut = _unions[u];
                    var members = ((NamespaceSym)own.LookupLocalValue(u.Name)!).Scope;
                    var uscope = WithTypeParams(own, u.TypeParams);
                    foreach (var v in u.Variants)
                    {
                        var vt = ut.Variant(v.Name)!;
                        foreach (var p in v.Params) vt.Fields.Add((p.Name, ResolveType(p.Type, uscope, m)));
                        if (v.Params.Count > 0)
                        {
                            var ctor = new ValueSym(new FnT(vt.Fields.Select(f => f.Type).ToList(), ut, new HashSet<string>(),
                                vt.Fields.Select(f => (string?)f.Name).ToList(), typeParams: u.TypeParams.ToList(), constructs: vt));
                            members.DefineValue(v.Name, ctor);
                            own.DefineValue(v.Name, ctor);
                        }
                    }
                    break;
                }
                case ExternDecl ex:
                {
                    var ft = ResolveSignature(ex.Signature, own, m, false);
                    own.DefineValue(ex.Signature.Name, new ValueSym(ft));
                    _externs[ft] = ex;
                    foreach (var c in ex.Catches)
                    {
                        if (Prune(ft.Return) is not AppT { Ctor: "Result" } res)
                        {
                            Diagnostics.Add(new Diagnostic(m.File, c.Pos, $"extern '{ex.Signature.Name}' has a catch line but does not return a Result"));
                            continue;
                        }
                        var variant = (Prune(res.Args[1]) as UnionT)?.Variant(c.Variant);
                        if (variant is null)
                            Diagnostics.Add(new Diagnostic(m.File, c.Pos, $"{Show(res.Args[1])} has no variant '{c.Variant}'"));
                        else if (variant.Fields.Count != 1 || Prune(variant.Fields[0].Type) is not PrimT { Name: "String" })
                            Diagnostics.Add(new Diagnostic(m.File, c.Pos, $"variant {c.Variant} must have exactly one String field to receive the exception message"));
                        if (c.Target is not ("csharp" or "cpp"))
                            Diagnostics.Add(new Diagnostic(m.File, c.Pos, $"unknown target '{c.Target}'; expected csharp or cpp"));
                    }
                    foreach (var b in ex.Bindings)
                        if (b.Target is not ("csharp" or "cpp"))
                            Diagnostics.Add(new Diagnostic(m.File, b.Pos, $"unknown target '{b.Target}'; expected csharp or cpp"));
                    break;
                }
                case FnDecl fn:
                {
                    var ft = ResolveSignature(fn.Signature, own, m, fn.Kind == FnKind.Handler);
                    if (fn.Kind == FnKind.Fn && ft.Effects is not null && ft.Effects.Contains("Write"))
                        Diagnostics.Add(new Diagnostic(m.File, fn.Pos, $"a fn cannot declare the Write effect; make '{fn.Signature.Name}' a handler"));
                    own.DefineValue(fn.Signature.Name, new ValueSym(ft, fn.Kind == FnKind.Handler));
                    NewFrame(ft, $"{m.Name}.{fn.Signature.Name}", fn.Pos, fn.Kind == FnKind.Handler ? FrameKind.Handler : FrameKind.Fn, m);
                    break;
                }
                case ServiceDecl s:
                {
                    var st = _services[s];
                    foreach (var dep in s.Dependencies) st.Dependencies.Add((dep.Name, ResolveType(dep.Type, own, m)));
                    foreach (var method in s.Methods)
                    {
                        if (st.Methods.ContainsKey(method.Signature.Name))
                            Diagnostics.Add(new Diagnostic(m.File, method.Pos, $"service {s.Name} declares '{method.Signature.Name}' more than once"));
                        var mt = ResolveSignature(method.Signature, own, m, false);
                        st.Methods[method.Signature.Name] = mt;
                        if (method.IsPrivate) st.PrivateMethods.Add(method.Signature.Name);
                        NewFrame(mt, $"{m.Name}.{s.Name}.{method.Signature.Name}", method.Pos, FrameKind.Method, m);
                    }
                    if (s.IsResource && (!st.Methods.TryGetValue("release", out var release) || release.Params.Count != 0 || Prune(release.Return) is not PrimT { Name: "Unit" }))
                        Diagnostics.Add(new Diagnostic(m.File, s.Pos, $"resource service {s.Name} must define 'fn release() -> Unit'"));
                    foreach (var (depName, depType) in st.Dependencies)
                        if (Prune(depType) is ServiceT { Scoped: true } dep && !s.Scoped)
                            Diagnostics.Add(new Diagnostic(m.File, s.Pos, $"service {s.Name} is a singleton but depends on scoped service {dep.Name} through '{depName}'; declare {s.Name} scoped"));
                    // Constructing a service is pure: it only captures its dependencies. Effects live in the entry expressions.
                    own.DefineValue(s.Name, new ValueSym(new FnT(
                        st.Dependencies.Select(x => x.Type).ToList(), st, new HashSet<string>(),
                        st.Dependencies.Select(x => (string?)x.Name).ToList(), constructsService: st)));
                    break;
                }
                case ShapeDecl sh:
                {
                    var ht = _shapes[sh];
                    var memberScope = ht.IsClass ? WithTypeParams(own, ht.TypeParams) : own;
                    foreach (var member in sh.Members)
                    {
                        var mt = ResolveSignature(member, memberScope, m, false);
                        if (ht.IsClass)
                        {
                            // a class member is generic in the shape's parameter, so a call instantiates it like any generic function
                            mt = new FnT(mt.Params, mt.Return, mt.Effects, mt.ParamNames, false, ht.TypeParams.ToList());
                            _classMembers[mt] = (sh.Name, member.Name);
                        }
                        ht.Members[member.Name] = mt;
                    }
                    break;
                }
                case InstanceDecl inst:
                {
                    if (own.LookupType(inst.Shape) is not ShapeT { IsClass: true } shape)
                    {
                        Diagnostics.Add(new Diagnostic(m.File, inst.Pos, $"'{inst.Shape}' is not a shape over a type; an instance needs 'shape {inst.Shape}[T]'"));
                        break;
                    }
                    var target = ResolveType(inst.Target, own, m);
                    var key = TypeKey(target);
                    if (Prune(target) is RecordT { TypeArgs.Count: > 0 } or UnionT { TypeArgs.Count: > 0 } or AppT or TupleT or FnT)
                        Diagnostics.Add(new Diagnostic(m.File, inst.Target.Pos, $"an instance must be for a record, union, or primitive type, not {Show(target)}"));
                    if (Instances.ContainsKey((inst.Shape, key)))
                        Diagnostics.Add(new Diagnostic(m.File, inst.Pos, $"{inst.Shape}[{key}] already has an instance"));
                    Instances[(inst.Shape, key)] = (inst, m);
                    InstanceTargets[inst] = target;
                    var subst = new Dictionary<string, TType> { [shape.TypeParams[0]] = target };
                    var seen = new HashSet<string>();
                    foreach (var method in inst.Methods)
                    {
                        var name = method.Signature.Name;
                        if (!seen.Add(name)) Diagnostics.Add(new Diagnostic(m.File, method.Pos, $"instance {inst.Shape}[{key}] defines '{name}' twice"));
                        var mt = ResolveSignature(method.Signature, own, m, method.Kind == FnKind.Handler);
                        _instanceMethods[(inst, name)] = mt;
                        var frame = NewFrame(mt, $"{m.Name}.{inst.Shape}[{key}].{name}", method.Pos, FrameKind.Fn, m);
                        if (!shape.Members.TryGetValue(name, out var expected))
                        {
                            Diagnostics.Add(new Diagnostic(m.File, method.Pos, $"shape {inst.Shape} has no member '{name}'"));
                            continue;
                        }
                        var want = Subst(new FnT(expected.Params, expected.Return, expected.Effects, expected.ParamNames), subst);
                        if (!TryUnify(new FnT(mt.Params, mt.Return), want))
                            Diagnostics.Add(new Diagnostic(m.File, method.Pos, $"'{name}' in instance {inst.Shape}[{key}] has type {Show(mt)} but the shape needs {Show(want)}"));
                        if (expected.Effects is { } allowed)
                            _obligations.Add(new Obligation(frame, allowed, method.Pos, $"'{name}' in instance {inst.Shape}[{key}]", m));
                        InstanceMemberTypes[(inst, name)] = new FnT(mt.Params, mt.Return, expected.Effects, mt.ParamNames);
                    }
                    foreach (var (name, _) in shape.Members)
                        if (!seen.Contains(name)) Diagnostics.Add(new Diagnostic(m.File, inst.Pos, $"instance {inst.Shape}[{key}] is missing '{name}'"));
                    break;
                }
            }
        }
    }

    private Frame NewFrame(FnT fn, string name, Position pos, FrameKind kind, Module m)
    {
        var frame = new Frame { Name = name, Pos = pos, Kind = kind, Module = m, Declared = fn.Effects };
        _frameOf[fn] = frame;
        _frames.Add(frame);
        return frame;
    }

    // ------------------------------------------------------------ pass 3: immutability

    private void CheckImmutability(Module m)
    {
        foreach (var d in m.Decls)
        {
            if (d is RecordDecl { IsEntity: false } r)
            {
                var rt = _records[r];
                foreach (var f in r.Fields)
                {
                    var ft = rt.Field(f.Name)!;
                    if (ContainsMutable(ft, new HashSet<TType>()))
                        Diagnostics.Add(new Diagnostic(m.File, f.Pos,
                            $"record {r.Name} is immutable but field '{f.Name}' has type {Show(ft)}, which contains mutable state; declare {r.Name} as an entity or remove the field"));
                }
            }
            if (d is UnionDecl u)
            {
                var ut = _unions[u];
                foreach (var v in u.Variants)
                    foreach (var p in v.Params)
                    {
                        var ft = ut.Variant(v.Name)!.Field(p.Name)!;
                        if (ContainsMutable(ft, new HashSet<TType>()))
                            Diagnostics.Add(new Diagnostic(m.File, p.Pos,
                                $"union {u.Name} is immutable but field '{p.Name}' of variant {v.Name} has type {Show(ft)}, which contains mutable state"));
                    }
            }
        }
    }

    private static bool ContainsMutable(TType t, HashSet<TType> visited)
    {
        t = Prune(t);
        // visit by definition: instantiations are fresh objects, so a recursive generic would never repeat
        var key = t switch { RecordT r => (TType?)(r.Definition ?? r), UnionT u => u.Definition ?? u, _ => t };
        if (!visited.Add(key!)) return false;
        return t switch
        {
            RecordT r => r.IsEntity || r.TypeArgs.Any(a => ContainsMutable(a, visited)) || (r.Definition ?? r).Fields.Any(f => ContainsMutable(f.Type, visited)),
            UnionT u => u.TypeArgs.Any(a => ContainsMutable(a, visited)) || (u.Definition ?? u).Variants.Any(v => ContainsMutable(v, visited)),
            AppT { Ctor: "Cell" } => true,
            ServiceT { IsResource: true } => true,
            AppT a => a.Args.Any(x => ContainsMutable(x, visited)),
            TupleT tt => tt.Items.Any(x => ContainsMutable(x, visited)),
            _ => false,
        };
    }

    // ------------------------------------------------------------ pass 4: bodies

    private void CheckBodies(Module m)
    {
        var own = _own[m];
        foreach (var d in m.Decls)
        {
            switch (d)
            {
                case RecordDecl r:
                    CheckFieldInits(r, _records[r], own, m);
                    break;
                case FnDecl fn:
                    CheckFunction(fn, ((ValueSym)own.LookupLocalValue(fn.Signature.Name)!).Type as FnT ?? throw new InvalidOperationException(), new Scope(WithTypeParams(own, fn.Signature.TypeParams), fn.Signature.Name), m);
                    break;
                case ServiceDecl s:
                {
                    var st = _services[s];
                    var serviceScope = new Scope(own, $"service {s.Name}");
                    foreach (var dep in st.Dependencies) serviceScope.DefineValue(dep.Name, new ValueSym(dep.Type));
                    foreach (var (name, ft) in st.Methods) serviceScope.DefineValue(name, new ValueSym(ft));
                    foreach (var method in s.Methods)
                        CheckFunction(method, st.Methods[method.Signature.Name], new Scope(serviceScope, method.Signature.Name), m, st);
                    break;
                }
                case InstanceDecl inst:
                    foreach (var method in inst.Methods)
                        if (_instanceMethods.TryGetValue((inst, method.Signature.Name), out var mt))
                            CheckFunction(method, mt, new Scope(own, $"instance {inst.Shape} {method.Signature.Name}"), m);
                    break;
                case RootDecl root:
                    CheckRoot(root, own, m);
                    break;
            }
        }
    }

    private void CheckFieldInits(RecordDecl r, RecordT rt, Scope own, Module m)
    {
        var scope = new Scope(WithTypeParams(own, r.TypeParams), $"init {r.Name}");
        foreach (var f in r.Fields)
        {
            var ft = rt.Field(f.Name)!;
            if (f.Init is not null)
            {
                var initScope = new Scope(scope, "init");
                initScope.DefineValue("value", new ValueSym(ft));
                var frame = new Frame { Name = $"init of {r.Name}.{f.Name}", Pos = f.Pos, Kind = FrameKind.Init, Module = m, Declared = new HashSet<string>() };
                _frames.Add(frame);
                var ctx = new Ctx { Scope = initScope, Module = m, Frame = frame, ReturnType = ft, Where = $"init of {r.Name}.{f.Name}" };
                Check(f.Init, ctx, ft);
            }
            scope.DefineValue(f.Name, new ValueSym(ft));
        }
    }

    private void CheckFunction(FnDecl fn, FnT type, Scope scope, Module m, ServiceT? service = null)
    {
        for (var i = 0; i < fn.Signature.Params.Count; i++)
            scope.DefineValue(fn.Signature.Params[i].Name, new ValueSym(type.Params[i]));
        var paramIndex = fn.Signature.Params.Select((p, i) => (p.Name, i)).ToDictionary(x => x.Name, x => x.i);
        var ctx = new Ctx { Scope = scope, Module = m, Frame = _frameOf[type], ReturnType = type.Return, Where = fn.Signature.Name, ParamIndex = paramIndex, Service = service, Constraints = type.Constraints };
        var bodyType = Block(fn.Body, ctx, type.Return);
        if (!Unify(bodyType, type.Return))
            Error(ctx, fn.Body.Pos, $"{fn.Signature.Name} is declared to return {Show(type.Return)} but its body has type {Show(bodyType)}");
    }

    // ------------------------------------------------------------ statements

    private TType Block(Block block, Ctx outer, TType? expected)
    {
        var ctx = outer.With(new Scope(outer.Scope, "block"));
        TType last = PrimT.Unit;
        for (var i = 0; i < block.Stmts.Count; i++)
        {
            var s = block.Stmts[i];
            var isLast = i == block.Stmts.Count - 1;
            switch (s)
            {
                case BindingStmt b:
                {
                    var t = Infer(b.Value, ctx, null);
                    if (Prune(t) is ServiceT { IsResource: true } res && b.Value is not NameExpr)
                        Error(ctx, b.Pos, $"'{b.Name}' is a fresh {res.Name}, which is a resource; bind it with 'use {b.Name} = ...' so it is released, or return it");
                    ctx.Scope.DefineValue(b.Name, new ValueSym(t));
                    last = PrimT.Unit;
                    if (isLast) Error(ctx, b.Pos, $"a block cannot end with a binding; '{b.Name}' is never used");
                    break;
                }
                case DestructureStmt ds:
                {
                    var t = Infer(ds.Value, ctx, null);
                    if (!Pattern(ds.Pattern, t, VariantsOf(Prune(t)), ctx.Scope, ctx, new()))
                        Error(ctx, ds.Pos, "this pattern can fail to match, so it cannot be a binding; use match, or a pattern that matches every value");
                    last = PrimT.Unit;
                    if (isLast) Error(ctx, ds.Pos, "a block cannot end with a binding");
                    break;
                }
                case UseStmt u:
                {
                    var t = Infer(u.Value, ctx, null);
                    if (Prune(t) is not ServiceT { IsResource: true } res)
                        Error(ctx, u.Pos, $"'use' needs a resource service but '{u.Name}' has type {Show(t)}");
                    else
                    {
                        if (u.Value is NameExpr) Error(ctx, u.Pos, $"'use' must bind a fresh resource; '{u.Name}' would release a value owned elsewhere");
                        // releasing at block end is a call the enclosing function makes
                        if (res.Methods.TryGetValue("release", out var release))
                            RecordCall(ctx, u.Pos, $"release of {u.Name}", release, release, Array.Empty<Expr?>(), Array.Empty<TType?>(), null, null);
                    }
                    ctx.Scope.DefineValue(u.Name, new ValueSym(t));
                    last = PrimT.Unit;
                    if (isLast) Error(ctx, u.Pos, $"a block cannot end with a binding; '{u.Name}' is never used");
                    break;
                }
                case ExprStmt e:
                    last = isLast ? Check(e.Value, ctx, expected) : Infer(e.Value, ctx, null);
                    break;
            }
        }
        return last;
    }

    // ------------------------------------------------------------ expressions

    /// <summary>Type an expression and require it to match <paramref name="expected"/> when given.</summary>
    private TType Check(Expr e, Ctx ctx, TType? expected)
    {
        var t = Infer(e, ctx, expected);
        if (expected is not null && !Unify(t, expected))
            return Error(ctx, e.Pos, $"expected {Show(expected)} but found {Show(t)}");
        return t;
    }

    /// <summary>Type an expression. <paramref name="hint"/> is only a hint for literals and lambdas.</summary>
    private TType Infer(Expr e, Ctx ctx, TType? hint)
    {
        var t = InferCore(e, ctx, hint);
        ExprTypes[e] = t;
        return t;
    }

    private TType InferCore(Expr e, Ctx ctx, TType? hint)
    {
        switch (e)
        {
            case TupleLit tl:
            {
                var hints = Prune(hint ?? PrimT.Unknown) is TupleT ht && ht.Items.Count == tl.Items.Count ? ht.Items : null;
                return new TupleT(tl.Items.Select((item, i) => Infer(item, ctx, hints?[i])).ToList());
            }
            case NameExpr n:
                return ValueOf(n.Name, n.Pos, ctx);
            case TypeNameExpr tn:
            {
                var sym = ctx.Scope.LookupValue(tn.Name);
                if (sym is null && ctx.Scope.LookupType(tn.Name) is ShapeT { IsClass: true } cls)
                    return new NamespaceT(tn.Name, ClassScope(cls));
                if (sym is null)
                {
                    if (ctx.Scope.LookupType(tn.Name) is { } asType)
                        return Error(ctx, tn.Pos, $"'{tn.Name}' is a {Kind(asType)}, not a value" + (asType is UnionT ? "; use a variant such as " + tn.Name + ".Variant" : ""));
                    return Error(ctx, tn.Pos, $"unknown name '{tn.Name}'");
                }
                return SymbolType(sym, tn.Name);
            }
            case IntLit: return PrimT.Int;
            case FloatLit: return PrimT.Float;
            case StringLit: return PrimT.String;
            case BoolLit: return PrimT.Bool;
            case ListLit l:
            {
                var elem = Prune(hint ?? PrimT.Unknown) is AppT { Ctor: "Vector" } a ? a.Args[0] : new VarT("elem");
                foreach (var item in l.Items) Check(item, ctx, elem);
                return new AppT("Vector", new[] { elem });
            }
            case MapLit ml:
            {
                TType k = new VarT("k"), v = new VarT("v");
                if (Prune(hint ?? PrimT.Unknown) is AppT { Ctor: "Map" } ma) { k = ma.Args[0]; v = ma.Args[1]; }
                foreach (var en in ml.Entries)
                {
                    Check(en.Key, ctx, k);
                    Check(en.Value, ctx, v);
                }
                return new AppT("Map", new[] { k, v });
            }
            case MemberExpr mem:
            {
                var (calleeType, receiver) = Member(mem, ctx);
                return receiver is null ? calleeType : Apply(calleeType, receiver, Array.Empty<Arg>(), Array.Empty<TypeRef>(), ctx, hint, mem.Pos, mem.Name);
            }
            case CallExpr c:
                return Call(c, ctx, hint);
            case UnaryExpr u:
            {
                if (u.Op == "supervise")
                {
                    // a supervisor point: only handler dispatch and service methods may observe a panic
                    if (ctx.Frame.Kind is not (FrameKind.Handler or FrameKind.Method))
                        Error(ctx, u.Pos, "supervise is only allowed in a handler or a service method; a fn cannot observe a panic");
                    var body = Infer(u.Operand, ctx, null);
                    return new AppT("Result", new[] { body, (TType)BuiltinSignatures.Panic });
                }
                var operand = Prune(Infer(u.Operand, ctx, null));
                if (u.Op == "not")
                    return Unify(operand, PrimT.Bool) ? PrimT.Bool : Error(ctx, u.Pos, $"'not' needs a Bool but found {Show(operand)}");
                if (operand is VarT) Unify(operand, PrimT.Int);
                if (Prune(operand) is PrimT { Name: "Int" or "Float" or "?" }) return Prune(operand);
                return Error(ctx, u.Pos, $"unary '-' needs a number but found {Show(operand)}");
            }
            case BinaryExpr b:
                return Binary(b, ctx);
            case PropagateExpr p:
            {
                var inner = Prune(Infer(p.Inner, ctx, null));
                if (inner is PrimT { Name: "?" }) return PrimT.Unknown;
                if (inner is not AppT { Ctor: "Result" } res)
                    return Error(ctx, p.Pos, $"'?' needs a Result but found {Show(inner)}");
                if (ctx.ReturnType is null)
                    return Error(ctx, p.Pos, "'?' can only be used inside a function");
                var ret = Prune(ctx.ReturnType);
                if (ret is VarT rv) Unify(rv, new AppT("Result", new TType[] { new VarT("t"), res.Args[1] }));
                ret = Prune(ctx.ReturnType);
                if (ret is not AppT { Ctor: "Result" } retRes)
                    return Error(ctx, p.Pos, $"'?' is used in {ctx.Where}, which returns {Show(ret)} rather than a Result");
                if (!Unify(res.Args[1], retRes.Args[1]))
                    return Error(ctx, p.Pos, $"'?' would propagate an error of type {Show(res.Args[1])} but {ctx.Where} returns errors of type {Show(retRes.Args[1])}");
                return res.Args[0];
            }
            case LambdaExpr lam:
                return Lambda(lam, ctx, Prune(hint ?? PrimT.Unknown) as FnT);
            case IfExpr ifx:
            {
                Check(ifx.Condition, ctx, PrimT.Bool);
                var thenT = Block(ifx.Then, ctx, hint);
                if (ifx.Else is null)
                {
                    if (!Unify(thenT, PrimT.Unit))
                        Error(ctx, ifx.Pos, $"an 'if' without 'else' must have type Unit, but the branch has type {Show(thenT)}");
                    return PrimT.Unit;
                }
                var elseT = Block(ifx.Else, ctx, hint ?? thenT);
                if (!Unify(thenT, elseT))
                    return Error(ctx, ifx.Pos, $"the branches of 'if' have different types: {Show(thenT)} and {Show(elseT)}");
                return Prune(thenT);
            }
            case MatchExpr mx:
                return Match(mx, ctx, hint);
            case WithExpr w:
                return With(w, ctx);
            default:
                return Error(ctx, e.Pos, $"cannot type {e.GetType().Name}");
        }
    }

    private static string Kind(TType t) => t switch
    {
        RecordT { IsEntity: true } => "entity",
        RecordT => "record",
        UnionT => "union",
        ServiceT => "service",
        ShapeT => "shape",
        _ => "type",
    };

    private TType ValueOf(string name, Position pos, Ctx ctx)
    {
        var sym = ctx.Scope.LookupValue(name);
        return sym is null ? Error(ctx, pos, $"unknown name '{name}'") : SymbolType(sym, name);
    }

    private static TType SymbolType(Symbol sym, string name) => sym switch
    {
        ValueSym v => v.Generic ? Fresh(v.Type) : v.Type,
        NamespaceSym n => new NamespaceT(name, n.Scope),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// Resolves <c>target.name</c>. Returns the member's type and, when the member is
    /// really <c>name(target, ...)</c> sugar, the receiver type to prepend.
    /// </summary>
    private (TType type, TType? receiver) Member(MemberExpr mem, Ctx ctx)
    {
        var targetType = Prune(Infer(mem.Target, ctx, null));
        switch (targetType)
        {
            case NamespaceT ns:
            {
                var sym = ns.Scope.LookupLocalValue(mem.Name);
                if (sym is null) return (Error(ctx, mem.Pos, $"{ns.Name} has no member '{mem.Name}'"), null);
                MemberKinds[mem] = MemberKind.Namespace;
                return (SymbolType(sym, mem.Name), null);
            }
            case RecordT r when r.Field(mem.Name) is { } field:
                MemberKinds[mem] = MemberKind.Field;
                return (field, null);
            case TupleT tt when int.TryParse(mem.Name, out var index):
                MemberKinds[mem] = MemberKind.Field;
                if (index < 0 || index >= tt.Items.Count) return (Error(ctx, mem.Pos, $"tuple {Show(tt)} has no item {index}"), null);
                return (tt.Items[index], null);
            case ServiceT s when s.Methods.TryGetValue(mem.Name, out var method):
                MemberKinds[mem] = MemberKind.Method;
                if (s.PrivateMethods.Contains(mem.Name) && ctx.Service?.Name != s.Name)
                    Error(ctx, mem.Pos, $"'{mem.Name}' is a private method of service {s.Name}");
                return (method, null);
            case ShapeT h when h.Members.TryGetValue(mem.Name, out var member):
                MemberKinds[mem] = MemberKind.ShapeMember;
                return (member, null);
            case PrimT { Name: "?" }:
                return (PrimT.Unknown, null);
        }
        var fn = ctx.Scope.LookupValue(mem.Name);
        if (fn is ValueSym { Type: FnT or OverloadT } vs) { MemberKinds[mem] = MemberKind.Sugar; return (vs.Type, targetType); }
        var what = targetType is VarT ? "a value of unknown type" : Show(targetType);
        return (Error(ctx, mem.Pos, $"{what} has no member '{mem.Name}' and no function '{mem.Name}' is in scope"), null);
    }

    private TType Call(CallExpr c, Ctx ctx, TType? hint)
    {
        var args = (IReadOnlyList<Arg>)c.Args.Concat(c.BlockArgs).ToList();
        TType calleeType;
        TType? receiver = null;
        string name;
        if (c.Callee is MemberExpr mem)
        {
            (calleeType, receiver) = Member(mem, ctx);
            name = mem.Name;
        }
        else
        {
            calleeType = Infer(c.Callee, ctx, null);
            name = c.Callee is NameExpr n ? n.Name : c.Callee is TypeNameExpr t ? t.Name : "expression";
        }
        var result = Apply(calleeType, receiver, args, c.TypeArgs, ctx, hint, c.Pos, name, c);
        return result;
    }

    private TType Apply(TType calleeType, TType? receiver, IReadOnlyList<Arg> argList, IReadOnlyList<TypeRef> typeArgs, Ctx ctx, TType? hint, Position pos, string name, CallExpr? call = null)
    {
        var args = argList.ToList();
        calleeType = Prune(calleeType);
        if (calleeType is PrimT { Name: "?" })
        {
            foreach (var a in args) Infer(a.Value, ctx, null);
            return PrimT.Unknown;
        }
        var alternatives = calleeType switch
        {
            FnT f => new[] { f },
            OverloadT o => o.Alternatives.ToArray(),
            _ => null,
        };
        if (alternatives is null)
            return Error(ctx, pos, $"'{name}' is {Show(calleeType)}, which is not callable");

        var total = args.Count + (receiver is null ? 0 : 1);
        var named = args.Where(a => a.Name is not null).ToList();
        var positionalArgs = args.Where(a => a.Name is null).ToList();

        // Infer the non-lambda arguments once, so overloads can be chosen by their types.
        var argTypes = new TType?[args.Count];
        for (var i = 0; i < args.Count; i++)
            if (args[i].Value is not LambdaExpr) argTypes[i] = Infer(args[i].Value, ctx, null);

        var explicitTypeArgs = typeArgs.Select(t => ResolveType(t, ctx.Scope, ctx.Module)).ToList();

        FnT? chosen = null;
        FnT? chosenOriginal = null;
        foreach (var alt in alternatives)
        {
            if (alt.Params.Count != total && !(named.Count > 0 && alt.Constructs is not null || alt.ConstructsService is not null)) continue;
            var inst = InstantiateWith(alt, explicitTypeArgs, ctx, pos);
            var instTypeArgs = _lastTypeArgs;
            if (alternatives.Length == 1) { chosen = inst; chosenOriginal = alt; _lastTypeArgs = instTypeArgs; break; }
            // trial: receiver and inferred positional args must fit
            var ok = true;
            var offset = receiver is null ? 0 : 1;
            if (receiver is not null && !TryUnify(receiver, inst.Params[0])) ok = false;
            for (var i = 0; ok && i < positionalArgs.Count; i++)
            {
                var idx = args.IndexOf(positionalArgs[i]);
                if (argTypes[idx] is { } at && i + offset < inst.Params.Count && !TryUnify(at, inst.Params[i + offset])) ok = false;
            }
            if (ok) { chosen = inst; chosenOriginal = alt; _lastTypeArgs = instTypeArgs; break; }
        }
        if (chosen is null)
        {
            var arities = string.Join(" or ", alternatives.Select(a => a.Params.Count - (receiver is null ? 0 : 1)).Distinct());
            if (alternatives.All(a => a.Params.Count != total))
                return Error(ctx, pos, $"'{name}' takes {arities} argument(s) but {args.Count} were given");
            var given = string.Join(", ", (receiver is null ? Array.Empty<TType>() : new[] { receiver }).Concat(argTypes.Where(t => t is not null)!).Select(t => Show(t!)));
            return Error(ctx, pos, $"no overload of '{name}' accepts ({given})");
        }

        var chosenAlt = chosenOriginal!;
        var chosenTypeArgs = _lastTypeArgs;

        // Map arguments to parameters, honouring named arguments on constructors.
        var slots = new Expr?[chosen.Params.Count];
        var slotTypes = new TType?[chosen.Params.Count];
        var next = 0;
        if (receiver is not null)
        {
            if (!Unify(receiver, chosen.Params[0]))
                return Error(ctx, pos, $"'{name}' expects {Show(chosen.Params[0])} as its receiver but found {Show(receiver)}");
            next = 1;
        }
        foreach (var a in args)
        {
            int idx;
            if (a.Name is not null)
            {
                if (chosen.Constructs is null && chosen.ConstructsService is null)
                    return Error(ctx, a.Pos, $"named arguments are only allowed when constructing a record or service, not when calling '{name}'");
                idx = chosen.ParamNames.ToList().IndexOf(a.Name);
                if (idx < 0) return Error(ctx, a.Pos, $"{name} has no field or dependency named '{a.Name}'");
            }
            else idx = next++;
            if (idx >= chosen.Params.Count) return Error(ctx, a.Pos, $"too many arguments to '{name}'");
            if (slots[idx] is not null) return Error(ctx, a.Pos, $"argument '{chosen.ParamNames[idx]}' of {name} is given twice");
            slots[idx] = a.Value;
            slotTypes[idx] = argTypes[args.IndexOf(a)];
        }
        for (var i = 0; i < chosen.Params.Count; i++)
        {
            if (i == 0 && receiver is not null) continue;
            if (slots[i] is null)
            {
                if (chosen.Constructs is not null || chosen.ConstructsService is not null)
                    return Error(ctx, pos, $"{name} is missing '{chosen.ParamNames[i]}'");
                return Error(ctx, pos, $"'{name}' takes {chosen.Params.Count} argument(s) but {args.Count} were given");
            }
            var expr = slots[i]!;
            var paramType = chosen.Params[i];
            if (expr is LambdaExpr)
            {
                var lt = Infer(expr, ctx, paramType);
                if (!Unify(lt, paramType))
                    Error(ctx, expr.Pos, $"argument '{chosen.ParamNames[i] ?? (i + 1).ToString()}' of {name}: expected {Show(paramType)} but found {Show(lt)}");
            }
            else if (!Unify(slotTypes[i]!, paramType))
                Error(ctx, expr.Pos, $"argument '{chosen.ParamNames[i] ?? (i + 1).ToString()}' of {name}: expected {Show(paramType)} but found {Show(slotTypes[i]!)}");
            else if (Prune(paramType) is FnT { Effects: { } allowed } && Prune(slotTypes[i]!) is FnT given && _frameOf.TryGetValue(given, out var givenFrame))
                _obligations.Add(new Obligation(givenFrame, allowed, expr.Pos, $"argument '{chosen.ParamNames[i] ?? (i + 1).ToString()}' of {name}", ctx.Module));
        }
        RecordCall(ctx, pos, name, chosenAlt, chosen, slots, slotTypes, receiver, call?.Callee);
        if (call is not null)
        {
            CalleeOf[call] = chosen;
            if (chosenTypeArgs is not null && !_builtinFns.Contains(chosenAlt)) CallTypeArgs[call] = chosenTypeArgs;
            if (chosenTypeArgs is not null && chosenOriginal is not null)
            {
                // resolved after every body is checked, when inference variables have their final bindings
                var original = chosenOriginal;
                var targs = chosenTypeArgs;
                var callSite = call;
                var callCtx = ctx;
                if (_classMembers.TryGetValue(original, out var cm))
                    _deferred.Add(() =>
                    {
                        var t = Prune(targs[0]);
                        if (CheckConstraint(cm.Shape, t, callCtx, pos, $"{cm.Shape}.{cm.Member}")) ClassCalls[callSite] = (cm.Shape, cm.Member, t);
                    });
                else if (original.Constraints.Count > 0)
                    _deferred.Add(() =>
                    {
                        var dicts = new List<(string, TType)>();
                        for (var i = 0; i < original.TypeParams.Count; i++)
                        {
                            if (!original.Constraints.TryGetValue(original.TypeParams[i], out var shapes)) continue;
                            var t = Prune(targs[i]);
                            foreach (var shapeName in shapes)
                                if (CheckConstraint(shapeName, t, callCtx, pos, $"'{name}'")) dicts.Add((shapeName, t));
                        }
                        CallDictionaries[callSite] = dicts;
                    });
            }
        }
        var result = chosen.Return;
        if (hint is not null) Unify(result, hint);
        return result;
    }

    /// <summary>Records the effects a call contributes to the enclosing frame, with provenance.</summary>
    private void RecordCall(Ctx ctx, Position pos, string name, FnT original, FnT chosen, Expr?[] slots, TType?[] slotTypes, TType? receiver, Expr? calleeExpr)
    {
        var flags = new HashSet<string>();
        var refs = new List<Frame>();
        Frame? calleeFrame = null;

        // Calling one of our own function-typed parameters: the caller supplies its effects.
        if (calleeExpr is NameExpr pn && original.Effects is null && !_frameOf.ContainsKey(original) && !_builtinFns.Contains(original)
            && ctx.ParamIndex.TryGetValue(pn.Name, out var polyIndex))
        {
            ctx.Frame.PolyParams.Add(polyIndex);
        }
        // Declared effects are the contract; only undeclared functions are charged their inferred body effects.
        else if (original.Effects is not null) flags.UnionWith(original.Effects);
        else if (_frameOf.TryGetValue(original, out var frame)) { refs.Add(frame); calleeFrame = frame; }
        else if (!_builtinFns.Contains(original)) flags.UnionWith(AllEffects); // a function value whose type gives no effects

        // Function-typed arguments: what each would contribute if the callee calls it.
        var fnArgs = new Dictionary<int, EffectSet>();
        for (var i = 0; i < chosen.Params.Count; i++)
        {
            if (Prune(chosen.Params[i]) is not FnT) continue;
            var argFlags = new HashSet<string>();
            var argRefs = new List<Frame>();
            if (i == 0 && receiver is not null) AddFnValueEffects(receiver, null, argFlags, argRefs, ctx);
            else if (slots[i] is LambdaExpr lam && _lambdaFrames.TryGetValue(lam, out var lf)) argRefs.Add(lf);
            else if (slots[i] is NameExpr an && ctx.ParamIndex.TryGetValue(an.Name, out var ownIndex) && Prune(slotTypes[i] ?? PrimT.Unknown) is FnT { Effects: null } af && !_frameOf.ContainsKey(af))
                ctx.Frame.PolyParams.Add(ownIndex); // passing our own parameter on: still the caller's responsibility
            else if (slotTypes[i] is { } st) AddFnValueEffects(st, slots[i], argFlags, argRefs, ctx);
            fnArgs[i] = new EffectSet(argFlags, argRefs);
        }
        ctx.Frame.Sites.Add(new Site(pos, name, flags, refs, calleeFrame, fnArgs, AllFnArgs: _builtinFns.Contains(original)));
    }

    private void AddFnValueEffects(TType t, Expr? expr, HashSet<string> flags, List<Frame> refs, Ctx ctx)
    {
        if (Prune(t) is not FnT f) return;
        if (f.Effects is not null) flags.UnionWith(f.Effects);
        else if (_frameOf.TryGetValue(f, out var fr)) refs.Add(fr);
        else flags.UnionWith(AllEffects);
    }

    private readonly Dictionary<LambdaExpr, Frame> _lambdaFrames = new(ReferenceEqualityComparer.Instance);

    private IReadOnlyList<TType>? _lastTypeArgs;

    private FnT InstantiateWith(FnT f, IReadOnlyList<TType> explicitTypeArgs, Ctx ctx, Position pos)
    {
        _lastTypeArgs = null;
        if (f.TypeParams.Count == 0) return f;
        if (explicitTypeArgs.Count == 0)
        {
            var vars = f.TypeParams.Select(p => (TType)new VarT(p.ToLowerInvariant())).ToList();
            var m = new Dictionary<string, TType>();
            for (var i = 0; i < f.TypeParams.Count; i++) m[f.TypeParams[i]] = vars[i];
            _lastTypeArgs = vars;
            return Instantiate(f, m);
        }
        if (explicitTypeArgs.Count != f.TypeParams.Count)
        {
            Error(ctx, pos, $"expected {f.TypeParams.Count} type argument(s) but {explicitTypeArgs.Count} were given");
            return Instantiate(f);
        }
        var map = new Dictionary<string, TType>();
        for (var i = 0; i < f.TypeParams.Count; i++) map[f.TypeParams[i]] = explicitTypeArgs[i];
        _lastTypeArgs = explicitTypeArgs;
        return Instantiate(f, map);
    }

    private TType Lambda(LambdaExpr lam, Ctx ctx, FnT? expected)
    {
        var scope = new Scope(ctx.Scope, "lambda");
        if (expected is not null && expected.Params.Count != lam.Params.Count)
            return Error(ctx, lam.Pos, $"this lambda takes {lam.Params.Count} parameter(s) but a function of {expected.Params.Count} is expected");
        var paramTypes = new List<TType>();
        for (var i = 0; i < lam.Params.Count; i++)
        {
            var p = lam.Params[i];
            TType pt = expected is not null ? expected.Params[i] : new VarT(p.Name);
            if (p.Type is not null)
            {
                var annotated = ResolveType(p.Type, ctx.Scope, ctx.Module);
                if (!Unify(annotated, pt))
                    Error(ctx, p.Pos, $"lambda parameter '{p.Name}' is annotated {Show(annotated)} but {Show(pt)} is expected");
                pt = annotated;
            }
            scope.DefineValue(p.Name, new ValueSym(pt));
            paramTypes.Add(pt);
        }
        var ret = expected?.Return ?? new VarT("ret");
        var frame = new Frame { Name = "lambda", Pos = lam.Pos, Kind = FrameKind.Lambda, Module = ctx.Module };
        _frames.Add(frame);
        _lambdaFrames[lam] = frame;
        var inner = new Ctx { Scope = scope, Module = ctx.Module, Frame = frame, ReturnType = ret, Where = "this lambda", Service = ctx.Service, Constraints = ctx.Constraints };
        var bodyType = Block(lam.Body, inner, ret);
        if (!Unify(bodyType, ret))
            Error(ctx, lam.Pos, $"lambda body has type {Show(bodyType)} but {Show(ret)} is expected");
        var fnType = new FnT(paramTypes, ret, null);
        _frameOf[fnType] = frame;
        if (expected?.Effects is { } allowed)
            _obligations.Add(new Obligation(frame, allowed, lam.Pos, "this lambda", ctx.Module));
        return fnType;
    }

    private TType Binary(BinaryExpr b, Ctx ctx)
    {
        switch (b.Op)
        {
            case "and":
            case "or":
                Check(b.Left, ctx, PrimT.Bool);
                Check(b.Right, ctx, PrimT.Bool);
                return PrimT.Bool;
            case "==":
            case "!=":
            {
                var l = Infer(b.Left, ctx, null);
                var r = Infer(b.Right, ctx, l);
                if (!Unify(l, r)) Error(ctx, b.Pos, $"cannot compare {Show(l)} with {Show(r)}");
                return PrimT.Bool;
            }
        }
        var left = Prune(Infer(b.Left, ctx, null));
        var right = Prune(Infer(b.Right, ctx, left is VarT ? null : left));
        if (left is VarT && right is not VarT) { Unify(left, right); left = Prune(left); }
        if (right is VarT && left is not VarT) { Unify(right, left); right = Prune(right); }
        if (left is VarT && right is VarT) { Unify(left, PrimT.Int); Unify(right, PrimT.Int); left = right = PrimT.Int; }
        if (left is PrimT { Name: "?" } || right is PrimT { Name: "?" }) return b.Op is "<" or "<=" or ">" or ">=" ? PrimT.Bool : PrimT.Unknown;

        var comparison = b.Op is "<" or "<=" or ">" or ">=";
        bool SameNamed(string n) => left is PrimT { } lp && right is PrimT { } rp && lp.Name == n && rp.Name == n;
        if (SameNamed("Int")) return comparison ? PrimT.Bool : PrimT.Int;
        if (left is PrimT { Name: "Int" or "Float" } && right is PrimT { Name: "Int" or "Float" }) return comparison ? PrimT.Bool : PrimT.Float;
        if (SameNamed("String"))
        {
            if (b.Op == "+") return PrimT.String;
            if (comparison) return PrimT.Bool;
        }
        if (SameNamed("Instant") && comparison) return PrimT.Bool;
        if (comparison && left is ParamT lp && right is ParamT rp && lp.Name == rp.Name)
        {
            if (ctx.Constraints.TryGetValue(lp.Name, out var shapes) && shapes.Contains("Ord"))
            {
                OrdComparisons[b] = lp.Name;
                return PrimT.Bool;
            }
            return Error(ctx, b.Pos, $"'{b.Op}' on {lp.Name} needs '{lp.Name}: Ord' in the type parameters");
        }
        return Error(ctx, b.Pos, $"operator '{b.Op}' is not defined on {Show(left)} and {Show(right)}");
    }

    private TType Match(MatchExpr mx, Ctx ctx, TType? hint)
    {
        var scrutinee = Prune(Infer(mx.Scrutinee, ctx, null));
        var variants = VariantsOf(scrutinee);
        if (variants is null && scrutinee is not PrimT { Name: "Bool" or "Int" or "String" or "?" } && scrutinee is not TupleT && scrutinee is not AppT { Ctor: "Vector" })
            return Error(ctx, mx.Scrutinee.Pos, scrutinee is VarT
                ? "cannot match on a value whose type is not yet known; add a type annotation"
                : $"cannot match on {Show(scrutinee)}; only unions, Option, Result, tuples, vectors, Bool, Int and String can be matched");

        var result = hint ?? new VarT("arm");
        var covered = new HashSet<string>();
        var catchAll = false;
        foreach (var arm in mx.Arms)
        {
            var armScope = new Scope(ctx.Scope, "arm");
            // a guarded arm covers nothing for exhaustiveness: the guard may be false
            var total = Pattern(arm.Pattern, scrutinee, variants, armScope, ctx, arm.Guard is null ? covered : new HashSet<string>());
            if (arm.Guard is not null)
            {
                var gt = Infer(arm.Guard, ctx.With(armScope), PrimT.Bool);
                if (!Unify(gt, PrimT.Bool)) Error(ctx, arm.Guard.Pos, $"a guard must be a Bool but this one is {Show(gt)}");
                total = false;
            }
            if (total) catchAll = true;
            var bodyType = Block(arm.Body, ctx.With(armScope), result);
            if (!Unify(bodyType, result))
                Error(ctx, arm.Pos, $"match arms have different types: this arm has type {Show(bodyType)} but earlier arms have type {Show(result)}");
        }
        if (!catchAll && variants is not null)
        {
            var missing = variants.Where(v => !covered.Contains(v.Name)).Select(v => v.Name).ToList();
            if (missing.Count > 0)
                Error(ctx, mx.Pos, $"match on {Show(scrutinee)} is not exhaustive; missing {string.Join(", ", missing)}");
        }
        else if (!catchAll && variants is null && scrutinee is PrimT { Name: "Bool" })
        {
            if (!(covered.Contains("true") && covered.Contains("false")))
                Error(ctx, mx.Pos, "match on Bool is not exhaustive; add the missing case or a '_' arm");
        }
        else if (!catchAll && scrutinee is AppT { Ctor: "Vector" })
        {
            // exhaustive when some arm takes every length from k up and each length below k has an exact arm
            var rests = covered.Where(c => c.StartsWith("rest:")).Select(c => int.Parse(c[5..])).ToList();
            var exact = covered.Where(c => c.StartsWith("len:")).Select(c => int.Parse(c[4..])).ToHashSet();
            var ok = rests.Count > 0 && Enumerable.Range(0, rests.Min()).All(exact.Contains);
            if (!ok) Error(ctx, mx.Pos, $"match on {Show(scrutinee)} is not exhaustive; add a '_' arm or a '[..., ...rest]' arm that covers the remaining lengths");
        }
        else if (!catchAll && variants is null && scrutinee is not PrimT { Name: "?" })
            Error(ctx, mx.Pos, $"match on {Show(scrutinee)} needs a '_' arm");
        return Prune(result);
    }

    private static List<(string Name, IReadOnlyList<TType> Fields)>? VariantsOf(TType t) => t switch
    {
        UnionT u => u.Variants.Select(v => (v.Name, (IReadOnlyList<TType>)v.Fields.Select(f => f.Type).ToList())).ToList(),
        RecordT { Union: { } u } => VariantsOf(u),
        AppT { Ctor: "Option" } o => new() { ("None", Array.Empty<TType>()), ("Some", new[] { o.Args[0] }) },
        AppT { Ctor: "Result" } r => new() { ("Ok", new[] { r.Args[0] }), ("Error", new[] { r.Args[1] }) },
        _ => null,
    };

    /// <summary>Checks a pattern against a type, binding names. Returns true when the pattern matches every value.</summary>
    private bool Pattern(Pattern p, TType t, List<(string Name, IReadOnlyList<TType> Fields)>? variants, Scope scope, Ctx ctx, HashSet<string> covered)
    {
        t = Prune(t);
        switch (p)
        {
            case WildcardPattern:
                return true;
            case BindPattern b:
                scope.DefineValue(b.Name, new ValueSym(t));
                return true;
            case AsPattern ap:
                scope.DefineValue(ap.Name, new ValueSym(t));
                return Pattern(ap.Inner, t, variants, scope, ctx, covered);
            case TuplePattern tp:
            {
                if (t is VarT) Unify(t, new TupleT(tp.Items.Select(_ => (TType)new VarT("item")).ToList()));
                t = Prune(t);
                if (t is PrimT { Name: "?" }) { foreach (var item in tp.Items) Pattern(item, PrimT.Unknown, null, scope, ctx, new()); return false; }
                if (t is not TupleT tt || tt.Items.Count != tp.Items.Count)
                {
                    Error(ctx, tp.Pos, $"a tuple pattern with {tp.Items.Count} items cannot match {Show(t)}");
                    return false;
                }
                var allTotal = true;
                for (var i = 0; i < tp.Items.Count; i++)
                    if (!Pattern(tp.Items[i], tt.Items[i], VariantsOf(Prune(tt.Items[i])), scope, ctx, new())) allTotal = false;
                return allTotal;
            }
            case ListPattern lp:
            {
                if (t is VarT) Unify(t, new AppT("Vector", new TType[] { new VarT("elem") }));
                t = Prune(t);
                if (t is PrimT { Name: "?" }) { foreach (var item in lp.Items) Pattern(item, PrimT.Unknown, null, scope, ctx, new()); return false; }
                if (t is not AppT { Ctor: "Vector" } vec)
                {
                    Error(ctx, lp.Pos, $"a list pattern cannot match {Show(t)}");
                    return false;
                }
                var elem = vec.Args[0];
                var itemsTotal = true;
                foreach (var item in lp.Items)
                    if (!Pattern(item, elem, VariantsOf(Prune(elem)), scope, ctx, new())) itemsTotal = false;
                var restTotal = lp.Rest is not null && Pattern(lp.Rest, t, null, scope, ctx, new());
                // for exhaustiveness: this arm covers exactly this length, or every length from here up
                if (itemsTotal && lp.Rest is null) covered.Add($"len:{lp.Items.Count}");
                if (itemsTotal && restTotal) covered.Add($"rest:{lp.Items.Count}");
                return lp.Items.Count == 0 && restTotal;
            }
            case LiteralPattern lit:
            {
                var lt = Infer(lit.Literal, ctx, null);
                if (!Unify(lt, t)) Error(ctx, lit.Pos, $"pattern of type {Show(lt)} cannot match {Show(t)}");
                if (lit.Literal is BoolLit bl) covered.Add(bl.Value ? "true" : "false");
                return false;
            }
            case VariantPattern vp:
            {
                if (t is PrimT { Name: "?" }) { foreach (var a in vp.Args) Pattern(a, PrimT.Unknown, null, scope, ctx, new()); return false; }
                if (variants is null)
                {
                    Error(ctx, vp.Pos, $"pattern {vp.Name} cannot match a value of type {Show(t)}");
                    return false;
                }
                var v = variants.FirstOrDefault(x => x.Name == vp.Name);
                if (v.Name is null)
                {
                    Error(ctx, vp.Pos, $"{Show(t)} has no variant '{vp.Name}'");
                    return false;
                }
                if (vp.Args.Count != 0 && vp.Args.Count != v.Fields.Count)
                {
                    Error(ctx, vp.Pos, $"pattern {vp.Name} has {vp.Args.Count} binder(s) but the variant has {v.Fields.Count} field(s)");
                    return false;
                }
                var subTotal = true;
                for (var i = 0; i < vp.Args.Count; i++)
                    if (!Pattern(vp.Args[i], v.Fields[i], VariantsOf(Prune(v.Fields[i])), scope, ctx, new())) subTotal = false;
                if (subTotal) covered.Add(vp.Name);
                return false;
            }
            default:
                throw new InvalidOperationException(p.GetType().Name);
        }
    }

    private TType With(WithExpr w, Ctx ctx)
    {
        var target = Prune(Infer(w.Target, ctx, null));
        if (target is PrimT { Name: "?" }) { foreach (var f in w.Fields) Infer(f.Value, ctx, null); return PrimT.Unknown; }
        if (target is not RecordT root)
            return Error(ctx, w.Pos, $"'with' needs a record but found {Show(target)}");
        foreach (var f in w.Fields)
        {
            RecordT cur = root;
            TType? fieldType = null;
            var ok = true;
            for (var i = 0; i < f.Path.Count; i++)
            {
                var seg = f.Path[i];
                var ft = cur.Field(seg);
                if (ft is null)
                {
                    Error(ctx, f.Pos, $"{cur.Name} has no field '{seg}'");
                    ok = false;
                    break;
                }
                if (i < f.Path.Count - 1)
                {
                    if (Prune(ft) is RecordT inner) cur = inner;
                    else
                    {
                        Error(ctx, f.Pos, $"field '{seg}' has type {Show(ft)}, which has no fields to update");
                        ok = false;
                        break;
                    }
                }
                fieldType = ft;
            }
            if (ok) Check(f.Value, ctx, fieldType);
            else Infer(f.Value, ctx, null);
        }
        return root;
    }

    // ------------------------------------------------------------ composition roots

    private void CheckRoot(RootDecl root, Scope own, Module m)
    {
        var rootFrame = new Frame { Name = $"{m.Name}.root {root.Name}", Pos = root.Pos, Kind = FrameKind.Root, Module = m };
        _frames.Add(rootFrame);
        _rootFrames[root] = rootFrame;
        var ctx = new Ctx { Scope = new Scope(own, $"root {root.Name}"), Module = m, Frame = rootFrame, Where = $"root {root.Name}" };
        var entries = new Dictionary<string, Arg>();
        foreach (var e in root.Entries)
        {
            if (e.Name is null) { Error(ctx, e.Pos, "root entries must be named"); continue; }
            if (!entries.TryAdd(e.Name, e)) Error(ctx, e.Pos, $"root {root.Name} defines '{e.Name}' twice");
        }
        var types = new Dictionary<string, TType>();
        var resolving = new HashSet<string>();

        TType EntryType(string name)
        {
            if (types.TryGetValue(name, out var done)) return done;
            var entry = entries[name];
            if (!resolving.Add(name)) return Error(ctx, entry.Pos, $"root {root.Name} has a dependency cycle through '{name}'");
            TType t;
            if (entry.Value is TypeNameExpr tn && own.LookupType(tn.Name) is ServiceT st)
                t = Construct(st, new Dictionary<string, TType>(), entry.Pos);
            else if (entry.Value is CallExpr { Callee: TypeNameExpr ct, HasParens: false } c && own.LookupType(ct.Name) is ServiceT cst)
            {
                var overrides = new Dictionary<string, TType>();
                foreach (var a in c.BlockArgs)
                {
                    if (a.Name is null) { Error(ctx, a.Pos, "children of a service entry must be named"); continue; }
                    overrides[a.Name] = Infer(a.Value, ctx, null);
                }
                t = Construct(cst, overrides, entry.Pos);
            }
            else t = Infer(entry.Value, ctx, null);
            types[name] = t;
            RootEntryTypes[(root, name)] = t;
            resolving.Remove(name);
            return t;
        }

        TType Construct(ServiceT st, Dictionary<string, TType> overrides, Position pos)
        {
            foreach (var (depName, depType) in st.Dependencies)
            {
                TType? provided = null;
                if (overrides.TryGetValue(depName, out var o)) provided = o;
                else if (entries.ContainsKey(depName)) provided = EntryType(depName);
                else
                {
                    var typeName = (Prune(depType) as ServiceT)?.Name ?? (Prune(depType) as ShapeT)?.Name;
                    var byType = entries.FirstOrDefault(e => e.Value.Value is TypeNameExpr te && te.Name == typeName);
                    if (byType.Key is not null) provided = EntryType(byType.Key);
                    else if (Prune(depType) is ServiceT implicitService) provided = Construct(implicitService, new(), pos);
                    else if (Prune(depType) is FnT factory && Prune(factory.Return) is ServiceT target)
                    {
                        // synthesised factory: lambda parameters fill the target's constructor by type, in order;
                        // the rest resolve from the root like any dependency
                        var plan = PlanFactory(factory, target);
                        if (plan is null)
                        {
                            Error(ctx, pos, $"root {root.Name} cannot synthesise '{depName}: {Show(depType)}' for service {st.Name}: a parameter of the function type matches no constructor parameter of {target.Name}");
                            continue;
                        }
                        var filled = new HashSet<string>(plan.Select(i => target.Dependencies[i].Name));
                        var remaining = new ServiceT(target.Name, target.Scoped, target.IsResource);
                        foreach (var d in target.Dependencies) if (!filled.Contains(d.Name)) remaining.Dependencies.Add(d);
                        Construct(remaining, new(), pos);
                        provided = new FnT(factory.Params, target, new HashSet<string>());
                    }
                }
                if (provided is null)
                {
                    Error(ctx, pos, $"root {root.Name} cannot resolve dependency '{depName}: {Show(depType)}' of service {st.Name}; add an entry named '{depName}'");
                    continue;
                }
                Provides(provided, depType, pos, $"dependency '{depName}' of service {st.Name}", ctx);
            }
            return st;
        }

        foreach (var name in entries.Keys) EntryType(name);
    }

    /// <summary>
    /// For a function-typed dependency returning a service, which constructor parameter each
    /// lambda parameter fills: the first unfilled one whose type unifies. Null when one does not fit.
    /// </summary>
    public static IReadOnlyList<int>? PlanFactory(FnT factory, ServiceT target)
    {
        var plan = new List<int>();
        var used = new HashSet<int>();
        foreach (var p in factory.Params)
        {
            var idx = -1;
            for (var i = 0; i < target.Dependencies.Count; i++)
            {
                if (used.Contains(i)) continue;
                var trail = new List<VarT>();
                if (Unify(p, target.Dependencies[i].Type, trail)) { idx = i; break; }
                foreach (var v in trail) v.Bound = null;
            }
            if (idx < 0) return null;
            used.Add(idx);
            plan.Add(idx);
        }
        return plan;
    }

    /// <summary>The members of a shape over a type as a namespace scope, for <c>Shape.member(...)</c> calls.</summary>
    private Scope ClassScope(ShapeT cls)
    {
        if (_classScopes.TryGetValue(cls, out var existing)) return existing;
        var scope = new Scope(null, cls.Name);
        foreach (var (name, fn) in cls.Members) scope.DefineValue(name, new ValueSym(fn));
        _classScopes[cls] = scope;
        return scope;
    }

    /// <summary>Whether <paramref name="t"/> satisfies the shape: a constrained parameter in scope, a built-in instance, or a declared one.</summary>
    private bool CheckConstraint(string shape, TType t, Ctx ctx, Position pos, string what)
    {
        t = Prune(t);
        switch (t)
        {
            case ParamT p:
                if (ctx.Constraints.TryGetValue(p.Name, out var shapes) && shapes.Contains(shape)) return true;
                Error(ctx, pos, $"{what} needs {p.Name} to be {shape}; add '{p.Name}: {shape}' to the type parameters");
                return false;
            case VarT:
                Error(ctx, pos, $"{what}: cannot tell which type the {shape} instance is for; add a type argument");
                return false;
            case PrimT { Name: "?" }:
                return false;
            default:
                if (BuiltinSignatures.HasBuiltinInstance(shape, t) || Instances.ContainsKey((shape, TypeKey(t)))) return true;
                Error(ctx, pos, $"{what}: no instance of {shape} for {Show(t)}");
                return false;
        }
    }

    /// <summary>Checks that a provided value can stand in for a required dependency type, structurally for shapes.</summary>
    private void Provides(TType provided, TType required, Position pos, string what, Ctx ctx)
    {
        provided = Prune(provided);
        required = Prune(required);
        if (provided is PrimT { Name: "?" } || required is PrimT { Name: "?" }) return;
        switch (required)
        {
            case ShapeT shape:
            {
                var members = provided switch
                {
                    ServiceT s => s.Methods.ToDictionary(kv => kv.Key, kv => (TType)kv.Value),
                    ShapeT h => h.Members.ToDictionary(kv => kv.Key, kv => (TType)kv.Value),
                    RecordT r => r.Fields.ToDictionary(f => f.Name, f => f.Type),
                    _ => null,
                };
                if (members is null)
                {
                    Error(ctx, pos, $"{what}: {Show(provided)} cannot satisfy shape {shape.Name}; a service, shape, or record of functions is needed");
                    return;
                }
                foreach (var (name, member) in shape.Members)
                {
                    if (!members.TryGetValue(name, out var impl))
                    {
                        Error(ctx, pos, $"{what}: {Show(provided)} does not satisfy shape {shape.Name}; it has no member '{name}'");
                        continue;
                    }
                    if (Prune(impl) is not FnT implFn)
                    {
                        Error(ctx, pos, $"{what}: member '{name}' of {Show(provided)} is {Show(impl)}, not a function");
                        continue;
                    }
                    if (!TryUnify(implFn, member))
                        Error(ctx, pos, $"{what}: member '{name}' of {Show(provided)} is {Show(implFn)} but shape {shape.Name} requires {Show(member)}");
                    else if (implFn.Effects is not null && member.Effects is not null && !implFn.Effects.IsSubsetOf(member.Effects))
                        Error(ctx, pos, $"{what}: member '{name}' of {Show(provided)} has effects {Fx(implFn.Effects)} but shape {shape.Name} allows only {Fx(member.Effects)}");
                    else if (implFn.Effects is null && member.Effects is not null && _frameOf.TryGetValue(implFn, out var implFrame))
                        _obligations.Add(new Obligation(implFrame, member.Effects, pos, $"{what}: member '{name}' of {Show(provided)} (shape {shape.Name})", ctx.Module));
                }
                return;
            }
            case FnT reqFn:
            {
                if (provided is not FnT provFn || !TryUnify(provFn, reqFn))
                {
                    Error(ctx, pos, $"{what}: expected {Show(required)} but found {Show(provided)}");
                    return;
                }
                if (provFn.Effects is not null && reqFn.Effects is not null && !provFn.Effects.IsSubsetOf(reqFn.Effects))
                    Error(ctx, pos, $"{what}: the function has effects {Fx(provFn.Effects)} but only {Fx(reqFn.Effects)} are allowed");
                else if (provFn.Effects is null && reqFn.Effects is not null && _frameOf.TryGetValue(provFn, out var provFrame))
                    _obligations.Add(new Obligation(provFrame, reqFn.Effects, pos, what, ctx.Module));
                return;
            }
            default:
                if (!TryUnify(provided, required))
                    Error(ctx, pos, $"{what}: expected {Show(required)} but found {Show(provided)}");
                return;
        }
    }

    private static string Fx(IReadOnlySet<string> effects) => effects.Count == 0 ? "Pure" : string.Join(" ", effects.OrderBy(Order));

    private static int Order(string effect) => effect switch { "Nondet" => 0, "Write" => 1, "Suspend" => 2, _ => 3 };

    // ------------------------------------------------------------ pass 5: effects

    /// <summary>Fixpoint over frames: each frame's effects are the union of its sites' flags and its referenced frames' effects.</summary>
    private void SolveEffects()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var f in _frames)
            {
                var before = f.Solved.Count;
                foreach (var site in f.Sites)
                {
                    f.Solved.UnionWith(site.Flags);
                    foreach (var r in site.Refs) f.Solved.UnionWith(r.Solved);
                    if (site.FnArgs is null) continue;
                    foreach (var (index, arg) in site.FnArgs)
                    {
                        if (!site.AllFnArgs && !(site.CalleeFrame?.PolyParams.Contains(index) ?? false)) continue;
                        f.Solved.UnionWith(arg.Flags);
                        foreach (var r in arg.Refs) f.Solved.UnionWith(r.Solved);
                    }
                }
                if (f.Solved.Count != before) changed = true;
            }
        } while (changed);
    }

    /// <summary>Finds the call that introduced an effect into a frame, following references one level for the message.</summary>
    private static string Because(Frame f, string effect)
    {
        foreach (var site in f.Sites)
        {
            if (site.Flags.Contains(effect)) return $"it calls '{site.Callee}' at {site.Pos}, which has the {effect} effect";
            foreach (var r in site.Refs)
                if (r.Solved.Contains(effect))
                    return r.Kind == FrameKind.Lambda
                        ? $"it calls '{site.Callee}' at {site.Pos} with a lambda that has the {effect} effect"
                        : $"it calls '{site.Callee}' at {site.Pos}, whose inferred effects include {effect}";
            if (site.FnArgs is not null)
                foreach (var (index, arg) in site.FnArgs)
                {
                    if (!site.AllFnArgs && !(site.CalleeFrame?.PolyParams.Contains(index) ?? false)) continue;
                    if (arg.Refs.Any(r => r.Kind == FrameKind.Lambda && r.Solved.Contains(effect)))
                        return $"it calls '{site.Callee}' at {site.Pos} with a lambda that has the {effect} effect";
                    if (arg.Flags.Contains(effect) || arg.Refs.Any(r => r.Solved.Contains(effect)))
                        return $"it calls '{site.Callee}' at {site.Pos} with an argument that has the {effect} effect";
                }
        }
        return $"the {effect} effect arises in its body";
    }

    private void CheckEffects()
    {
        foreach (var f in _frames)
        {
            if (f.Declared is { } declared)
            {
                var extra = f.Solved.Except(declared).OrderBy(Order).ToList();
                if (extra.Count == 0) continue;
                var message = f.Kind == FrameKind.Init
                    ? $"{f.Name} must be pure but has the {string.Join(" ", extra)} effect: {Because(f, extra[0])}"
                    : $"'{Short(f.Name)}' is declared ! {Fx(declared)} but its body has the {string.Join(" ", extra)} effect: {Because(f, extra[0])}";
                Diagnostics.Add(new Diagnostic(f.Module.File, f.Pos, message));
            }
            else if (f.Kind == FrameKind.Fn && f.Solved.Contains("Write"))
            {
                Diagnostics.Add(new Diagnostic(f.Module.File, f.Pos,
                    $"'{Short(f.Name)}' is a fn and cannot write: {Because(f, "Write")}; only handlers and service methods may write"));
            }
        }
        foreach (var o in _obligations)
        {
            var extra = o.Actual.Solved.Except(o.Allowed).OrderBy(Order).ToList();
            if (extra.Count == 0) continue;
            var subject = o.Actual.Kind == FrameKind.Lambda ? "this lambda" : $"'{Short(o.Actual.Name)}'";
            Diagnostics.Add(new Diagnostic(o.Module.File, o.Pos,
                $"{o.What}: {subject} has effects {Fx(o.Actual.Solved)} but only {Fx(o.Allowed)} are allowed: {Because(o.Actual, extra[0])}"));
        }
    }

    private static string Short(string qualified) => qualified.Contains('.') ? qualified[(qualified.LastIndexOf('.') + 1)..] : qualified;
}

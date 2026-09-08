using System.Text;
using Trebuchet.Compiler.Syntax;

namespace Trebuchet.Compiler.Semantics;

public sealed record Diagnostic(string? File, Position Pos, string Message)
{
    public override string ToString() => File is null ? $"{Pos}: error: {Message}" : $"{File}:{Pos}: error: {Message}";
}

/// <summary>Semantic types. Distinct from the syntactic <see cref="TypeRef"/>.</summary>
public abstract class TType
{
    public abstract string Show();
    public override string ToString() => Show();
}

/// <summary>Int, Float, Bool, String, Unit, Instant, plus the two special types Never (bottom) and Unknown (error recovery).</summary>
public sealed class PrimT : TType
{
    public string Name { get; }
    private PrimT(string name) => Name = name;
    public override string Show() => Name;

    public static readonly PrimT Int = new("Int");
    public static readonly PrimT Float = new("Float");
    public static readonly PrimT Bool = new("Bool");
    public static readonly PrimT String = new("String");
    public static readonly PrimT Unit = new("Unit");
    public static readonly PrimT Instant = new("Instant");
    public static readonly PrimT Never = new("Never");
    public static readonly PrimT Unknown = new("?");

    public static PrimT? ByName(string name) => name switch
    {
        "Int" => Int, "Float" => Float, "Bool" => Bool, "String" => String,
        "Unit" => Unit, "Instant" => Instant, "Never" => Never, _ => null,
    };
}

/// <summary>A builtin generic type applied to arguments: Vector, Map, Set, Result, Option, Cell.</summary>
public sealed class AppT : TType
{
    public string Ctor { get; }
    public IReadOnlyList<TType> Args { get; }
    public AppT(string ctor, IReadOnlyList<TType> args)
    {
        Ctor = ctor;
        Args = args;
    }
    public override string Show() => $"{Ctor}[{string.Join(", ", Args.Select(a => a.Show()))}]";

    public static readonly Dictionary<string, int> Arities = new()
    {
        ["Vector"] = 1, ["Set"] = 1, ["Option"] = 1, ["Cell"] = 1, ["Map"] = 2, ["Result"] = 2,
    };
    public static string Normalize(string name) => name == "List" ? "Vector" : name;
}

public sealed class RecordT : TType
{
    public string Name { get; }
    public bool IsEntity { get; }
    private List<(string Name, TType Type)>? _fields;
    private Func<List<(string Name, TType Type)>>? _lazyFields;
    /// <summary>
    /// Fields of the record. For an instantiation of a generic definition they are computed on
    /// first access by substituting the definition's fields, which is what lets a recursive
    /// generic such as <c>Tree[T]</c> with a <c>Tree[T]</c> field instantiate without looping.
    /// </summary>
    public List<(string Name, TType Type)> Fields
    {
        get
        {
            if (_fields is null)
            {
                _fields = _lazyFields?.Invoke() ?? new List<(string, TType)>();
                _lazyFields = null;
            }
            return _fields;
        }
    }
    internal void SetLazyFields(Func<List<(string Name, TType Type)>> compute) => _lazyFields = compute;
    /// <summary>The union this variant belongs to, or null for a plain record.</summary>
    public UnionT? Union { get; internal set; }
    /// <summary>Type parameter names of the generic definition (empty when not generic).</summary>
    public IReadOnlyList<string> TypeParams { get; }
    /// <summary>Type arguments: ParamT for the definition itself, concrete or variable types for an instantiation.</summary>
    public IReadOnlyList<TType> TypeArgs { get; }
    /// <summary>The generic definition this record was instantiated from; null for definitions and non-generic records.</summary>
    public RecordT? Definition { get; internal set; }
    public RecordT(string name, bool isEntity, IReadOnlyList<string>? typeParams = null, IReadOnlyList<TType>? typeArgs = null)
    {
        Name = name;
        IsEntity = isEntity;
        TypeParams = typeParams ?? Array.Empty<string>();
        TypeArgs = typeArgs ?? TypeParams.Select(p => (TType)new ParamT(p)).ToList();
    }
    public TType? Field(string name) => Fields.FirstOrDefault(f => f.Name == name).Type;
    public override string Show() => TypeArgs.Count == 0 ? Name : $"{Name}[{string.Join(", ", TypeArgs.Select(a => a.Show()))}]";
}

public sealed class UnionT : TType
{
    public string Name { get; }
    public List<RecordT> Variants { get; } = new();
    public IReadOnlyList<string> TypeParams { get; }
    public IReadOnlyList<TType> TypeArgs { get; }
    public UnionT? Definition { get; internal set; }
    public UnionT(string name, IReadOnlyList<string>? typeParams = null, IReadOnlyList<TType>? typeArgs = null)
    {
        Name = name;
        TypeParams = typeParams ?? Array.Empty<string>();
        TypeArgs = typeArgs ?? TypeParams.Select(p => (TType)new ParamT(p)).ToList();
    }
    public RecordT? Variant(string name) => Variants.FirstOrDefault(v => v.Name == name);
    public override string Show() => TypeArgs.Count == 0 ? Name : $"{Name}[{string.Join(", ", TypeArgs.Select(a => a.Show()))}]";
}

/// <summary>A tuple type, two or more items. Structural: (A, B) unifies with (A, B) item by item.</summary>
public sealed class TupleT : TType
{
    public IReadOnlyList<TType> Items { get; }
    public TupleT(IReadOnlyList<TType> items) => Items = items;
    public override string Show() => $"({string.Join(", ", Items.Select(i => i.Show()))})";
}

public sealed class FnT : TType
{
    public IReadOnlyList<TType> Params { get; }
    public IReadOnlyList<string?> ParamNames { get; }
    public TType Return { get; }
    /// <summary>Null when unspecified (to be inferred); empty for Pure.</summary>
    public IReadOnlySet<string>? Effects { get; }
    public bool IsHandler { get; }
    /// <summary>Names of type parameters for a generic builtin; substituted on instantiation.</summary>
    public IReadOnlyList<string> TypeParams { get; }
    /// <summary>Set for a record or variant constructor, so named arguments can be mapped.</summary>
    public RecordT? Constructs { get; }
    /// <summary>Set for a service constructor.</summary>
    public ServiceT? ConstructsService { get; }
    /// <summary>For an instantiated generic, the signature it was instantiated from.</summary>
    public FnT? Origin { get; internal set; }

    /// <summary>Constraints on the type parameters: parameter name to the shapes over types it must satisfy.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Constraints { get; }

    public FnT(IReadOnlyList<TType> parameters, TType ret, IReadOnlySet<string>? effects = null,
        IReadOnlyList<string?>? paramNames = null, bool isHandler = false, IReadOnlyList<string>? typeParams = null,
        RecordT? constructs = null, ServiceT? constructsService = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? constraints = null)
    {
        Constraints = constraints ?? new Dictionary<string, IReadOnlyList<string>>();
        Params = parameters;
        Return = ret;
        Effects = effects;
        ParamNames = paramNames ?? parameters.Select(_ => (string?)null).ToList();
        IsHandler = isHandler;
        TypeParams = typeParams ?? Array.Empty<string>();
        Constructs = constructs;
        ConstructsService = constructsService;
    }

    public bool Writes => IsHandler || (Effects is not null && Effects.Contains("Write"));

    public override string Show()
    {
        var ps = string.Join(", ", Params.Select(p => p.Show()));
        var fx = Effects is null ? "" : Effects.Count == 0 ? " ! Pure" : " ! " + string.Join(" ", Effects);
        return $"fn({ps}) -> {Return.Show()}{fx}";
    }
}

/// <summary>A set of alternatives for one builtin name, chosen by arity and receiver type.</summary>
public sealed class OverloadT : TType
{
    public IReadOnlyList<FnT> Alternatives { get; }
    public string Name { get; }
    public OverloadT(string name, IReadOnlyList<FnT> alternatives)
    {
        Name = name;
        Alternatives = alternatives;
    }
    public override string Show() => $"<{Name}: {Alternatives.Count} overloads>";
}

public sealed class ServiceT : TType
{
    public string Name { get; }
    public bool Scoped { get; }
    public bool IsResource { get; }
    public List<(string Name, TType Type)> Dependencies { get; } = new();
    public Dictionary<string, FnT> Methods { get; } = new();
    /// <summary>Methods marked private: callable from the service's own methods only.</summary>
    public HashSet<string> PrivateMethods { get; } = new();
    public ServiceT(string name, bool scoped, bool isResource = false)
    {
        Name = name;
        Scoped = scoped;
        IsResource = isResource;
    }
    public override string Show() => Name;
}

public sealed class ShapeT : TType
{
    public string Name { get; }
    public Dictionary<string, FnT> Members { get; } = new();
    /// <summary>Non-empty for a shape over a type (a type class): the one parameter its members mention.</summary>
    public IReadOnlyList<string> TypeParams { get; init; } = Array.Empty<string>();
    public bool IsClass => TypeParams.Count > 0;
    public ShapeT(string name) => Name = name;
    public override string Show() => Name;
}

/// <summary>An inference variable. Bound destructively by the unifier, with a trail for rollback.</summary>
public sealed class VarT : TType
{
    private static int _next;
    public int Id { get; } = ++_next;
    public string Hint { get; }
    public TType? Bound { get; internal set; }
    public VarT(string hint = "t") => Hint = hint;
    public override string Show() => Bound is null ? $"'{Hint}{Id}" : Bound.Show();
}

/// <summary>A generic parameter inside a builtin signature; replaced by a fresh <see cref="VarT"/> on instantiation.</summary>
public sealed class ParamT : TType
{
    public string Name { get; }
    public ParamT(string name) => Name = name;
    public override string Show() => Name;
}

/// <summary>The type of an expression that names a scope: a module by short name, a union, or a builtin namespace.</summary>
public sealed class NamespaceT : TType
{
    public string Name { get; }
    public Scope Scope { get; }
    public NamespaceT(string name, Scope scope)
    {
        Name = name;
        Scope = scope;
    }
    public override string Show() => $"namespace {Name}";
}

// ---------------------------------------------------------------- scopes

public abstract record Symbol;

/// <summary>A value binding. <see cref="IsHandler"/> marks handler declarations for the Write rule.</summary>
/// <summary>A value binding. <see cref="Generic"/> marks a value whose generic type is instantiated afresh at each use, such as the nullary variant of a generic union; a local whose type mentions the enclosing function's type parameters is not.</summary>
public sealed record ValueSym(TType Type, bool IsHandler = false, bool Generic = false) : Symbol;

public sealed record NamespaceSym(Scope Scope) : Symbol;

public sealed class Scope
{
    public Scope? Parent { get; }
    public string Label { get; }
    private readonly Dictionary<string, Symbol> _values;
    private readonly Dictionary<string, TType> _types;

    /// <summary>Names this scope refuses to show to lookups; used by <see cref="ExportView"/>.</summary>
    private readonly HashSet<string> _hidden = new();

    /// <summary>A view over this scope's live declarations that hides the given names: what another module sees through <c>use</c>.</summary>
    public Scope ExportView(IEnumerable<string> hidden)
    {
        var view = new Scope(null, Label, _values, _types); // same label: emitters qualify imported names by it
        foreach (var h in hidden) view._hidden.Add(h);
        return view;
    }

    private Scope(Scope? parent, string label, Dictionary<string, Symbol> values, Dictionary<string, TType> types)
    {
        Parent = parent;
        Label = label;
        _values = values;
        _types = types;
    }

    public Scope(Scope? parent, string label)
    {
        Parent = parent;
        Label = label;
        _values = new();
        _types = new();
    }

    /// <summary>Scopes whose own declarations are visible here, searched after this scope's own and before the parent. Used for imports.</summary>
    public List<Scope> Includes { get; } = new();

    public void DefineValue(string name, Symbol sym) => _values[name] = sym;
    public void DefineType(string name, TType type) => _types[name] = type;

    public Symbol? LookupValue(string name)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (!s._hidden.Contains(name) && s._values.TryGetValue(name, out var v)) return v;
            foreach (var inc in s.Includes)
                if (!inc._hidden.Contains(name) && inc._values.TryGetValue(name, out var iv)) return iv;
        }
        return null;
    }

    public TType? LookupType(string name)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (!s._hidden.Contains(name) && s._types.TryGetValue(name, out var t)) return t;
            foreach (var inc in s.Includes)
                if (!inc._hidden.Contains(name) && inc._types.TryGetValue(name, out var it)) return it;
        }
        return null;
    }

    /// <summary>This scope's own declarations only; used for namespace member access.</summary>
    public Symbol? LookupLocalValue(string name) => _hidden.Contains(name) ? null : _values.GetValueOrDefault(name);
    public bool IsHidden(string name) => _hidden.Contains(name);

    public bool HasLocalValue(string name) => _values.ContainsKey(name);
    public IEnumerable<KeyValuePair<string, Symbol>> LocalValues => _values;
    public IEnumerable<KeyValuePair<string, TType>> LocalTypes => _types;
}

// ---------------------------------------------------------------- unification

public static class Unifier
{
    public static TType Prune(TType t)
    {
        while (t is VarT { Bound: not null } v) t = v.Bound;
        return t;
    }

    /// <summary>Structural unification. Effects on function types are not compared here.</summary>
    public static bool Unify(TType a, TType b, List<VarT>? trail = null)
    {
        a = Prune(a);
        b = Prune(b);
        if (ReferenceEquals(a, b)) return true;
        if (a is PrimT { Name: "?" } || b is PrimT { Name: "?" }) return true;
        if (a is PrimT { Name: "Never" } || b is PrimT { Name: "Never" }) return true;
        if (a is VarT va) return Bind(va, b, trail);
        if (b is VarT vb) return Bind(vb, a, trail);
        switch (a, b)
        {
            case (PrimT pa, PrimT pb): return pa.Name == pb.Name;
            case (ParamT xa, ParamT xb): return xa.Name == xb.Name;
            case (AppT xa, AppT xb):
                if (xa.Ctor != xb.Ctor || xa.Args.Count != xb.Args.Count) return false;
                for (var i = 0; i < xa.Args.Count; i++)
                    if (!Unify(xa.Args[i], xb.Args[i], trail)) return false;
                return true;
            case (RecordT ra, RecordT rb):
                if (ReferenceEquals(ra, rb)) return true;
                if (ra.Union is not null && rb.Union is not null) return ra.Union.Name == rb.Union.Name && UnifyAll(ra.TypeArgs, rb.TypeArgs, trail);
                return ra.Name == rb.Name && ra.Union is null && rb.Union is null && UnifyAll(ra.TypeArgs, rb.TypeArgs, trail);
            case (UnionT ua, UnionT ub): return ua.Name == ub.Name && UnifyAll(ua.TypeArgs, ub.TypeArgs, trail);
            case (RecordT rv, UnionT u): return rv.Union is not null && rv.Union.Name == u.Name && UnifyAll(rv.TypeArgs, u.TypeArgs, trail);
            case (UnionT u, RecordT rv): return rv.Union is not null && rv.Union.Name == u.Name && UnifyAll(u.TypeArgs, rv.TypeArgs, trail);
            case (TupleT ta, TupleT tb): return ta.Items.Count == tb.Items.Count && UnifyAll(ta.Items, tb.Items, trail);
            case (ServiceT sa, ServiceT sb): return sa.Name == sb.Name;
            case (ShapeT ha, ShapeT hb): return ha.Name == hb.Name;
            case (FnT fa, FnT fb):
                if (fa.Params.Count != fb.Params.Count) return false;
                for (var i = 0; i < fa.Params.Count; i++)
                    if (!Unify(fa.Params[i], fb.Params[i], trail)) return false;
                return Unify(fa.Return, fb.Return, trail);
            default:
                return false;
        }
    }

    private static bool UnifyAll(IReadOnlyList<TType> a, IReadOnlyList<TType> b, List<VarT>? trail)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!Unify(a[i], b[i], trail)) return false;
        return true;
    }

    private static bool Bind(VarT v, TType t, List<VarT>? trail)
    {
        if (Occurs(v, t)) return false;
        v.Bound = t;
        trail?.Add(v);
        return true;
    }

    private static bool Occurs(VarT v, TType t)
    {
        t = Prune(t);
        return t switch
        {
            VarT w => ReferenceEquals(v, w),
            AppT a => a.Args.Any(x => Occurs(v, x)),
            TupleT tt => tt.Items.Any(x => Occurs(v, x)),
            FnT f => f.Params.Any(p => Occurs(v, p)) || Occurs(v, f.Return),
            RecordT r => r.TypeArgs.Any(x => Occurs(v, x)),
            UnionT u => u.TypeArgs.Any(x => Occurs(v, x)),
            _ => false,
        };
    }

    /// <summary>Unify, undoing all bindings if it fails.</summary>
    public static bool TryUnify(TType a, TType b)
    {
        var trail = new List<VarT>();
        if (Unify(a, b, trail)) return true;
        foreach (var v in trail) v.Bound = null;
        return false;
    }

    /// <summary>Replace generic parameters with fresh variables.</summary>
    public static FnT Instantiate(FnT f, IReadOnlyDictionary<string, TType>? explicitArgs = null)
    {
        if (f.TypeParams.Count == 0) return f;
        var map = new Dictionary<string, TType>();
        foreach (var p in f.TypeParams)
            map[p] = explicitArgs is not null && explicitArgs.TryGetValue(p, out var given) ? given : new VarT(p.ToLowerInvariant());
        return new FnT(
            f.Params.Select(p => Subst(p, map)).ToList(),
            Subst(f.Return, map),
            f.Effects, f.ParamNames, f.IsHandler, Array.Empty<string>(), f.Constructs, f.ConstructsService)
        { Origin = f.Origin ?? f };
    }

    public static TType Subst(TType t, IReadOnlyDictionary<string, TType> map)
    {
        t = Prune(t);
        switch (t)
        {
            case ParamT p: return map.TryGetValue(p.Name, out var r) ? r : p;
            case AppT a: return new AppT(a.Ctor, a.Args.Select(x => Subst(x, map)).ToList());
            case TupleT tt: return new TupleT(tt.Items.Select(x => Subst(x, map)).ToList());
            case FnT f: return new FnT(f.Params.Select(x => Subst(x, map)).ToList(), Subst(f.Return, map), f.Effects, f.ParamNames, f.IsHandler, f.TypeParams, f.Constructs, f.ConstructsService) { Origin = f.Origin ?? f };
            case UnionT u when u.TypeArgs.Count > 0 && Mentions(u, map):
                return InstantiateUnion(u, u.TypeArgs.Select(x => Subst(x, map)).ToList());
            case RecordT rec when rec.TypeArgs.Count > 0 && Mentions(rec, map):
                if (rec.Union is { } owner)
                    return InstantiateUnion(owner, rec.TypeArgs.Select(x => Subst(x, map)).ToList()).Variant(rec.Name)!;
                return InstantiateRecord(rec, rec.TypeArgs.Select(x => Subst(x, map)).ToList());
            default: return t;
        }
    }

    private static bool Mentions(TType t, IReadOnlyDictionary<string, TType> map)
    {
        t = Prune(t);
        return t switch
        {
            ParamT p => map.ContainsKey(p.Name),
            AppT a => a.Args.Any(x => Mentions(x, map)),
            FnT f => f.Params.Any(x => Mentions(x, map)) || Mentions(f.Return, map),
            TupleT tt => tt.Items.Any(x => Mentions(x, map)),
            RecordT r => r.TypeArgs.Any(x => Mentions(x, map)),
            UnionT u => u.TypeArgs.Any(x => Mentions(x, map)),
            _ => false,
        };
    }

    /// <summary>A record type with its type parameters replaced by the given arguments. Fields are substituted.</summary>
    public static RecordT InstantiateRecord(RecordT record, IReadOnlyList<TType> args)
    {
        var def = record.Definition ?? record;
        var map = new Dictionary<string, TType>();
        for (var i = 0; i < def.TypeParams.Count; i++) map[def.TypeParams[i]] = args[i];
        var inst = new RecordT(def.Name, def.IsEntity, def.TypeParams, args) { Definition = def };
        inst.SetLazyFields(() => def.Fields.Select(f => (f.Name, Subst(f.Type, map))).ToList());
        return inst;
    }

    public static UnionT InstantiateUnion(UnionT union, IReadOnlyList<TType> args)
    {
        var def = union.Definition ?? union;
        var map = new Dictionary<string, TType>();
        for (var i = 0; i < def.TypeParams.Count; i++) map[def.TypeParams[i]] = args[i];
        var inst = new UnionT(def.Name, def.TypeParams, args) { Definition = def };
        foreach (var v in def.Variants)
        {
            var vi = new RecordT(v.Name, false, def.TypeParams, args) { Union = inst, Definition = v };
            var variantDef = v;
            vi.SetLazyFields(() => variantDef.Fields.Select(f => (f.Name, Subst(f.Type, map))).ToList());
            inst.Variants.Add(vi);
        }
        return inst;
    }

    /// <summary>A generic type with fresh inference variables for its parameters, for a value expression such as a nullary variant.</summary>
    public static TType Fresh(TType t) => t switch
    {
        UnionT { TypeArgs.Count: > 0 } u when u.TypeArgs.All(a => a is ParamT) => InstantiateUnion(u, u.TypeParams.Select(p => (TType)new VarT(p.ToLowerInvariant())).ToList()),
        RecordT { TypeArgs.Count: > 0 } r when r.TypeArgs.All(a => a is ParamT) => InstantiateRecord(r, r.TypeParams.Select(p => (TType)new VarT(p.ToLowerInvariant())).ToList()),
        _ => t,
    };

    /// <summary>Fully pruned copy for printing in messages.</summary>
    public static TType Resolve(TType t)
    {
        t = Prune(t);
        return t switch
        {
            AppT a => new AppT(a.Ctor, a.Args.Select(Resolve).ToList()),
            FnT f => new FnT(f.Params.Select(Resolve).ToList(), Resolve(f.Return), f.Effects, f.ParamNames, f.IsHandler, f.TypeParams, f.Constructs, f.ConstructsService),
            TupleT tt => new TupleT(tt.Items.Select(Resolve).ToList()),
            RecordT r when r.TypeArgs.Count > 0 => r.Union is { } o ? InstantiateUnion(o, r.TypeArgs.Select(Resolve).ToList()).Variant(r.Name)! : InstantiateRecord(r, r.TypeArgs.Select(Resolve).ToList()),
            UnionT u when u.TypeArgs.Count > 0 => InstantiateUnion(u, u.TypeArgs.Select(Resolve).ToList()),
            _ => t,
        };
    }

    public static string Show(TType t) => Resolve(t).Show();
}

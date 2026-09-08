using System.Collections.Immutable;
using System.Text;
using Trebuchet.Compiler.Runtime;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Compiler.Syntax;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;

namespace Trebuchet.Compiler.Testing;

/// <summary>
/// Property-based testing over the interpreter. A property is any top-level function whose
/// name starts with <c>prop</c> and returns Bool. Arguments are generated from the parameter
/// types; a failing case is shrunk greedily (smaller numbers, shorter vectors and strings)
/// before it is reported. Pure functions need no setup, which is why this is cheap here.
/// </summary>
public sealed class PropertyRunner
{
    public sealed record Outcome(string Module, string Name, int Cases, string? Counterexample, string? Error)
    {
        public bool Passed => Counterexample is null && Error is null;
    }

    private readonly ModuleSet _modules;
    private readonly TypeChecker _checker;
    private readonly Interpreter _it;
    private readonly Random _random;

    public PropertyRunner(ModuleSet modules, int seed = 20260908)
    {
        _modules = modules;
        _it = new Interpreter(modules);
        _checker = _it.Checker ?? throw new TrebPanic("the program does not type-check; run treb check");
        _random = new Random(seed);
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _checker.Diagnostics;

    public static bool IsProperty(FnDecl fn) => fn.Signature.Name.StartsWith("prop", StringComparison.Ordinal);

    public IReadOnlyList<Outcome> RunAll(int cases = 100)
    {
        var outcomes = new List<Outcome>();
        foreach (var m in _modules.Modules)
            foreach (var fn in m.Decls.OfType<FnDecl>().Where(IsProperty))
                outcomes.Add(Run(m, fn, cases));
        return outcomes;
    }

    public Outcome Run(Module m, FnDecl fn, int cases)
    {
        var sym = _checker.ScopeOf(m).LookupLocalValue(fn.Signature.Name) as ValueSym;
        if (sym?.Type is not FnT ft)
            return new Outcome(m.Name, fn.Signature.Name, 0, null, "not a function");
        if (Unifier.Prune(ft.Return) is not PrimT { Name: "Bool" })
            return new Outcome(m.Name, fn.Signature.Name, 0, null, $"a property must return Bool, not {Unifier.Show(ft.Return)}");
        if (ft.TypeParams.Count > 0)
            return new Outcome(m.Name, fn.Signature.Name, 0, null, "a property cannot be generic; give its parameters concrete types");
        var env = _it.EnvOf(m);
        env.TryGet(fn.Signature.Name, out var callable);
        var paramTypes = ft.Params.Select(Unifier.Prune).ToList();
        foreach (var t in paramTypes)
            if (!CanGenerate(t)) return new Outcome(m.Name, fn.Signature.Name, 0, null, $"cannot generate values of type {Unifier.Show(t)}");

        for (var i = 0; i < cases; i++)
        {
            var size = Math.Min(1 + i / 4, 20); // grow the inputs as the run goes on
            var args = paramTypes.Select(t => Generate(t, size, env)).ToList();
            string? failure;
            try { failure = Holds(callable, args) ? null : "false"; }
            catch (TrebPanic p) { failure = "panic: " + p.Message; }
            if (failure is null) continue;
            var shrunk = Shrink(callable, args, paramTypes, env);
            var text = string.Join(", ", fn.Signature.Params.Select((p, k) => $"{p.Name} = {shrunk[k].Show()}"));
            return new Outcome(m.Name, fn.Signature.Name, i + 1, text, failure == "false" ? null : failure);
        }
        return new Outcome(m.Name, fn.Signature.Name, cases, null, null);
    }

    private bool Holds(Value callable, IReadOnlyList<Value> args) => _it.Call(callable, args) is BoolValue { V: true };

    private bool Fails(Value callable, IReadOnlyList<Value> args)
    {
        try { return !Holds(callable, args); }
        catch (TrebPanic) { return true; }
    }

    // ------------------------------------------------------------ generation

    private static bool CanGenerate(TType t) => Unifier.Prune(t) switch
    {
        PrimT { Name: "Int" or "Float" or "Bool" or "String" or "Instant" or "Unit" } => true,
        AppT { Ctor: "Vector" or "Set" or "Option" or "Seq" } a => CanGenerate(a.Args[0]),
        AppT { Ctor: "Map" or "Result" } a => a.Args.All(CanGenerate),
        TupleT tt => tt.Items.All(CanGenerate),
        RecordT { IsEntity: false } r => r.Fields.All(f => CanGenerate(f.Type)),
        UnionT u => u.Variants.All(v => v.Fields.All(f => CanGenerate(f.Type))),
        _ => false,
    };

    private Value Generate(TType t, int size, Env env)
    {
        t = Unifier.Prune(t);
        switch (t)
        {
            case PrimT { Name: "Int" }: return new IntValue(_random.Next(-size * 5, size * 5 + 1));
            case PrimT { Name: "Float" }: return new FloatValue(Math.Round((_random.NextDouble() * 2 - 1) * size * 5, 2));
            case PrimT { Name: "Bool" }: return BoolValue.Of(_random.Next(2) == 0);
            case PrimT { Name: "String" }:
            {
                var len = _random.Next(0, Math.Min(size, 8) + 1);
                var sb = new StringBuilder();
                for (var i = 0; i < len; i++) sb.Append("abcxyz "[_random.Next(7)]);
                return new StringValue(sb.ToString());
            }
            case PrimT { Name: "Instant" }:
                return new InstantValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_random.Next(0, 60 * 24 * 365)));
            case PrimT { Name: "Unit" }: return UnitValue.Instance;
            case AppT { Ctor: "Vector" } v:
                return new ListValue(Vector<Value>.From(Enumerable.Range(0, _random.Next(0, Math.Min(size, 8) + 1)).Select(_ => Generate(v.Args[0], size, env))));
            case AppT { Ctor: "Seq" } sq:
            {
                var items = Enumerable.Range(0, _random.Next(0, Math.Min(size, 8) + 1)).Select(_ => Generate(sq.Args[0], size, env)).ToList();
                return new SeqValue(() => items);
            }
            case AppT { Ctor: "Set" } st:
                return new SetValue(Set<Value>.From(Enumerable.Range(0, _random.Next(0, Math.Min(size, 8) + 1)).Select(_ => Generate(st.Args[0], size, env))));
            case AppT { Ctor: "Map" } mp:
            {
                var m = Map<Value, Value>.Empty;
                for (var i = _random.Next(0, Math.Min(size, 6) + 1); i > 0; i--) m = m.Set(Generate(mp.Args[0], size, env), Generate(mp.Args[1], size, env));
                return new MapValue(m);
            }
            case AppT { Ctor: "Option" } o:
                return _random.Next(4) == 0 ? Builtins.None : Builtins.Some(Generate(o.Args[0], size, env));
            case AppT { Ctor: "Result" } r:
                return _random.Next(4) == 0 ? Builtins.Error(Generate(r.Args[1], size, env)) : Builtins.Ok(Generate(r.Args[0], size, env));
            case TupleT tt:
                return new TupleValue(tt.Items.Select(i => Generate(i, size, env)).ToImmutableArray());
            case RecordT { Union: null } rec:
                return Construct(env, rec.Name, rec.Fields.Select(f => Generate(f.Type, size, env)).ToList(), size);
            case UnionT u:
            {
                var v = u.Variants[_random.Next(u.Variants.Count)];
                if (v.Fields.Count == 0)
                    return env.TryGet(v.Name, out var nullary) ? nullary : throw new TrebPanic($"cannot find variant {v.Name}");
                return Construct(env, v.Name, v.Fields.Select(f => Generate(f.Type, size, env)).ToList(), size);
            }
            case RecordT { Union: { } owner }:
                return Generate(owner, size, env);
            default:
                throw new TrebPanic($"cannot generate a value of type {Unifier.Show(t)}");
        }
    }

    /// <summary>Constructs a record; a validating constructor that panics gets a few more tries with fresh fields.</summary>
    private Value Construct(Env env, string name, IReadOnlyList<Value> args, int size)
    {
        if (!env.TryGet(name, out var ctor)) throw new TrebPanic($"cannot find constructor {name}");
        for (var attempt = 0; ; attempt++)
        {
            try { return _it.Call(ctor, args); }
            catch (TrebPanic) when (attempt < 20)
            {
                // a validating constructor rejected these fields: try again with fresh primitives in the same shape
                args = args.Select(a => Regenerate(a, size, env)).ToList();
            }
        }
    }

    private Value Regenerate(Value like, int size, Env env) => like switch
    {
        IntValue => new IntValue(_random.Next(-size * 5, size * 5 + 1)),
        FloatValue => new FloatValue(Math.Round((_random.NextDouble() * 2 - 1) * size * 5, 2)),
        BoolValue => BoolValue.Of(_random.Next(2) == 0),
        StringValue => new StringValue(new string("abcxyz"[_random.Next(6)], _random.Next(0, 5))),
        InstantValue => new InstantValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_random.Next(0, 60 * 24 * 365))),
        _ => like,
    };

    // ------------------------------------------------------------ shrinking

    private IReadOnlyList<Value> Shrink(Value callable, IReadOnlyList<Value> args, IReadOnlyList<TType> types, Env env)
    {
        var current = args.ToList();
        for (var round = 0; round < 200; round++)
        {
            var improved = false;
            for (var i = 0; i < current.Count && !improved; i++)
            {
                foreach (var candidate in Smaller(current[i]))
                {
                    var trial = current.ToList();
                    trial[i] = candidate;
                    if (Fails(callable, trial)) { current = trial; improved = true; break; }
                }
            }
            if (!improved) break;
        }
        return current;
    }

    /// <summary>Candidates that are "smaller" than a value, most aggressive first.</summary>
    private static IEnumerable<Value> Smaller(Value v)
    {
        switch (v)
        {
            case IntValue i when i.V != 0:
                yield return new IntValue(0);
                yield return new IntValue(i.V / 2);
                yield return new IntValue(i.V - Math.Sign(i.V));
                break;
            case FloatValue f when f.V != 0:
                yield return new FloatValue(0);
                yield return new FloatValue(Math.Round(f.V / 2, 2));
                break;
            case StringValue s when s.V.Length > 0:
                yield return new StringValue("");
                yield return new StringValue(s.V[..(s.V.Length / 2)]);
                yield return new StringValue(s.V[1..]);
                break;
            case ListValue l when l.Items.Count > 0:
            {
                yield return ListValue.Empty;
                yield return new ListValue(Vector<Value>.From(l.Items.Take(l.Items.Count / 2)));
                for (var i = 0; i < l.Items.Count; i++)
                    yield return new ListValue(Vector<Value>.From(l.Items.Where((_, k) => k != i)));
                for (var i = 0; i < l.Items.Count; i++)
                    foreach (var smallerItem in Smaller(l.Items.Get(i)))
                        yield return new ListValue(Vector<Value>.From(l.Items.Select((x, k) => k == i ? smallerItem : x)));
                break;
            }
            case SetValue s when s.Items.Count > 0:
                yield return SetValue.Empty;
                foreach (var x in s.Items) yield return new SetValue(s.Items.Remove(x));
                break;
            case TupleValue t:
                for (var i = 0; i < t.Items.Length; i++)
                    foreach (var smallerItem in Smaller(t.Items[i]))
                        yield return new TupleValue(t.Items.SetItem(i, smallerItem));
                break;
            case RecordValue r when r.FieldValues.Length > 0 && !r.IsVariant:
                for (var i = 0; i < r.FieldValues.Length; i++)
                    foreach (var smallerItem in Smaller(r.FieldValues[i]))
                        yield return new RecordValue(r.TypeName, r.Union, r.FieldNames, r.FieldValues.SetItem(i, smallerItem));
                break;
            case RecordValue { Union: "Option", TypeName: "Some" } o:
                yield return Builtins.None;
                foreach (var smallerItem in Smaller(o.FieldValues[0])) yield return Builtins.Some(smallerItem);
                break;
        }
    }

    // ------------------------------------------------------------ reporting

    public static string Report(IReadOnlyList<Outcome> outcomes)
    {
        var sb = new StringBuilder();
        foreach (var o in outcomes)
        {
            if (o.Passed) sb.AppendLine($"  ok      {o.Module}.{o.Name} ({o.Cases} cases)");
            else if (o.Counterexample is not null)
                sb.AppendLine($"  FAILED  {o.Module}.{o.Name} after {o.Cases} case(s){(o.Error is null ? "" : ", " + o.Error)}\n          counterexample: {o.Counterexample}");
            else sb.AppendLine($"  SKIPPED {o.Module}.{o.Name}: {o.Error}");
        }
        var failed = outcomes.Count(o => !o.Passed && o.Counterexample is not null);
        sb.AppendLine($"{outcomes.Count(o => o.Passed)} passed, {failed} failed, {outcomes.Count(o => o.Counterexample is null && o.Error is not null)} skipped");
        return sb.ToString();
    }
}

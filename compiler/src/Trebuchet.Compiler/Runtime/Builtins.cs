using System.Collections.Immutable;
using System.Globalization;
using Trebuchet.Compiler.Syntax;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;

namespace Trebuchet.Compiler.Runtime;

/// <summary>The prototype standard library. Functions dispatch on the receiver where a name is shared.</summary>
public static class Builtins
{
    public static RecordValue Ok(Value v) => new("Ok", "Result", ImmutableArray.Create("value"), ImmutableArray.Create(v));
    public static RecordValue Error(Value e) => new("Error", "Result", ImmutableArray.Create("error"), ImmutableArray.Create(e));
    public static RecordValue Some(Value v) => new("Some", "Option", ImmutableArray.Create("value"), ImmutableArray.Create(v));
    public static readonly RecordValue None = new("None", "Option", ImmutableArray<string>.Empty, ImmutableArray<Value>.Empty);

    public static bool IsOk(Value v, out Value payload)
    {
        if (v is RecordValue { Union: "Result", TypeName: "Ok" } r) { payload = r.FieldValues[0]; return true; }
        payload = null!;
        return false;
    }

    public static bool IsError(Value v, out Value error)
    {
        if (v is RecordValue { Union: "Result", TypeName: "Error" } r) { error = r.FieldValues[0]; return true; }
        error = null!;
        return false;
    }

    public static Env CreateGlobalEnv()
    {
        var g = new Env(null, "global");

        void Def(string name, Func<Interpreter, IReadOnlyList<Value>, Value> impl) => g.Define(name, new Builtin(name, impl));
        Value Bool(bool b) => BoolValue.Of(b);

        g.Define("unit", UnitValue.Instance);
        g.Define("None", None);
        Def("Some", (_, a) => Some(Arg(a, 0, "Some")));
        Def("ok", (_, a) => Ok(a.Count == 0 ? UnitValue.Instance : a[0]));
        Def("error", (_, a) => Error(Arg(a, 0, "error")));
        g.Define("Ok", g.Lookup("ok"));
        g.Define("Error", g.Lookup("error"));
        Def("fail", (_, a) =>
        {
            var payload = Arg(a, 0, "fail");
            var message = payload is RecordValue { TypeName: "ArgumentError" } r && r.Get("message") is StringValue m ? m.V : payload.Show();
            throw new TrebPanic(message, payload);
        });
        Def("ArgumentError", (_, a) => new RecordValue("ArgumentError", null, ImmutableArray.Create("message"), ImmutableArray.Create(Arg(a, 0, "ArgumentError"))));
        Def("print", (_, a) =>
        {
            Console.WriteLine(string.Join(" ", a.Select(v => v is StringValue s ? s.V : v.Show())));
            return UnitValue.Instance;
        });
        Def("toString", (_, a) => new StringValue(Arg(a, 0, "toString") is StringValue s ? s.V : a[0].Show()));

        // ---- Result and Option
        Def("mapError", (it, a) =>
        {
            var r = Arg(a, 0, "mapError");
            var f = Arg(a, 1, "mapError");
            return IsError(r, out var e) ? Error(it.Call(f, new[] { e })) : r;
        });
        Def("isOk", (it, a) => Bool(IsOk(Arg(a, 0, "isOk"), out _)));
        Def("isError", (it, a) => Bool(IsError(Arg(a, 0, "isError"), out _)));
        Def("getOr", (it, a) =>
        {
            var target = Arg(a, 0, "getOr");
            if (target is MapValue m) return m.Entries.TryGetValue(Arg(a, 1, "getOr"), out var v) ? v : Arg(a, 2, "getOr");
            if (target is RecordValue { Union: "Option" } o) return o.TypeName == "Some" ? o.FieldValues[0] : Arg(a, 1, "getOr");
            if (IsOk(target, out var ok)) return ok;
            if (IsError(target, out _)) return Arg(a, 1, "getOr");
            throw new TrebPanic($"getOr: unsupported receiver {target.Show()}");
        });

        // ---- collections
        Def("append", (_, a) => new ListValue(List(a, 0, "append").Items.Append(Arg(a, 1, "append"))));
        Def("concat", (_, a) => new ListValue(List(a, 0, "concat").Items.Concat(List(a, 1, "concat").Items)));
        Def("length", (_, a) => Arg(a, 0, "length") switch
        {
            ListValue l => new IntValue(l.Items.Count),
            StringValue s => new IntValue(s.V.Length),
            MapValue m => new IntValue(m.Entries.Count),
            SetValue st => new IntValue(st.Items.Count),
            var v => throw new TrebPanic($"length: unsupported receiver {v.Show()}"),
        });
        Def("isEmpty", (_, a) => Arg(a, 0, "isEmpty") switch
        {
            ListValue l => Bool(l.Items.Count == 0),
            StringValue s => Bool(s.V.Length == 0),
            MapValue m => Bool(m.Entries.Count == 0),
            SetValue st => Bool(st.Items.Count == 0),
            var v => throw new TrebPanic($"isEmpty: unsupported receiver {v.Show()}"),
        });
        Def("map", (it, a) =>
        {
            var target = Arg(a, 0, "map");
            var f = Arg(a, 1, "map");
            if (target is ListValue l) return new ListValue(Vector<Value>.From(l.Items.Select(x => it.Call(f, new[] { x }))));
            if (target is SeqValue sq) return new SeqValue(() => sq.Items().Select(x => it.Call(f, new[] { x })));
            if (IsOk(target, out var ok)) return Ok(it.Call(f, new[] { ok }));
            if (IsError(target, out _)) return target;
            if (target is RecordValue { Union: "Option" } o) return o.TypeName == "Some" ? Some(it.Call(f, new[] { o.FieldValues[0] })) : o;
            throw new TrebPanic($"map: unsupported receiver {target.Show()}");
        });
        Def("filter", (it, a) => Arg(a, 0, "filter") is SeqValue sq
            ? new SeqValue(() => sq.Items().Where(x => Truthy(it.Call(Arg(a, 1, "filter"), new[] { x }), "filter")))
            : new ListValue(Vector<Value>.From(List(a, 0, "filter").Items.Where(x => Truthy(it.Call(Arg(a, 1, "filter"), new[] { x }), "filter")))));
        Def("takeWhile", (it, a) => new SeqValue(() => SeqArg(a, 0, "takeWhile").Items().TakeWhile(x => Truthy(it.Call(Arg(a, 1, "takeWhile"), new[] { x }), "takeWhile"))));
        Def("toVector", (_, a) => new ListValue(Vector<Value>.From(SeqArg(a, 0, "toVector").Items())));
        Def("fold", (it, a) =>
        {
            var acc = Arg(a, 1, "fold");
            var f = Arg(a, 2, "fold");
            var source = Arg(a, 0, "fold") is SeqValue sq ? sq.Items() : List(a, 0, "fold").Items;
            foreach (var x in source) acc = it.Call(f, new[] { acc, x });
            return acc;
        });
        Def("any", (it, a) => Bool(List(a, 0, "any").Items.Any(x => Truthy(it.Call(Arg(a, 1, "any"), new[] { x }), "any"))));
        Def("all", (it, a) => Bool(List(a, 0, "all").Items.All(x => Truthy(it.Call(Arg(a, 1, "all"), new[] { x }), "all"))));
        Def("find", (it, a) =>
        {
            var f = Arg(a, 1, "find");
            foreach (var x in List(a, 0, "find").Items)
                if (Truthy(it.Call(f, new[] { x }), "find")) return Some(x);
            return None;
        });
        Def("forEach", (it, a) =>
        {
            foreach (var x in List(a, 0, "forEach").Items) it.Call(Arg(a, 1, "forEach"), new[] { x });
            return UnitValue.Instance;
        });
        Def("take", (_, a) => Arg(a, 0, "take") is SeqValue sq
            ? new SeqValue(() => sq.Items().Take((int)Math.Max(0, ((IntValue)Arg(a, 1, "take")).V)))
            : new ListValue(Vector<Value>.From(List(a, 0, "take").Items.Take((int)Math.Max(0, ((IntValue)Arg(a, 1, "take")).V)))));
        Def("drop", (_, a) => Arg(a, 0, "drop") is SeqValue sq
            ? new SeqValue(() => sq.Items().Skip((int)Math.Max(0, ((IntValue)Arg(a, 1, "drop")).V)))
            : new ListValue(Vector<Value>.From(List(a, 0, "drop").Items.Skip((int)Math.Max(0, ((IntValue)Arg(a, 1, "drop")).V)))));
        Def("at", (_, a) =>
        {
            var l = List(a, 0, "at").Items;
            var i = ((IntValue)Arg(a, 1, "at")).V;
            return i >= 0 && i < l.Count ? Some(l.Get((int)i)) : None;
        });
        Def("sortBy", (it, a) =>
        {
            var f = Arg(a, 1, "sortBy");
            var keyed = List(a, 0, "sortBy").Items.Select(x => (key: it.Call(f, new[] { x }), item: x)).ToList();
            return new ListValue(Vector<Value>.From(keyed.OrderBy(p => p.key, ValueComparer.Instance).Select(p => p.item)));
        });
        // constrained builtins receive their Ord dictionary as a trailing argument
        Def("sort", (it, a) =>
        {
            var dict = Arg(a, 1, "sort");
            var items = List(a, 0, "sort").Items.ToList();
            var sorted = items.OrderBy(x => x, Comparer<Value>.Create((x, y) => Math.Sign(CompareWith(it, dict, x, y)))).ToList();
            return new ListValue(Vector<Value>.From(sorted));
        });
        Def("maximum", (it, a) =>
        {
            var dict = Arg(a, 1, "maximum");
            Value? best = null;
            foreach (var x in List(a, 0, "maximum").Items) if (best is null || CompareWith(it, dict, x, best) > 0) best = x;
            return best is null ? None : Some(best);
        });
        Def("minimum", (it, a) =>
        {
            var dict = Arg(a, 1, "minimum");
            Value? best = null;
            foreach (var x in List(a, 0, "minimum").Items) if (best is null || CompareWith(it, dict, x, best) < 0) best = x;
            return best is null ? None : Some(best);
        });
        Def("traverse", (it, a) =>
        {
            var f = Arg(a, 1, "traverse");
            var acc = Vector<Value>.Empty;
            foreach (var x in List(a, 0, "traverse").Items)
            {
                var r = it.Call(f, new[] { x });
                if (IsError(r, out _)) return r;
                if (!IsOk(r, out var okValue)) throw new TrebPanic($"traverse: the function returned {r.Show()}, not a Result");
                acc = acc.Append(okValue);
            }
            return Ok(new ListValue(acc));
        });
        Def("first", (_, a) =>
        {
            if (Arg(a, 0, "first") is SeqValue sq) { foreach (var x in sq.Items()) return Some(x); return None; }
            return List(a, 0, "first").Items is { Count: > 0 } l ? Some(l.Get(0)) : None;
        });
        Def("last", (_, a) => List(a, 0, "last").Items is { Count: > 0 } l ? Some(l.Get(l.Count - 1)) : None);
        Def("reverse", (_, a) => new ListValue(Vector<Value>.From(List(a, 0, "reverse").Items.AsEnumerable().Reverse())));
        Def("contains", (_, a) => Arg(a, 0, "contains") switch
        {
            ListValue l => Bool(l.Items.Contains(Arg(a, 1, "contains"))),
            MapValue m => Bool(m.Entries.ContainsKey(Arg(a, 1, "contains"))),
            SetValue st => Bool(st.Items.Contains(Arg(a, 1, "contains"))),
            StringValue s => Bool(s.V.Contains(Str(a, 1, "contains"))),
            var v => throw new TrebPanic($"contains: unsupported receiver {v.Show()}"),
        });
        Def("sum", (_, a) => new IntValue(List(a, 0, "sum").Items.Sum(x => x is IntValue i ? i.V : throw new TrebPanic("sum: non-integer element"))));

        // ---- maps and cells share get/set; dispatch on receiver
        Def("get", (_, a) => Arg(a, 0, "get") switch
        {
            CellValue c => c.Current,
            MapValue m => m.Entries.TryGetValue(Arg(a, 1, "get"), out var v) ? Some(v) : None,
            var v => throw new TrebPanic($"get: unsupported receiver {v.Show()}"),
        });
        Def("set", (_, a) =>
        {
            switch (Arg(a, 0, "set"))
            {
                case CellValue c:
                    c.Current = Arg(a, 1, "set");
                    return UnitValue.Instance;
                case MapValue m:
                    return new MapValue(m.Entries.Set(Arg(a, 1, "set"), Arg(a, 2, "set")));
                case var v:
                    throw new TrebPanic($"set: unsupported receiver {v.Show()}");
            }
        });
        Def("remove", (_, a) => Arg(a, 0, "remove") is SetValue st
            ? new SetValue(st.Items.Remove(Arg(a, 1, "remove")))
            : new MapValue(Map(a, 0, "remove").Entries.Remove(Arg(a, 1, "remove"))));

        // ---- sets
        Def("toSet", (_, a) => new SetValue(Set<Value>.From(List(a, 0, "toSet").Items)));
        Def("add", (_, a) => new SetValue(SetArg(a, 0, "add").Items.Add(Arg(a, 1, "add"))));
        Def("items", (_, a) => new ListValue(Vector<Value>.From(SetArg(a, 0, "items").Items)));
        Def("merge", (_, a) => new SetValue(SetArg(a, 0, "merge").Items.Union(SetArg(a, 1, "merge").Items)));
        Def("intersect", (_, a) => new SetValue(SetArg(a, 0, "intersect").Items.Intersect(SetArg(a, 1, "intersect").Items)));
        Def("difference", (_, a) => new SetValue(SetArg(a, 0, "difference").Items.Difference(SetArg(a, 1, "difference").Items)));
        Def("keys", (_, a) => new ListValue(Vector<Value>.From(Map(a, 0, "keys").Entries.Keys)));
        Def("values", (_, a) => new ListValue(Vector<Value>.From(Map(a, 0, "values").Entries.Values)));
        Def("update", (it, a) =>
        {
            var c = Cell(a, 0, "update");
            c.Current = it.Call(Arg(a, 1, "update"), new[] { c.Current });
            return UnitValue.Instance;
        });
        Def("getAndUpdate", (it, a) =>
        {
            var c = Cell(a, 0, "getAndUpdate");
            var old = c.Current;
            c.Current = it.Call(Arg(a, 1, "getAndUpdate"), new[] { old });
            return old;
        });
        var seq = new Env(null, "Seq");
        seq.Define("from", new Builtin("Seq.from", (_, a) => { var items = List(a, 0, "Seq.from").Items; return new SeqValue(() => items); }));
        seq.Define("iterate", new Builtin("Seq.iterate", (it, a) =>
        {
            var seed = Arg(a, 0, "Seq.iterate");
            var f = Arg(a, 1, "Seq.iterate");
            return new SeqValue(() => Iterate(it, seed, f));
        }));
        seq.Define("range", new Builtin("Seq.range", (_, a) =>
        {
            var from = ((IntValue)Arg(a, 0, "Seq.range")).V;
            var to = ((IntValue)Arg(a, 1, "Seq.range")).V;
            return new SeqValue(() => Enumerable.Range(0, (int)Math.Max(0, to - from)).Select(i => (Value)new IntValue(from + i)));
        }));
        g.Define("Seq", new NamespaceValue("Seq", seq));

        var cell = new Env(null, "Cell");
        cell.Define("new", new Builtin("Cell.new", (_, a) => new CellValue(Arg(a, 0, "Cell.new"))));
        g.Define("Cell", new NamespaceValue("Cell", cell));

        // ---- strings
        Def("trim", (_, a) => new StringValue(Str(a, 0, "trim").Trim()));
        Def("toUpper", (_, a) => new StringValue(Str(a, 0, "toUpper").ToUpperInvariant()));
        Def("toLower", (_, a) => new StringValue(Str(a, 0, "toLower").ToLowerInvariant()));
        Def("startsWith", (_, a) => Bool(Str(a, 0, "startsWith").StartsWith(Str(a, 1, "startsWith"), StringComparison.Ordinal)));

        // ---- time
        var instant = new Env(null, "Instant");
        instant.Define("parse", new Builtin("Instant.parse", (_, a) =>
        {
            var s = Str(a, 0, "Instant.parse");
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? new InstantValue(d)
                : throw new TrebPanic($"Instant.parse: cannot parse \"{s}\"");
        }));
        instant.Define("now", new Builtin("Instant.now", (_, _) => new InstantValue(DateTimeOffset.UtcNow)));
        g.Define("Instant", new NamespaceValue("Instant", instant));
        var sys = new Env(null, "sys");
        sys.Define("clock", new Builtin("sys.clock", (_, _) => new InstantValue(DateTimeOffset.UtcNow)));
        g.Define("sys", new NamespaceValue("sys", sys));
        Def("uuid", (_, _) => new StringValue(Guid.NewGuid().ToString()));
        Def("sleep", (_, a) => { Thread.Sleep((int)Math.Max(0, ((IntValue)Arg(a, 0, "sleep")).V)); return UnitValue.Instance; });

        // ---- environment and json (minimal)
        var envNs = new Env(null, "env");
        envNs.Define("get", new Builtin("env.get", (_, a) =>
        {
            var name = Str(a, 0, "env.get");
            return Environment.GetEnvironmentVariable(name) is { } v ? new StringValue(v) : throw new TrebPanic($"env.get: {name} is not set");
        }));
        g.Define("env", new NamespaceValue("env", envNs));
        var json = new Env(null, "json");
        json.Define("encode", new Builtin("json.encode", (_, a) => new StringValue(Arg(a, 0, "json.encode").Show())));
        json.Define("decode", new Builtin("json.decode", (_, _) => throw new TrebPanic("json.decode is not available in the interpreter")));
        g.Define("json", new NamespaceValue("json", json));

        return g;
    }

    private static Value Lookup(this Env env, string name) => env.TryGet(name, out var v) ? v : throw new InvalidOperationException(name);

    private static Value Arg(IReadOnlyList<Value> a, int i, string fn) =>
        i < a.Count ? a[i] : throw new TrebPanic($"{fn}: expected at least {i + 1} argument(s) but got {a.Count}");

    private static ListValue List(IReadOnlyList<Value> a, int i, string fn) =>
        Arg(a, i, fn) as ListValue ?? throw new TrebPanic($"{fn}: argument {i + 1} is not a list: {a[i].Show()}");

    private static MapValue Map(IReadOnlyList<Value> a, int i, string fn) =>
        Arg(a, i, fn) as MapValue ?? throw new TrebPanic($"{fn}: argument {i + 1} is not a map: {a[i].Show()}");

    /// <summary>The built-in Ord instance: compare over primitives, as a namespace value with a compare member.</summary>
    public static readonly NamespaceValue OrdPrimitive = MakeOrdPrimitive();
    private static NamespaceValue MakeOrdPrimitive()
    {
        var env = new Env(null, "Ord");
        env.Define("compare", new Builtin("Ord.compare", (_, a) => new IntValue(ValueComparer.Instance.Compare(Arg(a, 0, "compare"), Arg(a, 1, "compare")))));
        return new NamespaceValue("Ord", env);
    }

    /// <summary>Compares two values through an Ord dictionary: a namespace with a compare member.</summary>
    public static long CompareWith(Interpreter it, Value dict, Value x, Value y)
    {
        if (dict is not NamespaceValue ns || !ns.Members.TryGet("compare", out var compare))
            throw new TrebPanic($"expected an Ord instance but found {dict.Show()}");
        return ((IntValue)it.Call(compare, new[] { x, y })).V;
    }

    /// <summary>Ordering for sortBy keys: numbers, strings (ordinal), instants, and booleans.</summary>
    private sealed class ValueComparer : IComparer<Value>
    {
        public static readonly ValueComparer Instance = new();
        public int Compare(Value? x, Value? y) => (x, y) switch
        {
            (IntValue a, IntValue b) => a.V.CompareTo(b.V),
            (FloatValue a, FloatValue b) => a.V.CompareTo(b.V),
            (IntValue a, FloatValue b) => ((double)a.V).CompareTo(b.V),
            (FloatValue a, IntValue b) => a.V.CompareTo((double)b.V),
            (StringValue a, StringValue b) => string.CompareOrdinal(a.V, b.V),
            (InstantValue a, InstantValue b) => a.V.CompareTo(b.V),
            (BoolValue a, BoolValue b) => a.V.CompareTo(b.V),
            _ => throw new TrebPanic($"sortBy: cannot order {x?.Show()} against {y?.Show()}"),
        };
    }

    private static IEnumerable<Value> Iterate(Interpreter it, Value seed, Value f)
    {
        var current = seed;
        while (true)
        {
            yield return current;
            current = it.Call(f, new[] { current });
        }
    }

    private static SeqValue SeqArg(IReadOnlyList<Value> a, int i, string fn) =>
        Arg(a, i, fn) as SeqValue ?? throw new TrebPanic($"{fn}: argument {i + 1} is not a sequence: {a[i].Show()}");

    private static SetValue SetArg(IReadOnlyList<Value> a, int i, string fn) =>
        Arg(a, i, fn) as SetValue ?? throw new TrebPanic($"{fn}: argument {i + 1} is not a set: {a[i].Show()}");

    private static CellValue Cell(IReadOnlyList<Value> a, int i, string fn) =>
        Arg(a, i, fn) as CellValue ?? throw new TrebPanic($"{fn}: argument {i + 1} is not a Cell: {a[i].Show()}");

    private static string Str(IReadOnlyList<Value> a, int i, string fn) =>
        (Arg(a, i, fn) as StringValue)?.V ?? throw new TrebPanic($"{fn}: argument {i + 1} is not a string: {a[i].Show()}");

    private static bool Truthy(Value v, string fn) =>
        v is BoolValue b ? b.V : throw new TrebPanic($"{fn}: predicate returned {v.Show()}, not a Bool");

    public static Value BinaryOp(string op, Value l, Value r, Position pos)
    {
        switch (op)
        {
            case "==": return BoolValue.Of(l.Equals(r));
            case "!=": return BoolValue.Of(!l.Equals(r));
        }
        if (op == "+" && (l is StringValue || r is StringValue))
            return new StringValue(AsText(l) + AsText(r));
        if (l is IntValue li && r is IntValue ri)
        {
            return op switch
            {
                "+" => new IntValue(li.V + ri.V),
                "-" => new IntValue(li.V - ri.V),
                "*" => new IntValue(li.V * ri.V),
                "/" => ri.V == 0 ? throw new TrebPanic($"{pos}: division by zero") : new IntValue(li.V / ri.V),
                "%" => ri.V == 0 ? throw new TrebPanic($"{pos}: division by zero") : new IntValue(li.V % ri.V),
                "<" => BoolValue.Of(li.V < ri.V),
                "<=" => BoolValue.Of(li.V <= ri.V),
                ">" => BoolValue.Of(li.V > ri.V),
                ">=" => BoolValue.Of(li.V >= ri.V),
                _ => throw new TrebPanic($"{pos}: unknown operator {op}"),
            };
        }
        if (l is IntValue or FloatValue && r is IntValue or FloatValue)
        {
            var a = l is IntValue la ? la.V : ((FloatValue)l).V;
            var b = r is IntValue rb ? rb.V : ((FloatValue)r).V;
            return op switch
            {
                "+" => new FloatValue(a + b),
                "-" => new FloatValue(a - b),
                "*" => new FloatValue(a * b),
                "/" => new FloatValue(a / b),
                "%" => new FloatValue(a % b),
                "<" => BoolValue.Of(a < b),
                "<=" => BoolValue.Of(a <= b),
                ">" => BoolValue.Of(a > b),
                ">=" => BoolValue.Of(a >= b),
                _ => throw new TrebPanic($"{pos}: unknown operator {op}"),
            };
        }
        if (l is StringValue ls && r is StringValue rs)
        {
            var c = string.CompareOrdinal(ls.V, rs.V);
            return op switch
            {
                "<" => BoolValue.Of(c < 0),
                "<=" => BoolValue.Of(c <= 0),
                ">" => BoolValue.Of(c > 0),
                ">=" => BoolValue.Of(c >= 0),
                _ => throw new TrebPanic($"{pos}: operator {op} is not defined on strings"),
            };
        }
        if (l is InstantValue lt && r is InstantValue rt)
        {
            return op switch
            {
                "<" => BoolValue.Of(lt.V < rt.V),
                "<=" => BoolValue.Of(lt.V <= rt.V),
                ">" => BoolValue.Of(lt.V > rt.V),
                ">=" => BoolValue.Of(lt.V >= rt.V),
                _ => throw new TrebPanic($"{pos}: operator {op} is not defined on instants"),
            };
        }
        throw new TrebPanic($"{pos}: operator {op} is not defined on {l.Show()} and {r.Show()}");
    }

    public static string AsText(Value v) => v is StringValue s ? s.V : v.Show();
}

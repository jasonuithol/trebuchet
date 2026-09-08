using System.Collections.Immutable;
using System.Text;
using Trebuchet.Compiler.Syntax;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;

namespace Trebuchet.Compiler.Runtime;

/// <summary>Runtime values. Everything except <see cref="CellValue"/> is immutable with structural equality.</summary>
public abstract class Value
{
    public abstract string Show();
    public override string ToString() => Show();
}

public sealed class IntValue : Value
{
    public long V { get; }
    public IntValue(long v) => V = v;
    public override string Show() => V.ToString();
    public override bool Equals(object? o) => o is IntValue i && i.V == V;
    public override int GetHashCode() => V.GetHashCode();
}

public sealed class FloatValue : Value
{
    public double V { get; }
    public FloatValue(double v) => V = v;
    public override string Show() => V.ToString("R");
    public override bool Equals(object? o) => o is FloatValue f && f.V.Equals(V);
    public override int GetHashCode() => V.GetHashCode();
}

public sealed class BoolValue : Value
{
    public static readonly BoolValue True = new(true);
    public static readonly BoolValue False = new(false);
    public bool V { get; }
    private BoolValue(bool v) => V = v;
    public static BoolValue Of(bool b) => b ? True : False;
    public override string Show() => V ? "true" : "false";
    public override bool Equals(object? o) => o is BoolValue b && b.V == V;
    public override int GetHashCode() => V.GetHashCode();
}

public sealed class StringValue : Value
{
    public string V { get; }
    public StringValue(string v) => V = v;
    public override string Show() => "\"" + V.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    public override bool Equals(object? o) => o is StringValue s && s.V == V;
    public override int GetHashCode() => V.GetHashCode();
}

public sealed class UnitValue : Value
{
    public static readonly UnitValue Instance = new();
    private UnitValue() { }
    public override string Show() => "unit";
    public override bool Equals(object? o) => o is UnitValue;
    public override int GetHashCode() => 1;
}

public sealed class InstantValue : Value
{
    public DateTimeOffset V { get; }
    public InstantValue(DateTimeOffset v) => V = v.ToUniversalTime();
    public override string Show() => V.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'");
    public override bool Equals(object? o) => o is InstantValue i && i.V == V;
    public override int GetHashCode() => V.GetHashCode();
}

/// <summary>A record instance, or a union variant instance when <see cref="Union"/> is set.</summary>
public sealed class RecordValue : Value
{
    public string TypeName { get; }
    public string? Union { get; }
    public ImmutableArray<string> FieldNames { get; }
    public ImmutableArray<Value> FieldValues { get; }

    public RecordValue(string typeName, string? union, ImmutableArray<string> names, ImmutableArray<Value> values)
    {
        TypeName = typeName;
        Union = union;
        FieldNames = names;
        FieldValues = values;
    }

    public bool IsVariant => Union is not null;

    public int IndexOf(string field) => FieldNames.IndexOf(field);

    public Value? Get(string field)
    {
        var i = IndexOf(field);
        return i < 0 ? null : FieldValues[i];
    }

    public RecordValue With(string field, Value value)
    {
        var i = IndexOf(field);
        if (i < 0) throw new TrebPanic($"{TypeName} has no field '{field}'");
        return new RecordValue(TypeName, Union, FieldNames, FieldValues.SetItem(i, value));
    }

    public override string Show()
    {
        if (FieldValues.Length == 0) return TypeName;
        var sb = new StringBuilder(TypeName).Append('(');
        for (var i = 0; i < FieldValues.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(FieldNames[i]).Append(": ").Append(FieldValues[i].Show());
        }
        return sb.Append(')').ToString();
    }

    public override bool Equals(object? o)
    {
        if (o is not RecordValue r || r.TypeName != TypeName || r.Union != Union || r.FieldValues.Length != FieldValues.Length) return false;
        for (var i = 0; i < FieldValues.Length; i++)
            if (!FieldValues[i].Equals(r.FieldValues[i])) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(TypeName);
        foreach (var v in FieldValues) h.Add(v);
        return h.ToHashCode();
    }
}

/// <summary>A lazy, re-iterable, possibly infinite sequence. Reference identity: a sequence is not a value you compare.</summary>
public sealed class SeqValue : Value
{
    public Func<IEnumerable<Value>> Items { get; }
    public SeqValue(Func<IEnumerable<Value>> items) => Items = items;
    public override string Show() => "<seq>";
}

public sealed class TupleValue : Value
{
    public ImmutableArray<Value> Items { get; }
    public TupleValue(ImmutableArray<Value> items) => Items = items;
    public override string Show() => "(" + string.Join(", ", Items.Select(i => i.Show())) + ")";
    public override bool Equals(object? o) => o is TupleValue t && t.Items.SequenceEqual(Items);
    public override int GetHashCode() { var h = new HashCode(); foreach (var i in Items) h.Add(i); return h.ToHashCode(); }
}

public sealed class ListValue : Value
{
    public static readonly ListValue Empty = new(Vector<Value>.Empty);
    public Vector<Value> Items { get; }
    public ListValue(Vector<Value> items) => Items = items;
    public override string Show() => "[" + string.Join(", ", Items.Select(i => i.Show())) + "]";
    public override bool Equals(object? o) => o is ListValue l && l.Items.Equals(Items);
    public override int GetHashCode() => Items.GetHashCode();
}

public sealed class MapValue : Value
{
    public static readonly MapValue Empty = new(Map<Value, Value>.Empty);
    public Map<Value, Value> Entries { get; }
    public MapValue(Map<Value, Value> entries) => Entries = entries;
    public override string Show() => "{" + string.Join(", ", Entries.Select(e => $"{e.Key.Show()}: {e.Value.Show()}")) + "}";
    public override bool Equals(object? o) => o is MapValue m && m.Entries.Equals(Entries);
    public override int GetHashCode() => Entries.GetHashCode();
}

public sealed class SetValue : Value
{
    public static readonly SetValue Empty = new(Set<Value>.Empty);
    public Set<Value> Items { get; }
    public SetValue(Set<Value> items) => Items = items;
    public override string Show() => "#{" + string.Join(", ", Items.Select(i => i.Show())) + "}";
    public override bool Equals(object? o) => o is SetValue s && s.Items.Equals(Items);
    public override int GetHashCode() => Items.GetHashCode();
}

/// <summary>The one mutable primitive. Reference identity.</summary>
public sealed class CellValue : Value
{
    public Value Current { get; set; }
    public CellValue(Value initial) => Current = initial;
    public override string Show() => $"Cell({Current.Show()})";
}

public abstract class FunctionValue : Value
{
    public abstract string Name { get; }
    public override string Show() => $"<fn {Name}>";
}

/// <summary>A user function or lambda closed over its defining environment.</summary>
public sealed class Closure : FunctionValue
{
    public override string Name { get; }
    public IReadOnlyList<string> Params { get; }
    public Block Body { get; }
    public Env Env { get; }
    public Closure(string name, IReadOnlyList<string> parameters, Block body, Env env)
    {
        Name = name;
        Params = parameters;
        Body = body;
        Env = env;
    }
}

public sealed class Builtin : FunctionValue
{
    public override string Name { get; }
    public Func<Interpreter, IReadOnlyList<Value>, Value> Impl { get; }
    public Builtin(string name, Func<Interpreter, IReadOnlyList<Value>, Value> impl)
    {
        Name = name;
        Impl = impl;
    }
}

/// <summary>Constructor for a record type, or for a union variant that carries fields.</summary>
public sealed class ConstructorValue : FunctionValue
{
    public override string Name { get; }
    public string? Union { get; }
    public IReadOnlyList<FieldDecl> Fields { get; }
    public Env Env { get; }
    public ConstructorValue(string name, string? union, IReadOnlyList<FieldDecl> fields, Env env)
    {
        Name = name;
        Union = union;
        Fields = fields;
        Env = env;
    }
}

/// <summary>A service type. Calling it constructs an instance.</summary>
public sealed class ServiceType : FunctionValue
{
    public override string Name => Decl.Name;
    public ServiceDecl Decl { get; }
    public Env ModuleEnv { get; }
    public ServiceType(ServiceDecl decl, Env moduleEnv)
    {
        Decl = decl;
        ModuleEnv = moduleEnv;
    }
}

public sealed class ServiceInstance : Value
{
    public ServiceType Type { get; }
    /// <summary>Dependencies and bound methods, parented to the module environment.</summary>
    public Env Env { get; }
    public ServiceInstance(ServiceType type, Env env)
    {
        Type = type;
        Env = env;
    }
    public override string Show() => $"<service {Type.Name}>";
}

/// <summary>A namespace: a module referenced by short name, a union, or a builtin type like Instant.</summary>
public sealed class NamespaceValue : Value
{
    public string Name { get; }
    public Env Members { get; }
    public NamespaceValue(string name, Env members)
    {
        Name = name;
        Members = members;
    }
    public override string Show() => $"<namespace {Name}>";
}

/// <summary>Unrecoverable failure. Caught only at supervisor points.</summary>
public sealed class TrebPanic : Exception
{
    public Value? Payload { get; }
    public TrebPanic(string message, Value? payload = null) : base(message) => Payload = payload;
}

/// <summary>Raised by <c>expr?</c> on an Error; caught by the enclosing function call.</summary>
public sealed class PropagateSignal : Exception
{
    public Value Error { get; }
    public PropagateSignal(Value error) : base("propagate") => Error = error;
}

public sealed class Env
{
    private readonly Dictionary<string, Value> _vars = new();
    public Env? Parent { get; }
    public string Label { get; }

    public Env(Env? parent, string label = "")
    {
        Parent = parent;
        Label = label;
    }

    public void Define(string name, Value value) => _vars[name] = value;

    public bool TryGet(string name, out Value value)
    {
        for (var e = this; e is not null; e = e.Parent)
            if (e._vars.TryGetValue(name, out value!)) return true;
        value = null!;
        return false;
    }

    public bool HasLocal(string name) => _vars.ContainsKey(name);

    public IEnumerable<KeyValuePair<string, Value>> Locals => _vars;
}

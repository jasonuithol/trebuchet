using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;

namespace Trebuchet.Compiler.Runtime;

/// <summary>
/// The interpreter's JSON boundary, the same shape as the .NET runtime's converter: a nested
/// single-field record is its value, a nullary variant is its name, a variant with fields
/// carries "type", Option is null or the value, and field names match leniently, so
/// full_name in a document binds fullName in a record. Missing Option fields are None.
/// </summary>
public static class JsonValues
{
    public static JsonNode? ToJson(Value v) => ToJson(v, 0);

    private static JsonNode? ToJson(Value v, int depth) => v switch
    {
        IntValue i => JsonValue.Create(i.V),
        FloatValue f => JsonValue.Create(f.V),
        BoolValue b => JsonValue.Create(b.V),
        StringValue s => JsonValue.Create(s.V),
        UnitValue => null,
        InstantValue t => JsonValue.Create(t.Show()),
        ListValue l => new JsonArray(l.Items.Select(x => ToJson(x, depth + 1)).ToArray()),
        SetValue st => new JsonArray(st.Items.Select(x => ToJson(x, depth + 1)).ToArray()),
        SeqValue sq => new JsonArray(sq.Items().Select(x => ToJson(x, depth + 1)).ToArray()),
        TupleValue tp => new JsonArray(tp.Items.Select(x => ToJson(x, depth + 1)).ToArray()),
        MapValue m => MapToJson(m, depth),
        CellValue c => ToJson(c.Current, depth),
        RecordValue { Union: "Option" } o => o.TypeName == "Some" ? ToJson(o.FieldValues[0], depth) : null,
        RecordValue r when r.IsVariant && r.FieldValues.Length == 0 => JsonValue.Create(r.TypeName),
        RecordValue r when depth > 0 && !r.IsVariant && r.FieldValues.Length == 1 && r.FieldValues[0] is StringValue or IntValue => ToJson(r.FieldValues[0], depth),
        RecordValue r => RecordToJson(r, depth),
        _ => JsonValue.Create(v.Show()),
    };

    private static JsonObject RecordToJson(RecordValue r, int depth)
    {
        var obj = new JsonObject();
        if (r.IsVariant) obj["type"] = r.TypeName;
        for (var i = 0; i < r.FieldValues.Length; i++) obj[r.FieldNames[i]] = ToJson(r.FieldValues[i], depth + 1);
        return obj;
    }

    private static JsonNode MapToJson(MapValue m, int depth)
    {
        var scalar = m.Entries.All(kv => kv.Key is StringValue or IntValue or RecordValue { FieldValues.Length: 1 });
        if (scalar)
        {
            var obj = new JsonObject();
            foreach (var kv in m.Entries)
            {
                var key = kv.Key switch
                {
                    StringValue s => s.V,
                    RecordValue { FieldValues.Length: 1 } r when r.FieldValues[0] is StringValue ks => ks.V,
                    var other => other.Show(),
                };
                obj[key] = ToJson(kv.Value, depth + 1);
            }
            return obj;
        }
        return new JsonArray(m.Entries.Select(kv => (JsonNode)new JsonObject { ["key"] = ToJson(kv.Key, depth + 1), ["value"] = ToJson(kv.Value, depth + 1) }).ToArray());
    }

    // ------------------------------------------------------------ reading

    public static Value FromJson(Interpreter it, TType type, JsonNode? node, string where)
    {
        var t = Unifier.Prune(type);
        switch (t)
        {
            case PrimT { Name: "String" }:
                return new StringValue(node is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? throw Missing(where));
            case PrimT { Name: "Int" }:
                if (node is JsonValue iv && iv.TryGetValue<long>(out var l)) return new IntValue(l);
                if (node is JsonValue ivd && ivd.TryGetValue<double>(out var ld) && Math.Floor(ld) == ld) return new IntValue((long)ld);
                throw new TrebPanic($"{where}: expected an integer");
            case PrimT { Name: "Float" }:
                if (node is JsonValue fv && fv.TryGetValue<double>(out var d)) return new FloatValue(d);
                throw new TrebPanic($"{where}: expected a number");
            case PrimT { Name: "Bool" }:
                if (node is JsonValue bv && bv.TryGetValue<bool>(out var b)) return BoolValue.Of(b);
                throw new TrebPanic($"{where}: expected true or false");
            case PrimT { Name: "Instant" }:
            {
                var text = node is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : throw new TrebPanic($"{where}: expected an ISO-8601 instant string");
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                    ? new InstantValue(dt) : throw new TrebPanic($"{where}: cannot parse instant \"{text}\"");
            }
            case PrimT { Name: "Unit" }: return UnitValue.Instance;
            case AppT { Ctor: "Option" } o:
                return node is null || node.GetValueKind() == JsonValueKind.Null ? Builtins.None : Builtins.Some(FromJson(it, o.Args[0], node, where));
            case AppT { Ctor: "Vector" } v:
            {
                if (node is not JsonArray arr) throw new TrebPanic($"{where}: expected an array");
                return new ListValue(Vector<Value>.From(arr.Select((x, i) => FromJson(it, v.Args[0], x, $"{where}[{i}]"))));
            }
            case AppT { Ctor: "Set" } st:
            {
                if (node is not JsonArray arr) throw new TrebPanic($"{where}: expected an array");
                return new SetValue(Set<Value>.From(arr.Select((x, i) => FromJson(it, st.Args[0], x, $"{where}[{i}]"))));
            }
            case AppT { Ctor: "Map" } mp:
            {
                var m = Map<Value, Value>.Empty;
                if (node is JsonObject obj)
                    foreach (var kv in obj) m = m.Set(FromJson(it, mp.Args[0], JsonValue.Create(kv.Key), $"{where}.{kv.Key}"), FromJson(it, mp.Args[1], kv.Value, $"{where}.{kv.Key}"));
                else if (node is JsonArray pairs)
                    foreach (var pair in pairs.OfType<JsonObject>()) m = m.Set(FromJson(it, mp.Args[0], pair["key"], where), FromJson(it, mp.Args[1], pair["value"], where));
                else throw new TrebPanic($"{where}: expected an object");
                return new MapValue(m);
            }
            case TupleT tt:
            {
                if (node is not JsonArray arr || arr.Count != tt.Items.Count) throw new TrebPanic($"{where}: expected an array of {tt.Items.Count}");
                return new TupleValue(tt.Items.Select((item, i) => FromJson(it, item, arr[i], $"{where}[{i}]")).ToImmutableArray());
            }
            case RecordT { Union: null } rec:
            {
                var ctor = FindValue(it, rec.Name) as ConstructorValue ?? throw new TrebPanic($"{where}: cannot find record {rec.Name}");
                // a single-field record binds from a bare scalar
                if (rec.Fields.Count == 1 && node is JsonValue) return it.Call(ctor, new[] { FromJson(it, rec.Fields[0].Type, node, where) });
                if (node is not JsonObject obj) throw new TrebPanic($"{where}: expected an object for {rec.Name}");
                return it.Call(ctor, rec.Fields.Select(f => Field(it, obj, f.Name, f.Type, where)).ToList());
            }
            case UnionT u:
            {
                string variantName;
                JsonObject? obj = null;
                if (node is JsonValue nv && nv.TryGetValue<string>(out var vs)) variantName = vs;
                else if (node is JsonObject o2 && o2.TryGetPropertyValue("type", out var tn) && tn is JsonValue tnv && tnv.TryGetValue<string>(out var tns)) { variantName = tns; obj = o2; }
                else throw new TrebPanic($"{where}: expected a variant name or an object with a \"type\" field for {u.Name}");
                var variant = u.Variants.FirstOrDefault(x => x.Name == variantName) ?? throw new TrebPanic($"{where}: {u.Name} has no variant '{variantName}'");
                var value = FindValue(it, variant.Name) ?? throw new TrebPanic($"{where}: cannot find variant {variant.Name}");
                if (variant.Fields.Count == 0) return value;
                if (obj is null) throw new TrebPanic($"{where}: variant {variantName} needs its fields");
                return it.Call(value, variant.Fields.Select(f => Field(it, obj, f.Name, f.Type, where)).ToList());
            }
            case RecordT { Union: { } owner }:
                return FromJson(it, owner, node, where);
            default:
                throw new TrebPanic($"{where}: cannot bind type {Unifier.Show(t)} from JSON");
        }
    }

    /// <summary>A field by lenient name: exact, then ignoring case and underscores. A missing Option is None.</summary>
    private static Value Field(Interpreter it, JsonObject obj, string field, TType type, string where)
    {
        if (!obj.TryGetPropertyValue(field, out var node))
        {
            var key = Normalise(field);
            var match = obj.FirstOrDefault(kv => Normalise(kv.Key) == key);
            if (match.Key is null)
            {
                if (Unifier.Prune(type) is AppT { Ctor: "Option" }) return Builtins.None;
                throw new TrebPanic($"{where}.{field}: missing");
            }
            node = match.Value;
        }
        return FromJson(it, type, node, $"{where}.{field}");
    }

    public static string Normalise(string name) => name.Replace("_", "").ToLowerInvariant();

    private static Value? FindValue(Interpreter it, string name)
    {
        foreach (var m in it.Modules.Modules)
            if (it.EnvOf(m).TryGet(name, out var v)) return v;
        return null;
    }

    private static TrebPanic Missing(string where) => new($"{where}: missing");
}

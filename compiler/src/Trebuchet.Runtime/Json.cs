using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trebuchet.Runtime;

/// <summary>Marks a generated Trebuchet record.</summary>
[AttributeUsage(AttributeTargets.Class)] public sealed class TrebuchetRecordAttribute : Attribute { }
/// <summary>Marks the abstract base of a generated Trebuchet union.</summary>
[AttributeUsage(AttributeTargets.Class)] public sealed class TrebuchetUnionAttribute : Attribute { }
/// <summary>Marks a generated union variant.</summary>
[AttributeUsage(AttributeTargets.Class)] public sealed class TrebuchetVariantAttribute : Attribute { }

/// <summary>
/// JSON shape at the host boundary, the same shape the interpreter's dev server uses:
/// a single-field record flattens to its value when nested and stays an object at the top
/// level; a nullary variant is its name; a variant with fields is an object with a
/// <c>type</c> field; <c>Option</c> is null or the value; <c>Map</c> is an object when the
/// key is a string, a number, or a single-field record over one, and an array of
/// <c>{key, value}</c> pairs otherwise; <c>Vector</c> and <c>Set</c> are arrays.
/// </summary>
public static class TrebuchetJson
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new TrebuchetJsonConverter());
        options.PropertyNameCaseInsensitive = true;
        return options;
    }

    private static JsonSerializerOptions? _options;
    /// <summary>One configured instance; JsonSerializerOptions caches converters and metadata, so reuse matters.</summary>
    public static JsonSerializerOptions Options => _options ??= Configure(new JsonSerializerOptions());

    /// <summary>A field name for lenient matching: full_name, fullName, and FullName are the same field.</summary>
    public static string Normalise(string name) => name.Replace("_", "").ToLowerInvariant();
}

public sealed class TrebuchetJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type t)
    {
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(Option<>) || def == typeof(Map<,>) || def == typeof(Set<>) || def == typeof(Vector<>)) return true;
        }
        if (t.IsDefined(typeof(TrebuchetUnionAttribute), false) || t.IsDefined(typeof(TrebuchetVariantAttribute), false)) return true;
        return t.IsDefined(typeof(TrebuchetRecordAttribute), false) && Ctor(t) is { } c && c.GetParameters().Length > 0;
    }

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options)
    {
        Type converter;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Option<>)) converter = typeof(OptionConverter<>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Map<,>)) converter = typeof(MapConverter<,>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Set<>)) converter = typeof(SetConverter<>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Vector<>)) converter = typeof(VectorConverter<>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsDefined(typeof(TrebuchetUnionAttribute), false) || t.IsDefined(typeof(TrebuchetVariantAttribute), false)) converter = typeof(UnionConverter<>).MakeGenericType(t);
        else if (SingleField(t) is not null) converter = typeof(SingleFieldConverter<>).MakeGenericType(t);
        else converter = typeof(RecordConverter<>).MakeGenericType(t);
        return (JsonConverter)Activator.CreateInstance(converter)!;
    }

    internal static ConstructorInfo? Ctor(Type t) => t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();

    /// <summary>The one constructor parameter and matching property of a single-field record, else null.</summary>
    internal static (ParameterInfo Param, PropertyInfo Prop)? SingleField(Type t)
    {
        var ctor = Ctor(t);
        if (ctor is null || ctor.GetParameters().Length != 1) return null;
        var p = ctor.GetParameters()[0];
        var prop = t.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop is null ? null : (p, prop);
    }

    /// <summary>A key that can be a JSON property name: string, number, bool, or a single-field record over one.</summary>
    internal static bool IsScalarKey(Type k) =>
        k == typeof(string) || k.IsPrimitive || k == typeof(decimal) || k == typeof(DateTimeOffset) || k == typeof(Guid)
        || (k.IsDefined(typeof(TrebuchetRecordAttribute), false) && SingleField(k) is { } f && IsScalarKey(f.Prop.PropertyType));

    internal static string KeyToString(object key) => key switch
    {
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ when SingleField(key.GetType()) is { } sf => KeyToString(sf.Prop.GetValue(key)!),
        _ => key.ToString() ?? "",
    };

    internal static object KeyFromString(string s, Type k)
    {
        if (k == typeof(string)) return s;
        if (k.IsDefined(typeof(TrebuchetRecordAttribute), false) && SingleField(k) is { } sf)
            return Ctor(k)!.Invoke(new[] { KeyFromString(s, sf.Prop.PropertyType) });
        if (k == typeof(DateTimeOffset)) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
        if (k == typeof(Guid)) return Guid.Parse(s);
        return Convert.ChangeType(s, k, CultureInfo.InvariantCulture);
    }

    /// <summary>What reading a record or variant needs to know about its type, worked out once.</summary>
    internal sealed class Shape
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Shape> Cache = new();
        public static Shape Of(Type t) => Cache.GetOrAdd(t, static type => new Shape(type));

        public readonly Type Type;
        public readonly ConstructorInfo Ctor;
        public readonly ParameterInfo[] Params;
        private readonly byte[][] _exact;
        private readonly Dictionary<string, int> _byNormalised = new();
        private readonly object?[] _absent;
        private readonly bool[] _mayBeAbsent;

        private Shape(Type t)
        {
            Type = t;
            Ctor = TrebuchetJsonConverter.Ctor(t) ?? throw new JsonException($"{t.Name} has no public constructor");
            Params = Ctor.GetParameters();
            _exact = Params.Select(p => System.Text.Encoding.UTF8.GetBytes(p.Name!)).ToArray();
            _absent = new object?[Params.Length];
            _mayBeAbsent = new bool[Params.Length];
            for (var i = 0; i < Params.Length; i++)
            {
                _byNormalised[TrebuchetJson.Normalise(Params[i].Name!)] = i;
                var pt = Params[i].ParameterType;
                if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Option<>))
                {
                    _absent[i] = pt.GetField("None")!.GetValue(null);
                    _mayBeAbsent[i] = true;
                }
            }
        }

        /// <summary>The parameter a property names: exact bytes first (no allocation), then leniently, so full_name finds fullName.</summary>
        public int IndexOf(ref Utf8JsonReader reader)
        {
            for (var i = 0; i < _exact.Length; i++)
                if (reader.ValueTextEquals(_exact[i])) return i;
            return _byNormalised.TryGetValue(TrebuchetJson.Normalise(reader.GetString()!), out var index) ? index : -1;
        }

        public object Instance()
        {
            if (Params.Length > 0) throw new JsonException($"{Type.Name} needs its fields");
            return Type.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? Ctor.Invoke(Array.Empty<object>());
        }

        public object Build(object?[] args, bool[] seen)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (seen[i]) continue;
                if (!_mayBeAbsent[i]) throw new JsonException($"missing field '{Params[i].Name}' for {Type.Name}");
                args[i] = _absent[i];
            }
            return Ctor.Invoke(args);
        }
    }

    /// <summary>
    /// Reads a record or variant straight from the tokens: each property goes to the constructor
    /// parameter it names, unknown properties are skipped, absent Options are None. No document
    /// is built, so a large array of records costs its records and nothing else.
    /// </summary>
    internal static object ReadObject(ref Utf8JsonReader reader, Type t, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"expected an object for {t.Name}");
        var shape = Shape.Of(t);
        var args = new object?[shape.Params.Length];
        var seen = new bool[shape.Params.Length];
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var i = shape.IndexOf(ref reader);
            reader.Read();
            if (i < 0 || seen[i]) { reader.Skip(); continue; }
            args[i] = JsonSerializer.Deserialize(ref reader, shape.Params[i].ParameterType, options);
            seen[i] = true;
        }
        return shape.Params.Length == 0 ? shape.Instance() : shape.Build(args, seen);
    }

    /// <summary>Reads an array's elements one at a time; null is an empty array.</summary>
    internal static List<T> ReadArray<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var items = new List<T>();
        if (reader.TokenType == JsonTokenType.Null) return items;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an array");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            items.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
        return items;
    }

    /// <summary>A record with several fields: read by lenient field name with Option defaults, written with the declared names.</summary>
    private sealed class RecordConverter<T> : JsonConverter<T>
    {
        private readonly ParameterInfo[] _params = Ctor(typeof(T))!.GetParameters();

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return (T)ReadObject(ref reader, typeof(T), options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var p in _params)
            {
                var prop = typeof(T).GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!;
                writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name);
                JsonSerializer.Serialize(writer, prop.GetValue(value), prop.PropertyType, options);
            }
            writer.WriteEndObject();
        }
    }

    private sealed class SingleFieldConverter<T> : JsonConverter<T>
    {
        private readonly (ParameterInfo Param, PropertyInfo Prop) _field = SingleField(typeof(T))!.Value;
        private readonly ConstructorInfo _ctor = Ctor(typeof(T))!;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            object? inner;
            if (reader.TokenType == JsonTokenType.StartObject) return (T)ReadObject(ref reader, typeof(T), options);
            inner = JsonSerializer.Deserialize(ref reader, _field.Prop.PropertyType, options);
            return (T)_ctor.Invoke(new[] { inner });
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var inner = _field.Prop.GetValue(value);
            if (writer.CurrentDepth > 0)
            {
                JsonSerializer.Serialize(writer, inner, _field.Prop.PropertyType, options);
                return;
            }
            writer.WriteStartObject();
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(_field.Prop.Name) ?? _field.Prop.Name);
            JsonSerializer.Serialize(writer, inner, _field.Prop.PropertyType, options);
            writer.WriteEndObject();
        }
    }

    private sealed class UnionConverter<T> : JsonConverter<T>
    {
        private static readonly Dictionary<string, Type> Variants = FindVariants();

        private static Dictionary<string, Type> FindVariants()
        {
            var t = typeof(T);
            var union = t.IsDefined(typeof(TrebuchetUnionAttribute), false) ? t : t.BaseType!;
            var open = union.IsGenericType ? union.GetGenericTypeDefinition() : union;
            var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in union.Assembly.GetTypes())
            {
                if (!candidate.IsDefined(typeof(TrebuchetVariantAttribute), false)) continue;
                var b = candidate.BaseType;
                if (b is null) continue;
                var bOpen = b.IsGenericType ? b.GetGenericTypeDefinition() : b;
                if (bOpen != open) continue;
                var closed = candidate.IsGenericTypeDefinition && union.IsGenericType ? candidate.MakeGenericType(union.GetGenericArguments()) : candidate;
                result[candidate.Name.Split('`')[0]] = closed;
            }
            return result;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString()!;
                if (!Variants.TryGetValue(name, out var nullary)) throw new JsonException($"unknown variant '{name}' of {typeof(T).Name}");
                return (T)Shape.Of(nullary).Instance();
            }
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"expected a variant name or an object for {typeof(T).Name}");
            // the tag may come after the fields: look ahead on a copy of the reader, then read for real
            Type variant = typeof(T);
            var tag = TagOf(reader);
            if (tag is not null)
            {
                if (!Variants.TryGetValue(tag, out variant!)) throw new JsonException($"unknown variant '{tag}' of {typeof(T).Name}");
            }
            else if (variant.IsAbstract) throw new JsonException($"{typeof(T).Name} needs a 'type' field");
            return (T)ReadObject(ref reader, variant, options);
        }

        /// <summary>The object's "type" property, found on a copy of the reader so the original stays at the object's start.</summary>
        private static string? TagOf(Utf8JsonReader scan)
        {
            var depth = scan.CurrentDepth;
            while (scan.Read())
            {
                if (scan.TokenType == JsonTokenType.EndObject && scan.CurrentDepth == depth) return null;
                if (scan.TokenType != JsonTokenType.PropertyName || scan.CurrentDepth != depth + 1) continue;
                var isTag = scan.ValueTextEquals("type");
                scan.Read();
                if (isTag) return scan.TokenType == JsonTokenType.String ? scan.GetString() : null;
                scan.Skip();
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var variant = value!.GetType();
            var name = variant.Name.Split('`')[0];
            var ps = Ctor(variant)?.GetParameters() ?? Array.Empty<ParameterInfo>();
            if (ps.Length == 0) { writer.WriteStringValue(name); return; }
            writer.WriteStartObject();
            writer.WriteString("type", name);
            foreach (var p in ps)
            {
                var prop = variant.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!;
                writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name);
                JsonSerializer.Serialize(writer, prop.GetValue(value), prop.PropertyType, options);
            }
            writer.WriteEndObject();
        }
    }

    private sealed class OptionConverter<T> : JsonConverter<Option<T>>
    {
        // without this the serializer short-circuits a JSON null to a C# null and never asks us
        public override bool HandleNull => true;

        public override Option<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? Option<T>.None : Option<T>.Some(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, Option<T> value, JsonSerializerOptions options)
        {
            if (value is Some<T> s) JsonSerializer.Serialize(writer, s.value, options);
            else writer.WriteNullValue();
        }
    }

    private sealed class VectorConverter<T> : JsonConverter<Vector<T>>
    {
        public override Vector<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Vector<T>.From(ReadArray<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, Vector<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var x in value) JsonSerializer.Serialize(writer, x, options);
            writer.WriteEndArray();
        }
    }

    private sealed class SetConverter<T> : JsonConverter<Set<T>> where T : notnull
    {
        public override Set<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Set<T>.From(ReadArray<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, Set<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var x in value) JsonSerializer.Serialize(writer, x, options);
            writer.WriteEndArray();
        }
    }

    private sealed class MapConverter<K, V> : JsonConverter<Map<K, V>> where K : notnull
    {
        private static readonly bool ScalarKey = IsScalarKey(typeof(K));

        public override Map<K, V> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var m = Map<K, V>.Empty;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var key = (K)KeyFromString(reader.GetString()!, typeof(K));
                    reader.Read();
                    m = m.Set(key, JsonSerializer.Deserialize<V>(ref reader, options)!);
                }
                return m;
            }
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an object or an array of key and value pairs for a Map");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("expected a {key, value} pair");
                K? key = default; V? value = default; var hasKey = false; var hasValue = false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isKey = reader.ValueTextEquals("key"); var isValue = reader.ValueTextEquals("value");
                    reader.Read();
                    if (isKey) { key = JsonSerializer.Deserialize<K>(ref reader, options); hasKey = true; }
                    else if (isValue) { value = JsonSerializer.Deserialize<V>(ref reader, options); hasValue = true; }
                    else reader.Skip();
                }
                if (!hasKey || !hasValue) throw new JsonException("a Map pair needs key and value");
                m = m.Set(key!, value!);
            }
            return m;
        }

        public override void Write(Utf8JsonWriter writer, Map<K, V> value, JsonSerializerOptions options)
        {
            if (ScalarKey)
            {
                writer.WriteStartObject();
                foreach (var e in value)
                {
                    writer.WritePropertyName(KeyToString(e.Key));
                    JsonSerializer.Serialize(writer, e.Value, options);
                }
                writer.WriteEndObject();
                return;
            }
            writer.WriteStartArray();
            foreach (var e in value)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("key");
                JsonSerializer.Serialize(writer, e.Key, options);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, e.Value, options);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }
}

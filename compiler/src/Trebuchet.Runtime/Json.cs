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

    public static JsonSerializerOptions Options => Configure(new JsonSerializerOptions());
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
        return t.IsDefined(typeof(TrebuchetRecordAttribute), false) && SingleField(t) is not null;
    }

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options)
    {
        Type converter;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Option<>)) converter = typeof(OptionConverter<>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Map<,>)) converter = typeof(MapConverter<,>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Set<>)) converter = typeof(SetConverter<>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Vector<>)) converter = typeof(VectorConverter<>).MakeGenericType(t.GetGenericArguments());
        else if (t.IsDefined(typeof(TrebuchetUnionAttribute), false) || t.IsDefined(typeof(TrebuchetVariantAttribute), false)) converter = typeof(UnionConverter<>).MakeGenericType(t);
        else converter = typeof(SingleFieldConverter<>).MakeGenericType(t);
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

    /// <summary>Builds a record or variant from a JSON object by matching constructor parameters to properties by name.</summary>
    internal static object Construct(Type t, JsonElement obj, JsonSerializerOptions options)
    {
        var ctor = Ctor(t) ?? throw new JsonException($"{t.Name} has no public constructor");
        var ps = ctor.GetParameters();
        if (ps.Length == 0)
        {
            var instance = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            return instance?.GetValue(null) ?? ctor.Invoke(Array.Empty<object>());
        }
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var found = false;
            foreach (var prop in obj.EnumerateObject())
            {
                if (!string.Equals(prop.Name, ps[i].Name, StringComparison.OrdinalIgnoreCase)) continue;
                args[i] = prop.Value.Deserialize(ps[i].ParameterType, options);
                found = true;
                break;
            }
            if (!found)
            {
                var pt = ps[i].ParameterType;
                if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Option<>)) args[i] = pt.GetField("None")!.GetValue(null);
                else throw new JsonException($"missing field '{ps[i].Name}' for {t.Name}");
            }
        }
        return ctor.Invoke(args);
    }

    private sealed class SingleFieldConverter<T> : JsonConverter<T>
    {
        private readonly (ParameterInfo Param, PropertyInfo Prop) _field = SingleField(typeof(T))!.Value;
        private readonly ConstructorInfo _ctor = Ctor(typeof(T))!;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            object? inner;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var obj = JsonElement.ParseValue(ref reader);
                return (T)Construct(typeof(T), obj, options);
            }
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
                return (T)Construct(nullary, default, options);
            }
            var obj = JsonElement.ParseValue(ref reader);
            Type variant = typeof(T);
            if (obj.TryGetProperty("type", out var tag) && tag.ValueKind == JsonValueKind.String)
            {
                if (!Variants.TryGetValue(tag.GetString()!, out variant!)) throw new JsonException($"unknown variant '{tag.GetString()}' of {typeof(T).Name}");
            }
            else if (variant.IsAbstract) throw new JsonException($"{typeof(T).Name} needs a 'type' field");
            return (T)Construct(variant, obj, options);
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
            Vector<T>.From(JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? new List<T>());

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
            Set<T>.From(JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? new List<T>());

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
            var el = JsonElement.ParseValue(ref reader);
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in el.EnumerateObject())
                    m = m.Set((K)KeyFromString(p.Name, typeof(K)), p.Value.Deserialize<V>(options)!);
                return m;
            }
            foreach (var pair in el.EnumerateArray())
                m = m.Set(pair.GetProperty("key").Deserialize<K>(options)!, pair.GetProperty("value").Deserialize<V>(options)!);
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

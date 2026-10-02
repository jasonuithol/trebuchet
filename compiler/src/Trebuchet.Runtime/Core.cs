using System.Globalization;

namespace Trebuchet.Runtime;

/// <summary>The type with one value. Used instead of void so every function returns something.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
    public override string ToString() => "unit";
}

/// <summary>Unrecoverable failure. Caught only at supervisor points such as a request boundary.</summary>
public sealed class TrebPanic : Exception
{
    public object? Payload { get; }
    public TrebPanic(string message, object? payload = null) : base(message) => Payload = payload;
}

public sealed record ArgumentError(string message);
/// <summary>The built-in shape over a type for ordering. Generated instances implement it; primitives have the instances below.</summary>
public interface Ord<T> { long compare(T a, T b); }
public sealed class Ord_Int : Ord<long> { public static readonly Ord_Int Instance = new(); public long compare(long a, long b) => a.CompareTo(b); }
public sealed class Ord_Float : Ord<double> { public static readonly Ord_Float Instance = new(); public long compare(double a, double b) => a.CompareTo(b); }
public sealed class Ord_String : Ord<string> { public static readonly Ord_String Instance = new(); public long compare(string a, string b) => string.CompareOrdinal(a, b); }
public sealed class Ord_Bool : Ord<bool> { public static readonly Ord_Bool Instance = new(); public long compare(bool a, bool b) => a.CompareTo(b); }
public sealed class Ord_Instant : Ord<DateTimeOffset> { public static readonly Ord_Instant Instance = new(); public long compare(DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b); }

/// <summary>The error a <c>supervise</c> expression yields for a caught panic.</summary>
public sealed record Panic(string message);

// ---------------------------------------------------------------- Option

public abstract record Option<T>
{
    public static Option<T> Some(T value) => new Some<T>(value);
    public static readonly Option<T> None = new None<T>();
    public bool IsSome => this is Some<T>;
    public static implicit operator Option<T>(NoneValue _) => None;
    public static implicit operator Option<T>(SomeValue<T> s) => new Some<T>(s.value);
}

public sealed record Some<T>(T value) : Option<T>
{
    public override string ToString() => $"Some({value})";
}

public sealed record None<T> : Option<T>
{
    public override string ToString() => "None";
}

/// <summary>Untyped None, convertible to any Option. Lets generated code write <c>None</c> without a type argument.</summary>
public readonly record struct NoneValue;

public readonly record struct SomeValue<T>(T value);

// ---------------------------------------------------------------- Result

public abstract record Result<T, E>
{
    public static Result<T, E> Ok(T value) => new Ok<T, E>(value);
    public static Result<T, E> Error(E error) => new Error<T, E>(error);
    public bool IsOk => this is Ok<T, E>;
    public static implicit operator Result<T, E>(OkValue<T> ok) => new Ok<T, E>(ok.value);
    public static implicit operator Result<T, E>(ErrorValue<E> err) => new Error<T, E>(err.error);
}

public sealed record Ok<T, E>(T value) : Result<T, E>
{
    public override string ToString() => $"Ok({value})";
}

public sealed record Error<T, E>(E error) : Result<T, E>
{
    public override string ToString() => $"Error({error})";
}

/// <summary>An Ok whose error type is not yet known; converts to any Result with that payload type.</summary>
public readonly record struct OkValue<T>(T value);

/// <summary>An Error whose payload type is not yet known; converts to any Result with that error type.</summary>
public readonly record struct ErrorValue<E>(E error);

// ---------------------------------------------------------------- Cell

/// <summary>The one mutable primitive. Reference identity; reads are Nondet, writes are Write.</summary>
/// <summary>
/// The one mutable primitive. Every operation is atomic: a .NET host runs handlers on parallel
/// threads, so an update must be a single read-modify-write, and the function it applies runs
/// under the lock exactly once. Uncontended, the lock costs a few nanoseconds.
/// </summary>
public sealed class Cell<T>
{
    private readonly object _gate = new();
    private T _value;
    public Cell(T initial) => _value = initial;
    public T Get() { lock (_gate) return _value; }
    public Unit Set(T value) { lock (_gate) _value = value; return Unit.Value; }
    public Unit Update(Func<T, T> f) { lock (_gate) _value = f(_value); return Unit.Value; }
    public T GetAndUpdate(Func<T, T> f) { lock (_gate) { var old = _value; _value = f(old); return old; } }
    public override string ToString() => $"Cell({Get()})";
}

// ---------------------------------------------------------------- builtin namespaces

public static class Cell
{
    public static Cell<T> create<T>(T initial) => new(initial);
}

public static class Instant
{
    public static DateTimeOffset parse(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : throw new TrebPanic($"Instant.parse: cannot parse \"{s}\"");
    public static DateTimeOffset now() => DateTimeOffset.UtcNow;
    public static DateTimeOffset plusSeconds(DateTimeOffset t, long seconds) => t.AddSeconds(seconds);
    public static long secondsBetween(DateTimeOffset from, DateTimeOffset to) => (long)(to - from).TotalSeconds;
}

public static class sys
{
    public static DateTimeOffset clock() => DateTimeOffset.UtcNow;
}

public static class env
{
    public static string get(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new TrebPanic($"env.get: {name} is not set");
}

public static class json
{
    public static string encode<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, TrebuchetJson.Options);
    public static T decode<T>(string text)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(text, TrebuchetJson.Options) ?? throw new TrebPanic("json.decode: the document is null"); }
        catch (System.Text.Json.JsonException ex) { throw new TrebPanic("json.decode: " + ex.Message); }
    }

    /// <summary>
    /// The elements of a top-level JSON array, one at a time: each is decoded when the sequence is
    /// pulled and can be dropped before the next, so a consumer that condenses as it goes never
    /// holds the whole array. A malformed document panics at the element that is wrong.
    /// </summary>
    public static Seq<T> decodeSeq<T>(string text) => new(() => Elements<T>(text));

    /// <summary>
    /// The same, over a document that arrives in pieces: text is taken a chunk at a time, only as
    /// far as the next element needs, so a file far larger than memory can be decoded and neither
    /// the document nor the array is ever whole in memory. A chunk may end anywhere, mid-token included.
    /// </summary>
    public static Seq<T> decodeChunks<T>(Seq<string> chunks) => new(() => ChunkedElements<T>(chunks));

    private static IEnumerable<T> ChunkedElements<T>(Seq<string> chunks)
    {
        using var source = chunks.GetEnumerator();
        var encoder = System.Text.Encoding.UTF8.GetEncoder(); // stateful: a surrogate pair may straddle two chunks
        var buffer = new byte[1 << 16];
        int start = 0, end = 0;
        var state = new System.Text.Json.JsonReaderState();
        var started = false;
        var final = false;
        while (true)
        {
            var step = NextChunked<T>(buffer, ref start, end, final, ref state, ref started, out var item);
            if (step == Step.Item) { yield return item; continue; }
            if (step == Step.End) yield break;
            if (final) throw new TrebPanic("json.decodeChunks: the document ends in the middle of a value");
            string? chunk = null;
            try { if (source.MoveNext()) chunk = source.Current; }
            catch (TrebPanic) { throw; }
            catch (Exception ex) { throw new TrebPanic("json.decodeChunks: " + ex.Message); }
            if (chunk is null) { final = true; continue; }
            // make room: slide what is unread to the front, grow if the next chunk still does not fit
            var need = encoder.GetByteCount(chunk.AsSpan(), flush: false);
            if (start > 0) { Buffer.BlockCopy(buffer, start, buffer, 0, end - start); end -= start; start = 0; }
            if (end + need > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, end + need));
            end += encoder.GetBytes(chunk.AsSpan(), buffer.AsSpan(end), flush: false);
        }
    }

    private enum Step { Item, End, NeedMore }

    private static Step NextChunked<T>(byte[] buffer, ref int start, int end, bool final, ref System.Text.Json.JsonReaderState state, ref bool started, out T item)
    {
        item = default!;
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
            if (!started)
            {
                if (!reader.Read()) return Step.NeedMore;
                if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray) throw new TrebPanic("json.decodeChunks: the document is not an array");
                started = true;
                start += (int)reader.BytesConsumed;
                state = reader.CurrentState;
                reader = new System.Text.Json.Utf8JsonReader(buffer.AsSpan(start, end - start), final, state);
            }
            if (!reader.Read()) return Step.NeedMore;
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndArray) return Step.End;
            // is the whole element here? look ahead on a copy before committing to read it
            var probe = reader;
            if (!probe.TrySkip()) return Step.NeedMore;
            item = System.Text.Json.JsonSerializer.Deserialize<T>(ref reader, TrebuchetJson.Options) ?? throw new TrebPanic("json.decodeChunks: an element is null");
            start += (int)reader.BytesConsumed;
            state = reader.CurrentState;
            return Step.Item;
        }
        catch (System.Text.Json.JsonException ex) { throw new TrebPanic("json.decodeChunks: " + ex.Message); }
    }

    private static IEnumerable<T> Elements<T>(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var state = new System.Text.Json.JsonReaderState();
        var offset = 0;
        var started = false;
        while (NextElement<T>(bytes, ref offset, ref state, ref started, out var item)) yield return item;
    }

    private static bool NextElement<T>(byte[] bytes, ref int offset, ref System.Text.Json.JsonReaderState state, ref bool started, out T item)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(bytes.AsSpan(offset), isFinalBlock: true, state);
            if (!started)
            {
                if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartArray) throw new TrebPanic("json.decodeSeq: the document is not an array");
                started = true;
            }
            if (!reader.Read()) throw new TrebPanic("json.decodeSeq: the array is not closed");
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndArray) { item = default!; return false; }
            item = System.Text.Json.JsonSerializer.Deserialize<T>(ref reader, TrebuchetJson.Options) ?? throw new TrebPanic("json.decodeSeq: an element is null");
            offset += (int)reader.BytesConsumed;
            state = reader.CurrentState;
            return true;
        }
        catch (System.Text.Json.JsonException ex) { throw new TrebPanic("json.decodeSeq: " + ex.Message); }
    }
}

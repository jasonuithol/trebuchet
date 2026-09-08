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
public sealed class Cell<T>
{
    private T _value;
    public Cell(T initial) => _value = initial;
    public T Get() => _value;
    public Unit Set(T value) { _value = value; return Unit.Value; }
    public Unit Update(Func<T, T> f) { _value = f(_value); return Unit.Value; }
    public T GetAndUpdate(Func<T, T> f) { var old = _value; _value = f(old); return old; }
    public override string ToString() => $"Cell({_value})";
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
    public static string encode<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);
    public static T decode<T>(string text) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(text) ?? throw new TrebPanic("json.decode: null");
}

namespace Trebuchet.Runtime;

/// <summary>
/// The standard library as seen by generated C#. Names match the Trebuchet builtins one
/// for one, so the emitter maps a call to a call. Generated files import this with
/// <c>using static Trebuchet.Runtime.Prelude;</c>.
/// </summary>
public static class Prelude
{
    public static readonly Unit unit = Unit.Value;
    public static readonly NoneValue None = default;

    public static SomeValue<T> Some<T>(T value) => new(value);
    public static OkValue<T> ok<T>(T value) => new(value);
    public static OkValue<Unit> ok() => new(Unit.Value);
    public static ErrorValue<E> error<E>(E e) => new(e);
    public static OkValue<T> Ok<T>(T value) => new(value);
    public static ErrorValue<E> Error<E>(E e) => new(e);

    public static T fail<T>(object payload) =>
        throw new TrebPanic(payload is ArgumentError a ? a.message : payload?.ToString() ?? "panic", payload);

    public static Unit print<T>(T value) { Console.WriteLine(value); return Unit.Value; }
    public static string toString<T>(T value) => value?.ToString() ?? "";

    // ---- Result and Option
    public static Result<T, F> mapError<T, E, F>(Result<T, E> r, Func<E, F> f) =>
        r is Error<T, E> e ? new Error<T, F>(f(e.error)) : new Ok<T, F>(((Ok<T, E>)r).value);
    public static bool isOk<T, E>(Result<T, E> r) => r is Ok<T, E>;
    public static bool isError<T, E>(Result<T, E> r) => r is Error<T, E>;
    public static V getOr<K, V>(Map<K, V> m, K key, V fallback) where K : notnull => m.GetOr(key, fallback);
    public static T getOr<T>(Option<T> o, T fallback) => o is Some<T> s ? s.value : fallback;
    public static T getOr<T, E>(Result<T, E> r, T fallback) => r is Ok<T, E> o ? o.value : fallback;

    // ---- collections
    public static Vector<T> vector<T>(params T[] items) => Vector<T>.Of(items);
    public static Vector<T> append<T>(Vector<T> v, T item) => v.Append(item);
    public static Vector<T> concat<T>(Vector<T> a, Vector<T> b) => a.Concat(b);
    public static long length<T>(Vector<T> v) => v.Count;
    public static long length(string s) => s.Length;
    public static long length<K, V>(Map<K, V> m) where K : notnull => m.Count;
    public static bool isEmpty<T>(Vector<T> v) => v.IsEmpty;
    public static bool isEmpty(string s) => s.Length == 0;
    public static bool isEmpty<K, V>(Map<K, V> m) where K : notnull => m.IsEmpty;
    public static Vector<U> map<T, U>(Vector<T> v, Func<T, U> f) => Vector<U>.From(v.Select(f));
    public static Result<U, E> map<T, E, U>(Result<T, E> r, Func<T, U> f) =>
        r is Ok<T, E> o ? new Ok<U, E>(f(o.value)) : new Error<U, E>(((Error<T, E>)r).error);
    public static Option<U> map<T, U>(Option<T> o, Func<T, U> f) => o is Some<T> s ? new Some<U>(f(s.value)) : new None<U>();
    public static Vector<T> filter<T>(Vector<T> v, Func<T, bool> f) => Vector<T>.From(v.Where(f));
    public static A fold<T, A>(Vector<T> v, A init, Func<A, T, A> f)
    {
        var acc = init;
        foreach (var x in v) acc = f(acc, x);
        return acc;
    }
    public static bool any<T>(Vector<T> v, Func<T, bool> f) => v.Any(f);
    public static bool all<T>(Vector<T> v, Func<T, bool> f) => v.All(f);
    public static Option<T> find<T>(Vector<T> v, Func<T, bool> f)
    {
        foreach (var x in v) if (f(x)) return new Some<T>(x);
        return new None<T>();
    }
    public static Unit forEach<T, U>(Vector<T> v, Func<T, U> f) { foreach (var x in v) f(x); return Unit.Value; }
    public static Option<T> first<T>(Vector<T> v) => v.First;
    public static Option<T> last<T>(Vector<T> v) => v.Last;
    public static Vector<T> reverse<T>(Vector<T> v) => Vector<T>.From(v.AsEnumerable().Reverse());
    public static bool contains<T>(Vector<T> v, T item) => v.Contains(item);
    public static bool contains<K, V>(Map<K, V> m, K key) where K : notnull => m.ContainsKey(key);
    public static bool contains(string s, string sub) => s.Contains(sub, StringComparison.Ordinal);
    public static long sum(Vector<long> v) => v.Sum();
    public static Vector<T> take<T>(Vector<T> v, long n) => Vector<T>.From(v.Take((int)Math.Max(0, Math.Min(n, v.Count))));
    public static Vector<T> drop<T>(Vector<T> v, long n) => Vector<T>.From(v.Skip((int)Math.Max(0, Math.Min(n, v.Count))));
    public static Option<T> at<T>(Vector<T> v, long i) => i >= 0 && i < v.Count ? Option<T>.Some(v.Get((int)i)) : Option<T>.None;
    public static Vector<T> sortBy<T, K>(Vector<T> v, Func<T, K> key) => Vector<T>.From(v.OrderBy(key));
    public static Vector<T> sort<T>(Vector<T> v, Ord<T> ord) => Vector<T>.From(v.OrderBy(x => x, Comparer<T>.Create((a, b) => Math.Sign(ord.compare(a, b)))));
    public static Option<T> maximum<T>(Vector<T> v, Ord<T> ord)
    {
        if (v.IsEmpty) return Option<T>.None;
        var best = v.Get(0);
        foreach (var x in v) if (ord.compare(x, best) > 0) best = x;
        return Option<T>.Some(best);
    }
    public static Option<T> minimum<T>(Vector<T> v, Ord<T> ord)
    {
        if (v.IsEmpty) return Option<T>.None;
        var best = v.Get(0);
        foreach (var x in v) if (ord.compare(x, best) < 0) best = x;
        return Option<T>.Some(best);
    }
    public static async ValueTask<Vector<T>> sortByAsync<T, K>(Vector<T> v, Func<T, ValueTask<K>> key)
    {
        var keyed = new List<(K Key, T Item)>();
        foreach (var x in v) keyed.Add((await key(x), x));
        return Vector<T>.From(keyed.OrderBy(p => p.Key).Select(p => p.Item));
    }
    public static Result<Vector<U>, E> traverse<T, U, E>(Vector<T> v, Func<T, Result<U, E>> f)
    {
        var acc = Vector<U>.Empty;
        foreach (var x in v)
        {
            var r = f(x);
            if (r is Error<U, E> e) return new Error<Vector<U>, E>(e.error);
            acc = acc.Append(((Ok<U, E>)r).value);
        }
        return new Ok<Vector<U>, E>(acc);
    }
    public static async ValueTask<Result<Vector<U>, E>> traverseAsync<T, U, E>(Vector<T> v, Func<T, ValueTask<Result<U, E>>> f)
    {
        var acc = Vector<U>.Empty;
        foreach (var x in v)
        {
            var r = await f(x);
            if (r is Error<U, E> e) return new Error<Vector<U>, E>(e.error);
            acc = acc.Append(((Ok<U, E>)r).value);
        }
        return new Ok<Vector<U>, E>(acc);
    }

    // ---- higher-order functions over suspending lambdas (generated code appends Async when a lambda suspends)
    public static async ValueTask<Vector<U>> mapAsync<T, U>(Vector<T> v, Func<T, ValueTask<U>> f)
    {
        var r = Vector<U>.Empty;
        foreach (var x in v) r = r.Append(await f(x));
        return r;
    }
    public static async ValueTask<Vector<T>> filterAsync<T>(Vector<T> v, Func<T, ValueTask<bool>> f)
    {
        var r = Vector<T>.Empty;
        foreach (var x in v) if (await f(x)) r = r.Append(x);
        return r;
    }
    public static async ValueTask<A> foldAsync<T, A>(Vector<T> v, A init, Func<A, T, ValueTask<A>> f)
    {
        var acc = init;
        foreach (var x in v) acc = await f(acc, x);
        return acc;
    }
    public static async ValueTask<bool> anyAsync<T>(Vector<T> v, Func<T, ValueTask<bool>> f)
    {
        foreach (var x in v) if (await f(x)) return true;
        return false;
    }
    public static async ValueTask<bool> allAsync<T>(Vector<T> v, Func<T, ValueTask<bool>> f)
    {
        foreach (var x in v) if (!await f(x)) return false;
        return true;
    }
    public static async ValueTask<Option<T>> findAsync<T>(Vector<T> v, Func<T, ValueTask<bool>> f)
    {
        foreach (var x in v) if (await f(x)) return new Some<T>(x);
        return new None<T>();
    }
    public static async ValueTask<Unit> forEachAsync<T, U>(Vector<T> v, Func<T, ValueTask<U>> f)
    {
        foreach (var x in v) await f(x);
        return Unit.Value;
    }
    public static async ValueTask<Result<T, F>> mapErrorAsync<T, E, F>(Result<T, E> r, Func<E, ValueTask<F>> f) =>
        r is Error<T, E> e ? new Error<T, F>(await f(e.error)) : new Ok<T, F>(((Ok<T, E>)r).value);
    public static async ValueTask<Unit> updateAsync<T>(Cell<T> c, Func<T, ValueTask<T>> f) { c.Set(await f(c.Get())); return Unit.Value; }

    // ---- maps and cells
    public static T get<T>(Cell<T> c) => c.Get();
    public static Option<V> get<K, V>(Map<K, V> m, K key) where K : notnull => m.Get(key);
    public static Unit set<T>(Cell<T> c, T value) => c.Set(value);
    public static Map<K, V> set<K, V>(Map<K, V> m, K key, V value) where K : notnull => m.Set(key, value);
    public static Map<K, V> remove<K, V>(Map<K, V> m, K key) where K : notnull => m.Remove(key);
    public static Vector<K> keys<K, V>(Map<K, V> m) where K : notnull => Vector<K>.From(m.Keys);
    public static Set<T> remove<T>(Set<T> s, T item) where T : notnull => s.Remove(item);
    public static long length<T>(Set<T> s) where T : notnull => s.Count;
    public static bool isEmpty<T>(Set<T> s) where T : notnull => s.IsEmpty;
    public static bool contains<T>(Set<T> s, T item) where T : notnull => s.Contains(item);
    public static Set<T> toSet<T>(Vector<T> v) where T : notnull => Set<T>.From(v);
    public static Set<T> add<T>(Set<T> s, T item) where T : notnull => s.Add(item);
    public static Vector<T> items<T>(Set<T> s) where T : notnull => Vector<T>.From(s);
    public static Set<T> merge<T>(Set<T> a, Set<T> b) where T : notnull => a.Union(b);
    public static Set<T> intersect<T>(Set<T> a, Set<T> b) where T : notnull => a.Intersect(b);
    public static Set<T> difference<T>(Set<T> a, Set<T> b) where T : notnull => a.Difference(b);
    public static Vector<V> values<K, V>(Map<K, V> m) where K : notnull => Vector<V>.From(m.Values);
    public static Unit update<T>(Cell<T> c, Func<T, T> f) => c.Update(f);
    public static T getAndUpdate<T>(Cell<T> c, Func<T, T> f) => c.GetAndUpdate(f);

    // ---- strings
    public static string trim(string s) => s.Trim();
    public static string toUpper(string s) => s.ToUpperInvariant();
    public static string toLower(string s) => s.ToLowerInvariant();
    public static bool startsWith(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);
    public static string uuid() => Guid.NewGuid().ToString();
    public static async ValueTask<Unit> sleep(long ms) { await System.Threading.Tasks.Task.Delay((int)Math.Max(0, ms)); return Unit.Value; }
}

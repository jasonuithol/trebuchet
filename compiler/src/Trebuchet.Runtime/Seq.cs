using System.Collections;

namespace Trebuchet.Runtime;

/// <summary>
/// A lazy, re-iterable, possibly infinite sequence. Every combinator returns a new Seq that
/// pulls from this one; toVector, first, and fold force it. A Seq has reference identity:
/// it is a computation, not a value, so it is never compared structurally.
/// </summary>
public sealed class Seq<T> : IEnumerable<T>
{
    private readonly Func<IEnumerable<T>> _source;
    public Seq(Func<IEnumerable<T>> source) => _source = source;
    public static Seq<T> From(IEnumerable<T> items) => new(() => items);
    public IEnumerator<T> GetEnumerator() => _source().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => "<seq>";
}

/// <summary>The Seq namespace of the language: constructors.</summary>
public static class Seq
{
    public static Seq<T> from<T>(Vector<T> v) => Seq<T>.From(v);
    public static Seq<T> iterate<T>(T seed, Func<T, T> f) => new(() => Iterate(seed, f));
    public static Seq<long> range(long from, long to) => new(() => Range(from, to));

    private static IEnumerable<T> Iterate<T>(T seed, Func<T, T> f)
    {
        var current = seed;
        while (true)
        {
            yield return current;
            current = f(current);
        }
    }

    private static IEnumerable<long> Range(long from, long to)
    {
        for (var i = from; i < to; i++) yield return i;
    }
}

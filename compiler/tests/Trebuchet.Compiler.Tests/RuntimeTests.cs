using Trebuchet.Runtime;
using Xunit;

namespace Trebuchet.Compiler.Tests;

public class RuntimeTests
{
    [Fact]
    public void CellUpdatesAreAtomicUnderParallelThreads()
    {
        var cell = new Cell<long>(0);
        Parallel.For(0, 64, _ => { for (var i = 0; i < 1000; i++) cell.Update(n => n + 1); });
        Assert.Equal(64_000, cell.Get());
        var log = new Cell<Vector<int>>(Vector<int>.Empty);
        Parallel.For(0, 500, i => log.Update(v => v.Append(i)));
        Assert.Equal(500, log.Get().Count);
    }

    [Fact]
    public void SetAlgebra()
    {
        var a = Set<string>.From(new[] { "red", "green", "blue" });
        var b = Set<string>.From(new[] { "green", "yellow" });
        Assert.Equal(Set<string>.From(new[] { "green" }), a.Intersect(b));
        Assert.Equal(4, a.Union(b).Count);
        Assert.Equal(Set<string>.From(new[] { "red", "blue" }), a.Difference(b));
        Assert.Equal(a, Set<string>.From(new[] { "blue", "red", "green" }));
    }

    [Fact]
    public void VectorAppendGetSetAcrossTrieBoundaries()
    {
        var v = Vector<int>.Empty;
        var expected = new List<int>();
        for (var i = 0; i < 5000; i++)
        {
            v = v.Append(i);
            expected.Add(i);
            Assert.Equal(expected.Count, v.Count);
        }
        for (var i = 0; i < expected.Count; i += 97) Assert.Equal(expected[i], v[i]);
        Assert.Equal(expected, v.ToList());

        var updated = v.Set(1234, -1).Set(0, -2).Set(4999, -3);
        Assert.Equal(-1, updated[1234]);
        Assert.Equal(-2, updated[0]);
        Assert.Equal(-3, updated[4999]);
        Assert.Equal(1234, v[1234]); // original untouched
        Assert.Equal(0, v[0]);
    }

    [Fact]
    public void VectorStructuralEqualityAndSharing()
    {
        var a = Vector<int>.Of(1, 2, 3);
        var b = Vector<int>.Empty.Append(1).Append(2).Append(3);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a.Append(4));
        Assert.Equal(Vector<int>.Of(1, 2, 3, 9, 8), a.Concat(Vector<int>.Of(9, 8)));
        Assert.Throws<TrebPanic>(() => a[3]);
    }

    [Fact]
    public void MapSetGetRemoveManyKeys()
    {
        var m = Map<string, int>.Empty;
        var reference = new Dictionary<string, int>();
        for (var i = 0; i < 3000; i++)
        {
            m = m.Set($"k{i}", i);
            reference[$"k{i}"] = i;
        }
        Assert.Equal(reference.Count, m.Count);
        foreach (var kv in reference) Assert.Equal(kv.Value, m.GetOr(kv.Key, -1));
        Assert.Equal(-1, m.GetOr("missing", -1));

        var overwritten = m.Set("k5", 500);
        Assert.Equal(500, overwritten.GetOr("k5", 0));
        Assert.Equal(5, m.GetOr("k5", 0));
        Assert.Equal(reference.Count, overwritten.Count);

        var removed = m;
        for (var i = 0; i < 3000; i += 2) removed = removed.Remove($"k{i}");
        Assert.Equal(1500, removed.Count);
        Assert.False(removed.ContainsKey("k0"));
        Assert.True(removed.ContainsKey("k1"));
        Assert.Equal(3000, m.Count);
    }

    private sealed record Colliding(int n)
    {
        public override int GetHashCode() => 42;
    }

    [Fact]
    public void MapHandlesHashCollisions()
    {
        var m = Map<Colliding, string>.Empty;
        for (var i = 0; i < 40; i++) m = m.Set(new Colliding(i), $"v{i}");
        Assert.Equal(40, m.Count);
        for (var i = 0; i < 40; i++) Assert.Equal($"v{i}", m.GetOr(new Colliding(i), "?"));
        var fewer = m.Remove(new Colliding(7));
        Assert.Equal(39, fewer.Count);
        Assert.False(fewer.ContainsKey(new Colliding(7)));
        Assert.True(fewer.ContainsKey(new Colliding(8)));
    }

    [Fact]
    public void MapEqualityIsOrderIndependent()
    {
        var a = Map<string, int>.Empty.Set("x", 1).Set("y", 2);
        var b = Map<string, int>.Empty.Set("y", 2).Set("x", 1);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a.Set("z", 3));
    }

    [Fact]
    public void ResultAndOptionConvertFromUntypedValues()
    {
        Result<int, string> r = Prelude.ok(1);
        Result<int, string> e = Prelude.error("bad");
        Option<int> n = Prelude.None;
        Option<int> s = Prelude.Some(2);
        Assert.True(r.IsOk);
        Assert.False(e.IsOk);
        Assert.False(n.IsSome);
        Assert.Equal(2, ((Some<int>)s).value);
        Assert.Equal(new Ok<int, string>(1), r);
        Assert.Equal("bad", Prelude.getOr(Prelude.mapError(e, x => x + "!"), 0) == 0 ? "bad" : "");
    }

    [Fact]
    public void RecordsContainingVectorsCompareStructurally()
    {
        var a = new Holder(Vector<int>.Of(1, 2));
        var b = new Holder(Vector<int>.Of(1, 2));
        Assert.Equal(a, b);
        Assert.Equal(a with { items = Vector<int>.Of(1, 2, 3) }, new Holder(Vector<int>.Of(1, 2, 3)));
    }

    private sealed record Holder(Vector<int> items);
}

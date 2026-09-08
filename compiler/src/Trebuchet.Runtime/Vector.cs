using System.Collections;

namespace Trebuchet.Runtime;

/// <summary>
/// Persistent vector: a 32-way bit-partitioned trie with a tail buffer, in the style of
/// Clojure's PersistentVector. get, set and append are O(log32 n); the tail makes append
/// amortised O(1). Structural equality. Concatenation and slicing are O(n) for now; an
/// RRB tree can replace this class later without changing its surface.
/// </summary>
public sealed class Vector<T> : IReadOnlyList<T>, IEquatable<Vector<T>>
{
    private const int Bits = 5;
    private const int Width = 1 << Bits;
    private const int Mask = Width - 1;

    private sealed class Node
    {
        public readonly object?[] Items;
        public Node(object?[] items) => Items = items;
        public Node() : this(new object?[Width]) { }
        public Node Clone() => new((object?[])Items.Clone());
    }

    private static readonly Node EmptyNode = new();
    public static readonly Vector<T> Empty = new(0, Bits, EmptyNode, Array.Empty<T>());

    private readonly int _count;
    private readonly int _shift;
    private readonly Node _root;
    private readonly T[] _tail;

    private Vector(int count, int shift, Node root, T[] tail)
    {
        _count = count;
        _shift = shift;
        _root = root;
        _tail = tail;
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public static Vector<T> Of(params T[] items) => From(items);

    public static Vector<T> From(IEnumerable<T> items)
    {
        var v = Empty;
        foreach (var x in items) v = v.Append(x);
        return v;
    }

    private int TailOffset => _count < Width ? 0 : ((_count - 1) >> Bits) << Bits;

    public T this[int index] => Get(index);

    public T Get(int index)
    {
        if (index < 0 || index >= _count) throw new TrebPanic($"index {index} out of range for a vector of {_count}");
        if (index >= TailOffset) return _tail[index & Mask];
        var node = _root;
        for (var level = _shift; level > 0; level -= Bits)
            node = (Node)node.Items[(index >> level) & Mask]!;
        return (T)node.Items[index & Mask]!;
    }

    public Vector<T> Append(T item)
    {
        if (_count - TailOffset < Width)
        {
            var newTail = new T[_tail.Length + 1];
            Array.Copy(_tail, newTail, _tail.Length);
            newTail[_tail.Length] = item;
            return new Vector<T>(_count + 1, _shift, _root, newTail);
        }
        // tail is full: push it into the trie
        var tailNode = new Node(_tail.Select(x => (object?)x).ToArray());
        Node newRoot;
        var newShift = _shift;
        if ((_count >> Bits) > (1 << _shift))
        {
            newRoot = new Node();
            newRoot.Items[0] = _root;
            newRoot.Items[1] = NewPath(_shift, tailNode);
            newShift += Bits;
        }
        else newRoot = PushTail(_shift, _root, tailNode);
        return new Vector<T>(_count + 1, newShift, newRoot, new[] { item });
    }

    private Node PushTail(int level, Node parent, Node tailNode)
    {
        var subIndex = ((_count - 1) >> level) & Mask;
        var result = parent.Clone();
        Node toInsert;
        if (level == Bits) toInsert = tailNode;
        else
        {
            var child = parent.Items[subIndex] as Node;
            toInsert = child is not null ? PushTail(level - Bits, child, tailNode) : NewPath(level - Bits, tailNode);
        }
        result.Items[subIndex] = toInsert;
        return result;
    }

    private static Node NewPath(int level, Node node)
    {
        if (level == 0) return node;
        var result = new Node();
        result.Items[0] = NewPath(level - Bits, node);
        return result;
    }

    public Vector<T> Set(int index, T item)
    {
        if (index < 0 || index >= _count) throw new TrebPanic($"index {index} out of range for a vector of {_count}");
        if (index >= TailOffset)
        {
            var newTail = (T[])_tail.Clone();
            newTail[index & Mask] = item;
            return new Vector<T>(_count, _shift, _root, newTail);
        }
        return new Vector<T>(_count, _shift, DoSet(_shift, _root, index, item), _tail);
    }

    private static Node DoSet(int level, Node node, int index, T item)
    {
        var result = node.Clone();
        if (level == 0) result.Items[index & Mask] = item;
        else
        {
            var sub = (index >> level) & Mask;
            result.Items[sub] = DoSet(level - Bits, (Node)node.Items[sub]!, index, item);
        }
        return result;
    }

    public Vector<T> Concat(Vector<T> other)
    {
        var v = this;
        foreach (var x in other) v = v.Append(x);
        return v;
    }

    public Vector<T> Concat(IEnumerable<T> other)
    {
        var v = this;
        foreach (var x in other) v = v.Append(x);
        return v;
    }

    public Option<T> First => _count == 0 ? Option<T>.None : Option<T>.Some(Get(0));
    public Option<T> Last => _count == 0 ? Option<T>.None : Option<T>.Some(Get(_count - 1));

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++) yield return Get(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(Vector<T>? other)
    {
        if (other is null || other._count != _count) return false;
        var cmp = EqualityComparer<T>.Default;
        for (var i = 0; i < _count; i++)
            if (!cmp.Equals(Get(i), other.Get(i))) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Vector<T> v && Equals(v);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var x in this) h.Add(x);
        return h.ToHashCode();
    }

    public override string ToString() => "[" + string.Join(", ", this) + "]";
}

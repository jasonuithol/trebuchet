using System.Collections;
using System.Numerics;

namespace Trebuchet.Runtime;

/// <summary>
/// Persistent hash map: a hash array mapped trie with 32-way bitmap-indexed nodes and
/// collision nodes. get, set and remove are O(log32 n). Structural equality, order-independent.
/// </summary>
public sealed class Map<K, V> : IReadOnlyDictionary<K, V>, IEquatable<Map<K, V>> where K : notnull
{
    private const int Bits = 5;
    private const int Mask = (1 << Bits) - 1;

    private abstract class Node
    {
        public abstract bool TryGet(int hash, int shift, K key, out V value);
        public abstract Node Set(int hash, int shift, K key, V value, ref int delta);
        public abstract Node? Remove(int hash, int shift, K key, ref int delta);
        public abstract IEnumerable<KeyValuePair<K, V>> Entries();
    }

    /// <summary>Bitmap-indexed node. Each set bit owns one slot holding either a leaf pair or a child node.</summary>
    private sealed class Bitmap : Node
    {
        private readonly uint _bitmap;
        private readonly object[] _slots; // Leaf or Node

        public static readonly Bitmap Empty = new(0, Array.Empty<object>());

        public Bitmap(uint bitmap, object[] slots)
        {
            _bitmap = bitmap;
            _slots = slots;
        }

        private static int Index(uint bitmap, uint bit) => BitOperations.PopCount(bitmap & (bit - 1));

        public override bool TryGet(int hash, int shift, K key, out V value)
        {
            var bit = 1u << ((hash >> shift) & Mask);
            if ((_bitmap & bit) == 0) { value = default!; return false; }
            var slot = _slots[Index(_bitmap, bit)];
            if (slot is Leaf leaf)
            {
                if (leaf.Hash == hash && EqualityComparer<K>.Default.Equals(leaf.Key, key)) { value = leaf.Value; return true; }
                value = default!;
                return false;
            }
            return ((Node)slot).TryGet(hash, shift + Bits, key, out value);
        }

        public override Node Set(int hash, int shift, K key, V value, ref int delta)
        {
            var bit = 1u << ((hash >> shift) & Mask);
            var idx = Index(_bitmap, bit);
            if ((_bitmap & bit) == 0)
            {
                var slots = new object[_slots.Length + 1];
                Array.Copy(_slots, 0, slots, 0, idx);
                slots[idx] = new Leaf(hash, key, value);
                Array.Copy(_slots, idx, slots, idx + 1, _slots.Length - idx);
                delta = 1;
                return new Bitmap(_bitmap | bit, slots);
            }
            var existing = _slots[idx];
            object replacement;
            if (existing is Leaf leaf)
            {
                if (leaf.Hash == hash && EqualityComparer<K>.Default.Equals(leaf.Key, key))
                {
                    delta = 0;
                    replacement = new Leaf(hash, key, value);
                }
                else
                {
                    delta = 1;
                    replacement = Merge(leaf, new Leaf(hash, key, value), shift + Bits);
                }
            }
            else replacement = ((Node)existing).Set(hash, shift + Bits, key, value, ref delta);
            var copy = (object[])_slots.Clone();
            copy[idx] = replacement;
            return new Bitmap(_bitmap, copy);
        }

        private static Node Merge(Leaf a, Leaf b, int shift)
        {
            if (a.Hash == b.Hash) return new Collision(a.Hash, new[] { a, b });
            if (shift >= 32) return new Collision(a.Hash, new[] { a, b });
            var ba = 1u << ((a.Hash >> shift) & Mask);
            var bb = 1u << ((b.Hash >> shift) & Mask);
            if (ba == bb) return new Bitmap(ba, new object[] { Merge(a, b, shift + Bits) });
            return ba < bb ? new Bitmap(ba | bb, new object[] { a, b }) : new Bitmap(ba | bb, new object[] { b, a });
        }

        public override Node? Remove(int hash, int shift, K key, ref int delta)
        {
            var bit = 1u << ((hash >> shift) & Mask);
            if ((_bitmap & bit) == 0) return this;
            var idx = Index(_bitmap, bit);
            var existing = _slots[idx];
            if (existing is Leaf leaf)
            {
                if (leaf.Hash != hash || !EqualityComparer<K>.Default.Equals(leaf.Key, key)) return this;
                delta = -1;
                if (_slots.Length == 1) return null;
                var slots = new object[_slots.Length - 1];
                Array.Copy(_slots, 0, slots, 0, idx);
                Array.Copy(_slots, idx + 1, slots, idx, _slots.Length - idx - 1);
                return new Bitmap(_bitmap & ~bit, slots);
            }
            var child = ((Node)existing).Remove(hash, shift + Bits, key, ref delta);
            if (ReferenceEquals(child, existing)) return this;
            if (child is null)
            {
                if (_slots.Length == 1) return null;
                var slots = new object[_slots.Length - 1];
                Array.Copy(_slots, 0, slots, 0, idx);
                Array.Copy(_slots, idx + 1, slots, idx, _slots.Length - idx - 1);
                return new Bitmap(_bitmap & ~bit, slots);
            }
            var copy = (object[])_slots.Clone();
            copy[idx] = child;
            return new Bitmap(_bitmap, copy);
        }

        public override IEnumerable<KeyValuePair<K, V>> Entries()
        {
            foreach (var slot in _slots)
            {
                if (slot is Leaf leaf) yield return new KeyValuePair<K, V>(leaf.Key, leaf.Value);
                else foreach (var e in ((Node)slot).Entries()) yield return e;
            }
        }
    }

    private sealed class Leaf
    {
        public readonly int Hash;
        public readonly K Key;
        public readonly V Value;
        public Leaf(int hash, K key, V value)
        {
            Hash = hash;
            Key = key;
            Value = value;
        }
    }

    private sealed class Collision : Node
    {
        private readonly int _hash;
        private readonly Leaf[] _leaves;
        public Collision(int hash, Leaf[] leaves)
        {
            _hash = hash;
            _leaves = leaves;
        }

        public override bool TryGet(int hash, int shift, K key, out V value)
        {
            foreach (var l in _leaves)
                if (EqualityComparer<K>.Default.Equals(l.Key, key)) { value = l.Value; return true; }
            value = default!;
            return false;
        }

        public override Node Set(int hash, int shift, K key, V value, ref int delta)
        {
            if (hash != _hash)
            {
                delta = 1;
                var bit = 1u << ((_hash >> shift) & Mask);
                return new Bitmap(bit, new object[] { this }).Set(hash, shift, key, value, ref delta);
            }
            for (var i = 0; i < _leaves.Length; i++)
            {
                if (EqualityComparer<K>.Default.Equals(_leaves[i].Key, key))
                {
                    delta = 0;
                    var copy = (Leaf[])_leaves.Clone();
                    copy[i] = new Leaf(hash, key, value);
                    return new Collision(_hash, copy);
                }
            }
            delta = 1;
            var grown = new Leaf[_leaves.Length + 1];
            Array.Copy(_leaves, grown, _leaves.Length);
            grown[^1] = new Leaf(hash, key, value);
            return new Collision(_hash, grown);
        }

        public override Node? Remove(int hash, int shift, K key, ref int delta)
        {
            var idx = Array.FindIndex(_leaves, l => EqualityComparer<K>.Default.Equals(l.Key, key));
            if (idx < 0) return this;
            delta = -1;
            if (_leaves.Length == 1) return null;
            var rest = _leaves.Where((_, i) => i != idx).ToArray();
            return rest.Length == 1 ? new Bitmap(1u << ((_hash >> shift) & Mask), new object[] { rest[0] }) : new Collision(_hash, rest);
        }

        public override IEnumerable<KeyValuePair<K, V>> Entries() => _leaves.Select(l => new KeyValuePair<K, V>(l.Key, l.Value));
    }

    public static readonly Map<K, V> Empty = new(Bitmap.Empty, 0);

    private readonly Node _root;
    private readonly int _count;

    private Map(Node root, int count)
    {
        _root = root;
        _count = count;
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    private static int HashOf(K key) => EqualityComparer<K>.Default.GetHashCode(key);

    public bool TryGetValue(K key, out V value) => _root.TryGet(HashOf(key), 0, key, out value);

    public bool ContainsKey(K key) => TryGetValue(key, out _);

    public Option<V> Get(K key) => TryGetValue(key, out var v) ? Option<V>.Some(v) : Option<V>.None;

    public V GetOr(K key, V fallback) => TryGetValue(key, out var v) ? v : fallback;

    public Map<K, V> Set(K key, V value)
    {
        var delta = 0;
        var root = _root.Set(HashOf(key), 0, key, value, ref delta);
        return new Map<K, V>(root, _count + delta);
    }

    public Map<K, V> Remove(K key)
    {
        var delta = 0;
        var root = _root.Remove(HashOf(key), 0, key, ref delta) ?? Bitmap.Empty;
        return delta == 0 ? this : new Map<K, V>(root, _count + delta);
    }

    public static Map<K, V> From(IEnumerable<KeyValuePair<K, V>> entries)
    {
        var m = Empty;
        foreach (var e in entries) m = m.Set(e.Key, e.Value);
        return m;
    }

    public IEnumerable<K> Keys => this.Select(e => e.Key);
    public IEnumerable<V> Values => this.Select(e => e.Value);
    public V this[K key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key.ToString());

    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _root.Entries().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(Map<K, V>? other)
    {
        if (other is null || other._count != _count) return false;
        var cmp = EqualityComparer<V>.Default;
        foreach (var e in this)
            if (!other.TryGetValue(e.Key, out var v) || !cmp.Equals(v, e.Value)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Map<K, V> m && Equals(m);

    public override int GetHashCode()
    {
        // order-independent
        var h = 0;
        foreach (var e in this) h ^= HashCode.Combine(e.Key, e.Value);
        return HashCode.Combine(_count, h);
    }

    public override string ToString() => "{" + string.Join(", ", this.Select(e => $"{e.Key}: {e.Value}")) + "}";
}

/// <summary>Persistent hash set over <see cref="Map{K,V}"/>.</summary>
public sealed class Set<T> : IReadOnlyCollection<T>, IEquatable<Set<T>> where T : notnull
{
    public static readonly Set<T> Empty = new(Map<T, Unit>.Empty);
    private readonly Map<T, Unit> _map;
    private Set(Map<T, Unit> map) => _map = map;

    public int Count => _map.Count;
    public bool IsEmpty => _map.IsEmpty;
    public bool Contains(T item) => _map.ContainsKey(item);
    public Set<T> Add(T item) => new(_map.Set(item, Unit.Value));
    public Set<T> Remove(T item) => new(_map.Remove(item));
    public static Set<T> From(IEnumerable<T> items)
    {
        var s = Empty;
        foreach (var x in items) s = s.Add(x);
        return s;
    }

    public Set<T> Union(Set<T> other)
    {
        var s = this;
        foreach (var x in other) s = s.Add(x);
        return s;
    }
    public Set<T> Intersect(Set<T> other) => From(this.Where(other.Contains));
    public Set<T> Difference(Set<T> other) => From(this.Where(x => !other.Contains(x)));

    public IEnumerator<T> GetEnumerator() => _map.Keys.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(Set<T>? other) => other is not null && _map.Equals(other._map);
    public override bool Equals(object? obj) => obj is Set<T> s && Equals(s);
    public override int GetHashCode() => _map.GetHashCode();
    public override string ToString() => "#{" + string.Join(", ", this) + "}";
}

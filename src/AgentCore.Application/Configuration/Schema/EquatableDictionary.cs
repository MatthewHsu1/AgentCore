using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// An immutable string-keyed map that compares by its entries and keeps its insertion order.
/// </summary>
/// <remarks>
/// The reason is the same as for <see cref="EquatableList{T}"/>: a record compares each field with
/// the default comparer, and a plain dictionary compares by reference.
/// </remarks>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class EquatableDictionary<TValue> : IReadOnlyDictionary<string, TValue>, IEquatable<EquatableDictionary<TValue>>
{
    private readonly Dictionary<string, TValue> _entries;
    private readonly string[] _order;

    /// <summary>Creates a map from the given entries, in the given order.</summary>
    /// <param name="entries">The entries to copy.</param>
    public EquatableDictionary(IEnumerable<KeyValuePair<string, TValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries = new Dictionary<string, TValue>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in entries)
        {
            _entries[entry.Key] = entry.Value;
            order.Add(entry.Key);
        }

        _order = [.. order];
    }

    private EquatableDictionary()
    {
        _entries = new Dictionary<string, TValue>(StringComparer.Ordinal);
        _order = [];
    }

    /// <summary>Gets the map with no entries.</summary>
    public static EquatableDictionary<TValue> Empty { get; } = new EquatableDictionary<TValue>();

    /// <summary>Gets the number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Gets the keys, in insertion order.</summary>
    public IEnumerable<string> Keys => _order;

    /// <summary>Gets the values, in key insertion order.</summary>
    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (var key in _order)
            {
                yield return _entries[key];
            }
        }
    }

    /// <summary>Gets the value of one key.</summary>
    /// <param name="key">The key to read.</param>
    public TValue this[string key] => _entries[key];

    /// <summary>Reports whether the map holds the key.</summary>
    /// <param name="key">The key to look for.</param>
    /// <returns><see langword="true"/> when the key is present.</returns>
    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <summary>Reads the value of one key.</summary>
    /// <param name="key">The key to read.</param>
    /// <param name="value">The value, when the key is present.</param>
    /// <returns><see langword="true"/> when the key is present.</returns>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out TValue value) => _entries.TryGetValue(key, out value);

    /// <summary>Enumerates the entries, in insertion order.</summary>
    /// <returns>An enumerator over the entries.</returns>
    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
    {
        foreach (var key in _order)
        {
            yield return new KeyValuePair<string, TValue>(key, _entries[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Compares this map with another map, entry by entry. The order does not matter.</summary>
    /// <param name="other">The other map.</param>
    /// <returns><see langword="true"/> when both maps hold the same keys and equal values.</returns>
    public bool Equals(EquatableDictionary<TValue>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_entries.Count != other._entries.Count)
        {
            return false;
        }

        foreach (var entry in _entries)
        {
            if (!other._entries.TryGetValue(entry.Key, out var otherValue))
            {
                return false;
            }

            if (!ConfigurationEquality.ValueEquals(entry.Value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as EquatableDictionary<TValue>);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var entry in _entries)
        {
            hash ^= HashCode.Combine(entry.Key, ConfigurationEquality.ValueHash(entry.Value));
        }

        return HashCode.Combine(_entries.Count, hash);
    }

    /// <summary>Compares two maps.</summary>
    /// <param name="left">The left map.</param>
    /// <param name="right">The right map.</param>
    /// <returns><see langword="true"/> when both maps are equal.</returns>
    public static bool operator ==(EquatableDictionary<TValue>? left, EquatableDictionary<TValue>? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two maps.</summary>
    /// <param name="left">The left map.</param>
    /// <param name="right">The right map.</param>
    /// <returns><see langword="true"/> when the maps differ.</returns>
    public static bool operator !=(EquatableDictionary<TValue>? left, EquatableDictionary<TValue>? right) => !(left == right);
}

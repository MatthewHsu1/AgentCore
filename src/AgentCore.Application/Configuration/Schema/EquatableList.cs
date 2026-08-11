using System.Collections;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// An immutable list that compares by its elements.
/// </summary>
/// <remarks>
/// The bound configuration records are <c>record</c> types, and a record compares each field with
/// the default comparer. A plain list compares by reference, so the same document loaded twice
/// would not be equal. This type gives the records element equality instead.
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    private readonly T[] _items;

    /// <summary>Creates a list from the given elements.</summary>
    /// <param name="items">The elements to copy.</param>
    public EquatableList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    private EquatableList() => _items = [];

    /// <summary>Gets the list with no elements.</summary>
    public static EquatableList<T> Empty { get; } = new EquatableList<T>();

    /// <summary>Gets the number of elements.</summary>
    public int Count => _items.Length;

    /// <summary>Gets the element at the given position.</summary>
    /// <param name="index">The zero-based position.</param>
    public T this[int index] => _items[index];

    /// <summary>Enumerates the elements.</summary>
    /// <returns>An enumerator over the elements.</returns>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Compares this list with another list, element by element.</summary>
    /// <param name="other">The other list.</param>
    /// <returns><see langword="true"/> when both lists hold equal elements in the same order.</returns>
    public bool Equals(EquatableList<T>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_items.Length != other._items.Length)
        {
            return false;
        }

        for (var index = 0; index < _items.Length; index++)
        {
            if (!ConfigurationEquality.ValueEquals(_items[index], other._items[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_items.Length);
        foreach (var item in _items)
        {
            hash.Add(ConfigurationEquality.ValueHash(item));
        }

        return hash.ToHashCode();
    }

    /// <summary>Compares two lists.</summary>
    /// <param name="left">The left list.</param>
    /// <param name="right">The right list.</param>
    /// <returns><see langword="true"/> when both lists are equal.</returns>
    public static bool operator ==(EquatableList<T>? left, EquatableList<T>? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two lists.</summary>
    /// <param name="left">The left list.</param>
    /// <param name="right">The right list.</param>
    /// <returns><see langword="true"/> when the lists differ.</returns>
    public static bool operator !=(EquatableList<T>? left, EquatableList<T>? right) => !(left == right);
}

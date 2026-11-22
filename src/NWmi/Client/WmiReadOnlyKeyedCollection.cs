using System.Collections;

namespace NWmi.Client;

/// <summary>
/// Named elements (properties, methods, qualifiers) in their declared order, looked up by a name matched
/// case-insensitively, as everywhere in CIM.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public class WmiReadOnlyKeyedCollection<T> : IReadOnlyCollection<T> where T : class
{
    private readonly List<T> _items;
    private readonly Dictionary<string, T> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<T, string> _nameOf;

    internal WmiReadOnlyKeyedCollection(IEnumerable<T> items, Func<T, string> nameOf)
    {
        _nameOf = nameOf;
        _items = [];
        foreach (var item in items)
            if (_byName.TryAdd(nameOf(item), item))
                _items.Add(item);
    }

    /// <summary>The element called <paramref name="name"/>, or null when there is none.</summary>
    public T? this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            return _byName.GetValueOrDefault(name);
        }
    }

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <summary>Whether there is an element called <paramref name="name"/>.</summary>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.ContainsKey(name);
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private protected void AddItem(T item)
    {
        var name = _nameOf(item);
        if (!_byName.TryAdd(name, item))
            throw new ArgumentException($"There is already an element named '{name}'.", nameof(item));

        _items.Add(item);
    }
}

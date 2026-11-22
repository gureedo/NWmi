namespace NWmi.Client;

/// <summary>A <see cref="WmiReadOnlyKeyedCollection{T}"/> that can be added to: the properties of a <see cref="WmiInstance"/>.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class WmiKeyedCollection<T> : WmiReadOnlyKeyedCollection<T> where T : class
{
    private readonly Action<T>? _beforeAdd;

    internal WmiKeyedCollection(IEnumerable<T> items, Func<T, string> nameOf, Action<T>? beforeAdd = null)
        : base(items, nameOf) => _beforeAdd = beforeAdd;

    /// <summary>Adds <paramref name="item"/>.</summary>
    /// <exception cref="ArgumentException">There is already an element of that name.</exception>
    /// <exception cref="InvalidOperationException">The collection does not take new elements (the properties of an instance that has a class from the server).</exception>
    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _beforeAdd?.Invoke(item);
        AddItem(item);
    }
}

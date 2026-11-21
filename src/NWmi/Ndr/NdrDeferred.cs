namespace NWmi.Ndr;

/// <summary>
/// The result of reading a pointer. An embedded pointer's referent is not in the stream until the
/// enclosing structure ends (C706 14.3.12), so <see cref="Value"/> is only available after that.
/// Top-level pointers are resolved before the read call returns.
/// </summary>
internal sealed class NdrDeferred<T>
{
    private T? _value;
    private List<Action<T>>? _continuations;

    internal NdrDeferred(uint referentId)
    {
        ReferentId = referentId;
    }

    /// <summary>The wire referent identifier; 0 for a null pointer.</summary>
    public uint ReferentId { get; }

    public bool IsNull => ReferentId == 0;

    public bool IsResolved { get; private set; }

    public T Value => IsResolved
        ? _value!
        : throw new InvalidOperationException(
            "The pointee has not been read yet; it is available once the enclosing structure has been fully read.");

    /// <summary>Runs <paramref name="continuation"/> with the value, now if resolved, otherwise when it is.</summary>
    public void Then(Action<T> continuation)
    {
        if (IsResolved)
        {
            continuation(_value!);
            return;
        }

        (_continuations ??= []).Add(continuation);
    }

    internal void Resolve(T value)
    {
        _value = value;
        IsResolved = true;
        if (_continuations is null)
            return;

        foreach (var continuation in _continuations)
            continuation(value);

        _continuations = null;
    }
}

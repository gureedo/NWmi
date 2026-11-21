using NWmi.Wmio;

namespace NWmi.Client;

/// <summary>
/// One object or class enumeration registered with a <see cref="WmiRefresher"/>. Its values are filled in by
/// <see cref="WmiRefresher.RefreshAsync"/>; before the first refresh <see cref="Object"/> is null and
/// <see cref="Objects"/> is empty.
/// </summary>
public sealed class WmiRefreshItem
{
    internal WmiRefreshItem(string target, bool isEnumeration, bool isHighPerformance, int cancelId, WmioClass? template)
    {
        Target = target;
        IsEnumeration = isEnumeration;
        IsHighPerformance = isHighPerformance;
        CancelId = cancelId;
        Template = template;
    }

    /// <summary>The object path or class name it was registered with.</summary>
    public string Target { get; }

    /// <summary>True for a class enumeration (<see cref="WmiRefresher.AddEnumerationAsync"/>), false for a single object.</summary>
    public bool IsEnumeration { get; }

    /// <summary>
    /// True when the provider refreshes it on the server (the <c>Win32_Perf*</c> classes): one call refreshes all such
    /// items together. Otherwise each refresh re-reads it with an ordinary <c>GetObject</c> or query.
    /// </summary>
    public bool IsHighPerformance { get; }

    /// <summary>The latest value of a single object; null before the first refresh, and for an enumeration.</summary>
    public WmiObject? Instance { get; internal set; }

    /// <summary>The latest instances of an enumeration; for a single object, empty.</summary>
    public IReadOnlyList<WmiObject> Objects { get; internal set; } = [];

    /// <summary>True when the server reported an error for this item in the last refresh (the previous values are kept).</summary>
    public bool LastRefreshFailed { get; internal set; }

    internal int CancelId { get; }

    internal WmioClass? Template { get; }

    internal bool Removed { get; set; }
}

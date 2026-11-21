namespace NWmi.Wmio;

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix

/// <summary>
/// The fixed dictionary of common strings (MS-WMIO 2.2.80).
/// A HeapRef whose top bit is set names one of these by ordinal instead of pointing into the heap, saving the space of repeating the same few strings.
/// </summary>
internal static class WmioDictionary
{
    private static readonly string[] Entries =
    [
        "\"", "key", "", "read", "write", "volatile", "provider", "dynamic", "cimwin32", "DWORD", "CIMTYPE",
    ];

    public static int Count => Entries.Length;

    public static string Get(int ordinal)
    {
        if ((uint)ordinal < (uint)Entries.Length)
            return Entries[ordinal];
        throw new WmioFormatException($"Dictionary ordinal {ordinal} is outside 0..{Entries.Length - 1}.");
    }

    public static int IndexOf(string value) => Array.IndexOf(Entries, value);
}

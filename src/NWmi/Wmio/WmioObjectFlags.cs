namespace NWmi.Wmio;

/// <summary>
/// The ObjectFlags byte that starts an ObjectEncoding (MS-WMIO 2.2.5): whether a class or an instance follows, and
/// whether a Decoration precedes it. The two top bits are the Prototype Result Object and incomplete-keys markers
/// of MS-WMI 2.2.4.1.
/// </summary>
[Flags]
internal enum WmioObjectFlags : byte
{
    None = 0,

    /// <summary>A class follows (CLASS_FLAG).</summary>
    Class = 0x01,

    /// <summary>An instance follows (INSTANCE_FLAG).</summary>
    Instance = 0x02,

    /// <summary>A Decoration (server and namespace names) precedes the object (DECORATION_FLAG).</summary>
    Decorated = 0x04,

    /// <summary>A Prototype Result Object (MS-WMI 2.2.4.1); only meaningful for a class.</summary>
    PrototypeResult = 0x10,

    /// <summary>The key properties are incomplete (MS-WMI 2.2.4.1); only meaningful for a class.</summary>
    IncompleteKeys = 0x40,
}

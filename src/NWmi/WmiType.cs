#pragma warning disable CA1720 // the member names are the CIM type names

namespace NWmi;

/// <summary>The CIM type of a property, parameter or qualifier value. An array type is its element type plus 16.</summary>
public enum WmiType
{
    /// <summary>The type is not known: a WinRM reply for a class whose definition the server does not give.</summary>
    Unknown = 0,

    /// <summary><c>boolean</c>.</summary>
    Boolean = 1,

    /// <summary><c>uint8</c>, a <see cref="byte"/>.</summary>
    UInt8 = 2,

    /// <summary><c>sint8</c>, an <see cref="sbyte"/>.</summary>
    SInt8 = 3,

    /// <summary><c>uint16</c>, a <see cref="ushort"/>.</summary>
    UInt16 = 4,

    /// <summary><c>sint16</c>, a <see cref="short"/>.</summary>
    SInt16 = 5,

    /// <summary><c>uint32</c>, a <see cref="uint"/>.</summary>
    UInt32 = 6,

    /// <summary><c>sint32</c>, an <see cref="int"/>.</summary>
    SInt32 = 7,

    /// <summary><c>uint64</c>, a <see cref="ulong"/>.</summary>
    UInt64 = 8,

    /// <summary><c>sint64</c>, a <see cref="long"/>.</summary>
    SInt64 = 9,

    /// <summary><c>real32</c>, a <see cref="float"/>.</summary>
    Real32 = 10,

    /// <summary><c>real64</c>, a <see cref="double"/>.</summary>
    Real64 = 11,

    /// <summary><c>char16</c>, a <see cref="char"/>.</summary>
    Char16 = 12,

    /// <summary>A DMTF datetime or interval string.</summary>
    DateTime = 13,

    /// <summary><c>string</c>.</summary>
    String = 14,

    /// <summary>The path of another instance.</summary>
    Reference = 15,

    /// <summary>An embedded object.</summary>
    Instance = 16,

    /// <summary>An array of <see cref="Boolean"/>.</summary>
    BooleanArray = 17,

    /// <summary>An array of <see cref="UInt8"/>.</summary>
    UInt8Array = 18,

    /// <summary>An array of <see cref="SInt8"/>.</summary>
    SInt8Array = 19,

    /// <summary>An array of <see cref="UInt16"/>.</summary>
    UInt16Array = 20,

    /// <summary>An array of <see cref="SInt16"/>.</summary>
    SInt16Array = 21,

    /// <summary>An array of <see cref="UInt32"/>.</summary>
    UInt32Array = 22,

    /// <summary>An array of <see cref="SInt32"/>.</summary>
    SInt32Array = 23,

    /// <summary>An array of <see cref="UInt64"/>.</summary>
    UInt64Array = 24,

    /// <summary>An array of <see cref="SInt64"/>.</summary>
    SInt64Array = 25,

    /// <summary>An array of <see cref="Real32"/>.</summary>
    Real32Array = 26,

    /// <summary>An array of <see cref="Real64"/>.</summary>
    Real64Array = 27,

    /// <summary>An array of <see cref="Char16"/>.</summary>
    Char16Array = 28,

    /// <summary>An array of <see cref="DateTime"/>.</summary>
    DateTimeArray = 29,

    /// <summary>An array of <see cref="String"/>.</summary>
    StringArray = 30,

    /// <summary>An array of <see cref="Reference"/>.</summary>
    ReferenceArray = 31,

    /// <summary>An array of <see cref="Instance"/>.</summary>
    InstanceArray = 32,
}

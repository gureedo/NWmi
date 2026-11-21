namespace NWmi.Ndr.Ole;

/// <summary>VARTYPE values (MS-OAUT 2.2.7). Only the types that can appear on the wire are listed.</summary>
internal enum OleVarType : ushort
{
    Empty = 0x0000,
    Null = 0x0001,
    I2 = 0x0002,
    I4 = 0x0003,
    R4 = 0x0004,
    R8 = 0x0005,
    Cy = 0x0006,
    Date = 0x0007,
    Bstr = 0x0008,
    Dispatch = 0x0009,
    Error = 0x000A,
    Bool = 0x000B,
    Variant = 0x000C,
    Unknown = 0x000D,
    Decimal = 0x000E,
    I1 = 0x0010,
    UI1 = 0x0011,
    UI2 = 0x0012,
    UI4 = 0x0013,
    I8 = 0x0014,
    UI8 = 0x0015,
    Int = 0x0016,
    UInt = 0x0017,
    Record = 0x0024,

    /// <summary>Flag: the value is a SAFEARRAY of the element type in the low bits.</summary>
    Array = 0x2000,

    /// <summary>Flag: the value is passed by reference. Not supported by this library.</summary>
    ByRef = 0x4000,
}

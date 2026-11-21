namespace NWmi.Wmio;

/// <summary>
/// The CIM type system (MS-WMIO 2.2.82): a 16-bit base type, optionally combined with the array flag.
/// Values match the base types exactly; an array of a base type is that value OR'd with <see cref="ArrayFlag"/>.
/// </summary>
internal enum CimType : ushort
{
    Sint8 = 16,
    Uint8 = 17,
    Sint16 = 2,
    Uint16 = 18,
    Sint32 = 3,
    Uint32 = 19,
    Sint64 = 20,
    Uint64 = 21,
    Real32 = 4,
    Real64 = 5,
    Boolean = 11,
    String = 8,
    DateTime = 101,
    Reference = 102,
    Char16 = 103,
    Object = 13,

    /// <summary>Set on an array's base type to mark it as an array (0x2000).</summary>
    ArrayFlag = 0x2000,
}

internal static class CimTypeExtensions
{
    extension(CimType type)
    {
        public bool IsArray() => (type & CimType.ArrayFlag) != 0;
        public CimType BaseType() => type & ~CimType.ArrayFlag;

        public CimType AsArray()
        {
            if (type.IsArray())
                throw new ArgumentException($"{type} is already an array type.", nameof(type));

            return type | CimType.ArrayFlag;
        }

        /// <summary>
        /// The number of octets a single value of this base type occupies inline in a ValueTable (MS-WMIO 2.2.72,
        /// 2.2.75): numeric types by their natural size, BOOL and CHAR16 as 2 bytes, and every HeapRef-based type
        /// (string, datetime, reference, object, and every array) as the 4-byte HeapRef itself.
        /// </summary>
        public int InlineValueSize()
        {
            if (type.IsArray())
                return 4; // every array value is a HeapRef to its Encoded-Array

            return type.BaseType() switch
            {
                CimType.Sint8 or CimType.Uint8 => 1,
                CimType.Sint16 or CimType.Uint16 or CimType.Char16 or CimType.Boolean => 2,
                CimType.Sint32 or CimType.Uint32 or CimType.Real32 => 4,
                CimType.Sint64 or CimType.Uint64 or CimType.Real64 => 8,
                CimType.String or CimType.DateTime or CimType.Reference or CimType.Object => 4,
                _ => throw new NotSupportedException($"Unknown CIM base type {type}."),
            };
        }

        /// <summary>True for the CIM base types this library can encode and decode.</summary>
        public bool IsSupportedBaseType() => Enum.IsDefined(type.BaseType());
    }
}

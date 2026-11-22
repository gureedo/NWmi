namespace NWmi.Wmio;

/// <summary>
/// The CIM type system (MS-WMIO 2.2.82): a 16-bit base type, optionally combined with the array flag.
/// Values match the base types exactly; an array of a base type is that value OR'd with <see cref="ArrayFlag"/>.
/// </summary>
internal enum WbemType : ushort
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

internal static class WbemTypeExtensions
{
    extension(WbemType type)
    {
        public bool IsArray() => (type & WbemType.ArrayFlag) != 0;
        public WbemType BaseType() => type & ~WbemType.ArrayFlag;

        public WbemType AsArray()
        {
            if (type.IsArray())
                throw new ArgumentException($"{type} is already an array type.", nameof(type));

            return type | WbemType.ArrayFlag;
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
                WbemType.Sint8 or WbemType.Uint8 => 1,
                WbemType.Sint16 or WbemType.Uint16 or WbemType.Char16 or WbemType.Boolean => 2,
                WbemType.Sint32 or WbemType.Uint32 or WbemType.Real32 => 4,
                WbemType.Sint64 or WbemType.Uint64 or WbemType.Real64 => 8,
                WbemType.String or WbemType.DateTime or WbemType.Reference or WbemType.Object => 4,
                _ => throw new NotSupportedException($"Unknown CIM base type {type}."),
            };
        }

        /// <summary>True for the CIM base types this library can encode and decode.</summary>
        public bool IsSupportedBaseType() => Enum.IsDefined(type.BaseType());
    }
}

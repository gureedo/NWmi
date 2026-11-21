using System.Buffers.Binary;

namespace NWmi.Dcom;

/// <summary>
/// NDR type serialization version 1 (MS-RPCE 2.2.6), the framing DCOM uses for each part of an activation
/// properties blob: an 8-byte common header, an 8-byte private header holding the data length, then the NDR
/// data padded to a multiple of 8 bytes. Only little-endian data is supported.
/// </summary>
internal static class TypeSerialization1
{
    private const byte Version = 1;
    private const byte LittleEndian = 0x10;
    private const ushort CommonHeaderLength = 8;
    private const uint CommonHeaderFiller = 0xCCCCCCCC;
    private const int HeadersSize = 16;
    private const int Alignment = 8;

    /// <summary>Frames a top-level constructed type that has already been NDR-encoded.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> ndrData)
    {
        var padded = (ndrData.Length + Alignment - 1) / Alignment * Alignment;
        var result = new byte[HeadersSize + padded];
        result[0] = Version;
        result[1] = LittleEndian;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), CommonHeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), CommonHeaderFiller);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)padded);
        ndrData.CopyTo(result.AsSpan(HeadersSize));
        return result;
    }

    /// <summary>
    /// Removes the framing and returns the NDR data, including its trailing padding.
    /// </summary>
    /// <param name="data">The bytes starting at the common header; more bytes may follow.</param>
    /// <param name="consumed">How many bytes the framed value occupies.</param>
    /// <exception cref="FormatException">The framing is invalid, big-endian, or longer than the data.</exception>
    public static byte[] Unwrap(ReadOnlySpan<byte> data, out int consumed)
    {
        if (data.Length < HeadersSize)
            throw new FormatException("The serialized data is too short for its headers.");

        if (data[0] != Version)
            throw new FormatException($"Unsupported type serialization version {data[0]}.");

        if (data[1] != LittleEndian)
            throw new FormatException("Only little-endian type serialization is supported.");

        if (BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != CommonHeaderLength)
            throw new FormatException("The common type header has the wrong length.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (length % Alignment != 0 || length > data.Length - HeadersSize)
            throw new FormatException($"The serialized object length {length} is invalid.");

        consumed = HeadersSize + (int)length;
        return [.. data.Slice(HeadersSize, (int)length)];
    }
}

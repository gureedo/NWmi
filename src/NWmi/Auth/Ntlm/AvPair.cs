using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace NWmi.Auth.Ntlm;

/// <summary>AV_PAIR (MS-NLMP 2.2.2.1)</summary>
internal readonly record struct AvPair(ushort Id, byte[] Value)
{
    public static List<AvPair> ParseList(ReadOnlySpan<byte> data)
    {
        var pairs = new List<AvPair>();
        var offset = 0;
        while (true)
        {
            if (data.Length - offset < 4)
                throw new SecurityProviderException("The NTLM target info is truncated or lacks its terminator.");

            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            offset += 4;
            if (id == AvId.EndOfList)
                return pairs;

            if (data.Length - offset < length)
                throw new SecurityProviderException("An NTLM target info attribute runs past the end of the data.");

            pairs.Add(new AvPair(id, [.. data.Slice(offset, length)]));
            offset += length;
        }
    }

    public static byte[] SerializeList(IEnumerable<AvPair> pairs)
    {
        var writer = new ArrayBufferWriter<byte>();
        foreach (var pair in pairs)
        {
            var header = writer.GetSpan(4);
            BinaryPrimitives.WriteUInt16LittleEndian(header, pair.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], checked((ushort)pair.Value.Length));
            writer.Advance(4);
            writer.Write(pair.Value);
        }

        writer.Write(stackalloc byte[4]); // MsvAvEOL, length 0
        return [.. writer.WrittenSpan];
    }

    public static AvPair Text(ushort id, string value) => new(id, Encoding.Unicode.GetBytes(value));

    public static AvPair UInt32(ushort id, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return new AvPair(id, bytes);
    }
}



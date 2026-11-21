using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>Decodes the ObjectArray buffer IWbemWCOSmartEnum::Next returns (MS-WMI 2.2.14).</summary>
internal static class ObjectArrayDecoder
{
    private static readonly byte[] Signature = [.. "WBEMDATA"u8];

    private const byte ObjectTypeClass = 1;
    private const byte ObjectTypeInstance = 2;
    private const byte ObjectTypeInstanceNoClass = 3;

    public static List<WmioObjectBlock?> Decode(ReadOnlySpan<byte> buffer, Dictionary<Guid, byte[]> classCache)
    {
        var reader = new BinaryReader(buffer);
        if (reader.ReadUInt32() != 0)
            throw new WmioFormatException("ObjectArray dwByteOrdering must be 0 (little-endian).");

        if (!reader.ReadSpan(8u).SequenceEqual(Signature))
            throw new WmioFormatException("ObjectArray is missing its \"WBEMDATA\" signature.");

        reader.Expect32("dwSizeOfHeader1", 0x1A);
        reader.ReadUInt32(); // dwDataSize1: redundant with the fields that follow, each self-describing
        reader.ReadUInt32(); // dwFlags: MUST be 0, not checked (servers have been observed not to honor every MUST)
        reader.ReadByte(); // bVersion
        reader.ReadByte(); // bPacketType
        reader.Expect32("dwSizeOfHeader2", 8);
        reader.ReadUInt32(); // dwDataSize2
        reader.Expect32("dwSizeOfHeader3", 12);
        reader.ReadUInt32(); // dwDataSize3
        var count = reader.ReadUInt32();
        if (count > (uint)buffer.Length)
            throw new WmioFormatException($"ObjectArray dwNumObjects ({count}) cannot exceed the buffer size.");

        var objects = new List<WmioObjectBlock?>((int)count);
        for (var i = 0; i < count; i++)
            objects.Add(DecodeDataPacketObject(ref reader, classCache));

        return objects;
    }

    /// <summary>WBEM_DATAPACKET_OBJECT (MS-WMI 2.2.14.1).</summary>
    private static WmioObjectBlock DecodeDataPacketObject(ref BinaryReader reader, Dictionary<Guid, byte[]> classCache)
    {
        reader.Expect32("WBEM_DATAPACKET_OBJECT.dwSizeOfHeader", 9);
        var dataSize = reader.ReadUInt32();
        var objectType = reader.ReadByte();
        var objectData = reader.ReadSpan(dataSize);

        return objectType switch
        {
            ObjectTypeClass => DecodeClass(objectData),
            ObjectTypeInstance => DecodeInstance(objectData, classCache),
            ObjectTypeInstanceNoClass => DecodeInstanceNoClass(objectData, classCache),
            _ => throw new WmioFormatException($"WBEM_DATAPACKET_OBJECT.bObjectType {objectType} is not valid."),
        };
    }

    /// <summary>WBEMOBJECT_CLASS (MS-WMI 2.2.14.2): a standalone class, with no classID to cache it under.</summary>
    private static WmioObjectBlock DecodeClass(ReadOnlySpan<byte> objectData)
    {
        var reader = new BinaryReader(objectData);
        reader.Expect32("WBEMOBJECT_CLASS.dwSizeOfHeader", 8);
        var dataSize = reader.ReadUInt32();
        return WmioObjectBlock.Decode(new WmioReader(reader.ReadSpan(dataSize).ToArray()));
    }

    /// <summary>WBEMOBJECT_INSTANCE (MS-WMI 2.2.14.3): a full instance, with its own embedded class, cached under classID.</summary>
    private static WmioObjectBlock DecodeInstance(ReadOnlySpan<byte> objectData, Dictionary<Guid, byte[]> classCache)
    {
        var reader = new BinaryReader(objectData);
        reader.Expect32("WBEMOBJECT_INSTANCE.dwSizeOfHeader", 0x18);
        var dataSize = reader.ReadUInt32();
        var classId = new Guid(reader.ReadSpan(16u));
        var encoded = reader.ReadSpan(dataSize).ToArray();
        var block = WmioObjectBlock.Decode(new WmioReader(encoded));
        if (block.Instance is not null)
            classCache[classId] = ExtractRawCurrentClass(encoded);

        return block;
    }

    /// <summary>
    /// WBEMOBJECT_INSTANCE_NOCLASS (MS-WMI 2.2.14.4): an instance whose encoding omits CurrentClass; it is
    /// spliced back in from <paramref name="classCache"/> before decoding (MS-WMIO 2.2.3's own guidance for
    /// this exact case, carried out here rather than in <c>NWmi.Wmio</c>).
    /// </summary>
    private static WmioObjectBlock DecodeInstanceNoClass(ReadOnlySpan<byte> objectData,
        Dictionary<Guid, byte[]> classCache)
    {
        var header = new BinaryReader(objectData);
        header.Expect32("WBEMOBJECT_INSTANCE_NOCLASS.dwSizeOfHeader", 0x18);
        var dataSize = header.ReadUInt32();
        var classId = new Guid(header.ReadSpan(16u));
        var body = header.ReadSpan(dataSize).ToArray();

        if (!classCache.TryGetValue(classId, out var rawCurrentClass))
            throw new WmioFormatException(
                $"An ObjectArray instance references class {classId} before any instance of it carried the class definition.");

        var bodyReader = new WmioReader(body);
        var flags = (WmioObjectFlags)bodyReader.ReadByte();
        if (!flags.HasFlag(WmioObjectFlags.Instance) || flags.HasFlag(WmioObjectFlags.Class))
            throw new WmioFormatException(
                $"WBEMOBJECT_INSTANCE_NOCLASS's ObjectFlags (0x{(byte)flags:X2}) must mark an instance, not a class.");

        string? server = null;
        string? ns = null;
        if (flags.HasFlag(WmioObjectFlags.Decorated))
        {
            server = bodyReader.ReadEncodedString();
            ns = bodyReader.ReadEncodedString();
        }

        var instanceBody = bodyReader.ReadBytes(bodyReader.Remaining);

        var spliced = new WmioWriter();
        spliced.WriteByte((byte)flags);
        if (server is not null)
        {
            spliced.WriteEncodedString(server);
            spliced.WriteEncodedString(ns!);
        }

        spliced.WriteBytes(rawCurrentClass);
        spliced.WriteBytes(instanceBody);

        return WmioObjectBlock.Decode(new WmioReader(spliced.ToArray()));
    }

    /// <summary>
    /// Slices out exactly the CurrentClass ClassPart's bytes (MS-WMIO 2.2.14, with no following MethodsPart:
    /// an instance's embedded class never has one) from an already-decoded WBEMOBJECT_INSTANCE's encoded
    /// object bytes, verbatim, by walking only as far as ObjectFlags, an optional Decoration, and the
    /// ClassPart's own self-inclusive length say to.
    /// </summary>
    private static byte[] ExtractRawCurrentClass(byte[] encoded)
    {
        var reader = new WmioReader(encoded);
        var flags = (WmioObjectFlags)reader.ReadByte();
        if (flags.HasFlag(WmioObjectFlags.Decorated))
        {
            reader.ReadEncodedString();
            reader.ReadEncodedString();
        }

        var classPartStart = reader.Position;
        var classPart = reader.ReadLengthPrefixedBlock();
        return encoded[classPartStart..(classPartStart + classPart.Length)];
    }

    /// <summary>A tiny forward-only byte reader for the ObjectArray headers, which are plain, unaligned, little-endian integers.</summary>
    private ref struct BinaryReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public uint ReadUInt32()
        {
            if (_data.Length < 4)
                throw new WmioFormatException($"Need 4 bytes but only {_data.Length} remain.");

            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_data);
            _data = _data[4..];
            return value;
        }

        public byte ReadByte()
        {
            if (_data.Length < 1)
                throw new WmioFormatException("Need 1 byte but none remain.");

            var value = _data[0];
            _data = _data[1..];
            return value;
        }

        /// <summary>A bounds check done in the unsigned domain, so an oversized <paramref name="count"/> cannot overflow an <c>int</c> cast.</summary>
        public ReadOnlySpan<byte> ReadSpan(uint count)
        {
            if (count > (uint)_data.Length)
                throw new WmioFormatException($"Need {count} bytes but only {_data.Length} remain.");

            var span = _data[..(int)count];
            _data = _data[(int)count..];
            return span;
        }

        public void Expect32(string fieldName, uint expected)
        {
            var actual = ReadUInt32();
            if (actual != expected)
                throw new WmioFormatException($"{fieldName} must be 0x{expected:X}, not 0x{actual:X}.");
        }
    }
}

using System.Buffers.Binary;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmi;

public class ObjectArrayDecoderTests
{
    private static WmioClass OneIntProperty() => new(
        "TestClass",
        [],
        [],
        [new WmioProperty("Count", WbemType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(false, false), 0, 0)],
        []);

    private static byte[] EncodeClassBody(WmioClass @class)
    {
        var writer = new WmioWriter();
        @class.Write(writer, hasMethodsPart: false);
        return writer.ToArray();
    }

    /// <summary>The bytes of a full WmioInstance encoding that follow its embedded CurrentClass (MS-WMIO 2.2.53).</summary>
    private static byte[] EncodeInstanceBodyOnly(WmioInstance instance, byte[] classBytes)
    {
        var writer = new WmioWriter();
        instance.Write(writer);
        var full = writer.ToArray();
        Assert.True(full.AsSpan(0, classBytes.Length).SequenceEqual(classBytes));
        return full[classBytes.Length..];
    }

    private static byte[] EncodeFullInstanceObjectBlock(WmioInstance instance)
    {
        var writer = new WmioWriter();
        WmioObjectBlock.ForInstance(instance).Encode(writer);
        return writer.ToArray();
    }

    private static byte[] BuildNoClassObjectData(byte[] instanceBodyOnly)
    {
        var writer = new WmioWriter();
        writer.WriteByte(0x02); // ObjectFlags: instance bit, no decoration
        writer.WriteBytes(instanceBodyOnly);
        return writer.ToArray();
    }

    /// <summary>
    /// WBEM_DATAPACKET_OBJECT wrapping either a WBEMOBJECT_CLASS (<paramref name="classId"/> null) or a
    /// WBEMOBJECT_INSTANCE/_NOCLASS (<paramref name="classId"/> set), per MS-WMI 2.2.14.1-.4.
    /// </summary>
    private static byte[] BuildDataPacketObject(byte objectType, Guid? classId, byte[] objectData)
    {
        byte[] inner;
        if (classId is null)
        {
            inner = new byte[8 + objectData.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(inner, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(4), (uint)objectData.Length);
            objectData.CopyTo(inner.AsSpan(8));
        }
        else
        {
            inner = new byte[0x18 + objectData.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(inner, 0x18);
            BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(4), (uint)objectData.Length);
            classId.Value.TryWriteBytes(inner.AsSpan(8, 16));
            objectData.CopyTo(inner.AsSpan(0x18));
        }

        using var stream = new MemoryStream();
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, 9); // WBEM_DATAPACKET_OBJECT.dwSizeOfHeader
        stream.Write(four);
        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)inner.Length); // dwSizeOfData
        stream.Write(four);
        stream.WriteByte(objectType);
        stream.Write(inner);
        return stream.ToArray();
    }

    private static byte[] BuildObjectArray(params byte[][] dataPacketObjects)
    {
        using var stream = new MemoryStream();
        var four = new byte[4];
        void WriteU32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(four, v);
            stream.Write(four);
        }

        var body = dataPacketObjects.SelectMany(b => b).ToArray();

        WriteU32(0); // dwByteOrdering
        stream.Write("WBEMDATA"u8);
        WriteU32(0x1A); // dwSizeOfHeader1
        WriteU32(8); // dwDataSize1 (not checked by the decoder)
        WriteU32(0); // dwFlags
        stream.WriteByte(1); // bVersion
        stream.WriteByte(1); // bPacketType
        WriteU32(8); // dwSizeOfHeader2
        WriteU32((uint)(4 + body.Length)); // dwDataSize2
        WriteU32(12); // dwSizeOfHeader3
        WriteU32((uint)body.Length); // dwDataSize3
        WriteU32((uint)dataPacketObjects.Length); // dwNumObjects
        stream.Write(body);
        return stream.ToArray();
    }

    [Fact]
    public void A_standalone_class_object_decodes()
    {
        var @class = OneIntProperty();

        // WmioObjectBlock has no public class-encoding entry point (class encoding for PutClass is out of
        // scope; WmioObjectBlock.Encode throws for a class block), so this builds
        // the WBEMOBJECT_CLASS payload by hand: ObjectFlags, then ParentClass and CurrentClass
        // (WmioObjectBlock.Decode's class branch always reads both, MS-WMIO 2.2.5). ParentClass's content is
        // discarded by the decoder, so reusing the same class encoding for both slots is fine.
        var payload = new WmioWriter();
        payload.WriteByte(0x01); // ObjectFlags: class bit
        @class.Write(payload, hasMethodsPart: true); // ParentClass (discarded by the decoder)
        @class.Write(payload, hasMethodsPart: true); // CurrentClass

        var buffer = BuildObjectArray(BuildDataPacketObject(1, null, payload.ToArray()));
        var cache = new Dictionary<Guid, byte[]>();
        var objects = ObjectArrayDecoder.Decode(buffer, cache);

        var decoded = Assert.Single(objects);
        Assert.True(decoded!.IsClass);
        Assert.Equal("TestClass", decoded.Class!.Name);
        Assert.Empty(cache);
    }

    [Fact]
    public void An_instance_with_its_class_is_decoded_and_cached_then_a_classless_instance_reuses_it()
    {
        var @class = OneIntProperty();
        var classId = Guid.NewGuid();

        var instance1 = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = 1 });
        var instance2 = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = 2 });

        var classBytes = EncodeClassBody(@class);
        var fullObjectData = EncodeFullInstanceObjectBlock(instance1);
        var bodyOnly = EncodeInstanceBodyOnly(instance2, classBytes);
        var noClassObjectData = BuildNoClassObjectData(bodyOnly);

        var buffer = BuildObjectArray(
            BuildDataPacketObject(2, classId, fullObjectData),
            BuildDataPacketObject(3, classId, noClassObjectData));

        var cache = new Dictionary<Guid, byte[]>();
        var objects = ObjectArrayDecoder.Decode(buffer, cache);

        Assert.Equal(2, objects.Count);
        Assert.Equal(1, objects[0]!.Instance!.Values["Count"]);
        Assert.Equal(2, objects[1]!.Instance!.Values["Count"]);
        Assert.Equal("TestClass", objects[1]!.Instance!.Class.Name);
        Assert.Equal(classBytes, cache[classId]); // cached verbatim, not re-encoded from the decoded WmioClass
    }

    [Fact]
    public void A_classless_instance_referencing_an_unknown_class_id_throws()
    {
        var @class = OneIntProperty();
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = 1 });
        var classBytes = EncodeClassBody(@class);
        var bodyOnly = EncodeInstanceBodyOnly(instance, classBytes);
        var noClassObjectData = BuildNoClassObjectData(bodyOnly);

        var buffer = BuildObjectArray(BuildDataPacketObject(3, Guid.NewGuid(), noClassObjectData));

        Assert.Throws<WmioFormatException>(() => ObjectArrayDecoder.Decode(buffer, []));
    }
}

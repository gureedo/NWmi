using System.Buffers.Binary;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>Decodes the blobs of IWbemRemoteRefresher::RemoteRefresh (MS-WMI 2.2.15 to 2.2.19).</summary>
internal static class RefreshedInstanceDecoder
{
    /// <summary>A single object's blob (<see cref="WbemInstanceBlobType.All"/>): one RefreshedSingleInstance.</summary>
    public static WmioInstance DecodeSingle(WmioClass template, ReadOnlySpan<byte> blob) =>
        DecodeInstance(template, blob);

    /// <summary>An enumeration's blob (<see cref="WbemInstanceBlobType.Enum"/>, WBEM_INSTANCE_BLOB): version, count, then size-prefixed instances.</summary>
    public static List<WmioInstance> DecodeEnum(WmioClass template, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 8)
            throw new WmioFormatException("A refreshed enumeration is shorter than its header.");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (version != 1)
            throw new WmioFormatException($"A refreshed enumeration has version {version}, not 1.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);
        if (count > (uint)blob.Length)
            throw new WmioFormatException($"A refreshed enumeration announces {count} objects in {blob.Length} bytes.");

        var instances = new List<WmioInstance>((int)count);
        var position = 8;
        for (var i = 0; i < count; i++)
        {
            if (blob.Length - position < 4)
                throw new WmioFormatException("A refreshed enumeration is cut off inside an object's size.");

            var size = BinaryPrimitives.ReadUInt32LittleEndian(blob[position..]);
            position += 4;
            if (size > (uint)(blob.Length - position))
                throw new WmioFormatException(
                    $"A refreshed object claims {size} bytes but {blob.Length - position} remain.");

            instances.Add(DecodeInstance(template, blob.Slice(position, (int)size)));
            position += (int)size;
        }

        return instances;
    }

    /// <summary>
    /// Decodes a RefreshedSingleInstance (MS-WMI 2.2.19) against <paramref name="template"/>, the class of the
    /// instances being refreshed: the instance heap's size, then the NdTable, InstanceData, InstanceQualifierSet,
    /// the property-qualifier flag and the heap, i.e. an InstanceType's body minus the flags and class name that
    /// the template already supplies. It is re-framed as a full InstanceType, so the one decoder serves both.
    /// </summary>
    private static WmioInstance DecodeInstance(WmioClass template, ReadOnlySpan<byte> blob)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.RawClassPart is not { } rawClass)
            throw new WmioFormatException("A refreshed instance needs a decoded template class.");

        if (blob.Length < 4)
            throw new WmioFormatException("A refreshed instance is shorter than its heap size field.");

        var writer = new WmioWriter();
        writer.WriteBytes(rawClass);
        // The block's length counts its own 4 bytes, the flags byte, the class name reference and the payload.
        writer.WriteUInt32((uint)(sizeof(uint) + 1 + sizeof(uint) + (blob.Length - 4)));
        writer.WriteByte(0); // InstanceFlags
        writer.WriteUInt32(WmioHeap.NullRef); // no class name: the template gives it
        writer.WriteBytes(blob[4..]); // the leading heap size is implied by the heap's own header
        return WmioInstance.Read(new WmioReader(writer.ToArray()));
    }
}

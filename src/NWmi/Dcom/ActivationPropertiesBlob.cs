using System.Buffers.Binary;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;

namespace NWmi.Dcom;

/// <summary>
/// The activation properties BLOB (MS-DCOM 2.2.22): <c>dwSize</c>, <c>dwReserved</c>, the framed CustomHeader, then
/// each framed property in the order the header lists them. This type only joins and splits the parts; the parts
/// themselves are <see cref="IActivationProperty"/>s, built by <see cref="InstantiationInfoData"/>,
/// <see cref="ScmRequestInfoData"/> and the others; a property read back becomes the matching type where it can be decoded and an
/// <see cref="UnknownActivationProperty"/> otherwise.
/// </summary>
internal static class ActivationPropertiesBlob
{
    private const int PrefixSize = 8; // dwSize and dwReserved

    public static byte[] Build(IReadOnlyList<IActivationProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Count == 0)
            throw new ArgumentException("An activation properties BLOB needs at least one property.",
                nameof(properties));

        var framed = properties.Select(p => TypeSerialization1.Wrap(p.Encode())).ToList();
        var header = new DcomCustomHeader(
            [.. properties.Select(p => p.Clsid)],
            [.. framed.Select(f => (uint)f.Length)]);

        // The header's own size does not depend on the size values it carries, so measure it first.
        var headerLength = TypeSerialization1.Wrap(header.Encode(0, 0)).Length;
        var total = headerLength + framed.Sum(f => f.Length);
        var framedHeader = TypeSerialization1.Wrap(header.Encode((uint)total, (uint)headerLength));

        var blob = new byte[PrefixSize + total];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)total);
        framedHeader.CopyTo(blob, PrefixSize);
        var offset = PrefixSize + framedHeader.Length;
        foreach (var part in framed)
        {
            part.CopyTo(blob, offset);
            offset += part.Length;
        }

        return blob;
    }

    public static IReadOnlyList<IActivationProperty> Parse(ReadOnlySpan<byte> blob)
    {
        try
        {
            return ParseCore(blob);
        }
        catch (NdrFormatException ex)
        {
            throw new FormatException("The activation properties header is malformed.", ex);
        }
    }

    private static List<IActivationProperty> ParseCore(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < PrefixSize)
            throw new FormatException("The activation properties BLOB is too short.");

        var size = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (size > blob.Length - PrefixSize)
            throw new FormatException($"The BLOB announces {size} bytes but holds {blob.Length - PrefixSize}.");

        var body = blob.Slice(PrefixSize, (int)size);
        var headerData = TypeSerialization1.Unwrap(body, out var headerUsed);
        var header = DcomCustomHeader.Decode(headerData);

        var properties = new List<IActivationProperty>(header.PropertyClsids.Count);
        var offset = headerUsed;
        for (var i = 0; i < header.PropertyClsids.Count; i++)
        {
            var length = header.PropertySizes[i];
            if (length > body.Length - offset)
                throw new FormatException(
                    $"Property {i} claims {length} bytes but only {body.Length - offset} remain.");

            var data = TypeSerialization1.Unwrap(body.Slice(offset, (int)length), out var used);
            if (used != length)
                throw new FormatException($"Property {i} does not fill the {length} bytes the header gives it.");

            properties.Add(Decode(header.PropertyClsids[i], data));
            offset += (int)length;
        }

        return properties;
    }

    private static IActivationProperty Decode(Guid clsid, byte[] data)
    {
        if (clsid == InstantiationInfoData.PropertyClsid)
            return InstantiationInfoData.Decode(data);

        if (clsid == ScmRequestInfoData.PropertyClsid)
            return ScmRequestInfoData.Decode(data);

        if (clsid == ScmReplyInfoData.PropertyClsid)
            return ScmReplyInfoData.Decode(data);

        if (clsid == PropsOutInfo.PropertyClsid)
            return PropsOutInfo.Decode(data);

        return new UnknownActivationProperty(clsid, data);
    }
}

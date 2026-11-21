using System.Buffers.Binary;
using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;

namespace NWmi.Tests.Dcom;

public class ActivationPropertiesBlobTests
{
    private static readonly Guid ClassId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static List<IActivationProperty> RequiredProperties() =>
    [
        new InstantiationInfoData(ClassId, [DcomIds.IUnknown]),
        new ScmRequestInfoData([DualStringArray.TcpTowerId]),
        new LocationInfoData(),
    ];

    [Fact]
    public void The_sizes_in_the_blob_add_up()
    {
        var properties = RequiredProperties();
        var blob = ActivationPropertiesBlob.Build(properties);

        var dwSize = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        Assert.Equal((uint)(blob.Length - 8), dwSize);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4))); // dwReserved

        // The CustomHeader starts right after the prefix, framed: 16 bytes of headers, then its totalSize field.
        Assert.Equal(dwSize, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8 + 16)));

        // Each property is framed and padded to a multiple of 8, and the whole blob is a multiple of 8 after the prefix.
        Assert.Equal(0, (blob.Length - 8) % 8);
    }

    [Fact]
    public void The_header_lists_the_properties_in_order_with_their_framed_sizes()
    {
        var properties = RequiredProperties();
        var blob = ActivationPropertiesBlob.Build(properties);

        var parsed = ActivationPropertiesBlob.Parse(blob);

        Assert.Equal(properties.Select(p => p.Clsid), parsed.Select(p => p.Clsid));
        Assert.IsType<InstantiationInfoData>(parsed[0]);
        Assert.IsType<ScmRequestInfoData>(parsed[1]);

        // A property with no decoder is kept as received: the original plus alignment padding.
        var location = Assert.IsType<UnknownActivationProperty>(parsed[2]);
        var original = properties[2].Encode();
        Assert.Equal(original, location.Data[..original.Length]);
        Assert.All(location.Data[original.Length..], b => Assert.Equal(0, b));
        Assert.Equal(0, location.Data.Length % 8);
    }

    [Fact]
    public void A_parsed_property_can_be_decoded_again()
    {
        var parsed = ActivationPropertiesBlob.Parse(ActivationPropertiesBlob.Build(RequiredProperties()));

        var instantiation = Assert.IsType<InstantiationInfoData>(parsed[0]);
        Assert.Equal(ClassId, instantiation.ClassId);
        Assert.Equal([DcomIds.IUnknown], instantiation.RequestedInterfaces);

        var request = Assert.IsType<ScmRequestInfoData>(parsed[1]);
        Assert.Equal([DualStringArray.TcpTowerId], request.RequestedProtocolSequences);
    }

    [Fact]
    public void A_blob_needs_at_least_one_property()
    {
        Assert.Throws<ArgumentException>(() => ActivationPropertiesBlob.Build([]));
    }

    [Fact]
    public void Truncated_or_inconsistent_blobs_are_rejected()
    {
        var blob = ActivationPropertiesBlob.Build(RequiredProperties());

        Assert.Throws<FormatException>(() => ActivationPropertiesBlob.Parse(blob.AsSpan(0, 4)));
        Assert.Throws<FormatException>(() => ActivationPropertiesBlob.Parse(blob.AsSpan(0, blob.Length - 8)));

        var oversized = (byte[])blob.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(oversized, 0xFFFFFF00);
        Assert.Throws<FormatException>(() => ActivationPropertiesBlob.Parse(oversized));
    }

    [Fact]
    public void Corrupt_blobs_only_ever_throw_FormatException()
    {
        var original = ActivationPropertiesBlob.Build(RequiredProperties());
        var random = new Random(6001);
        for (var round = 0; round < 5000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                ActivationPropertiesBlob.Parse(mutated);
            }
            catch (FormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}

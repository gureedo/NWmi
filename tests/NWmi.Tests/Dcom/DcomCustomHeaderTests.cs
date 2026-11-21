using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class DcomCustomHeaderTests
{
    private static readonly Guid ClsidA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ClsidB = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    [Fact]
    public void One_property_has_the_documented_layout()
    {
        var header = new DcomCustomHeader([ClsidA], [10]);

        var expected = Hex.Parse(
            "e7030000 | 58000000 | 00000000 | 02000000 | 01000000 | " + // totalSize, headerSize, reserved, destCtx, cIfs
            "00000000000000000000000000000000 | " + // classInfoClsid (GUID_NULL)
            "00000200 | 04000200 | 00000000 | " + // pclsid, pSizes, pdwReserved (null)
            "01000000 | 11111111222233334444555555555555 | " + // pclsid array: max_count 1, ClsidA
            "01000000 | 0a000000"); // pSizes array: max_count 1, size 10

        Assert.Equal(expected, header.Encode(totalSize: 999, headerSize: 88));
    }

    [Fact]
    public void Headers_with_several_properties_round_trip()
    {
        var header = new DcomCustomHeader([ClsidA, ClsidB], [100, 4000]);
        var encoded = header.Encode(totalSize: 12345, headerSize: 0);

        var decoded = DcomCustomHeader.Decode(encoded);

        Assert.Equal(header.PropertyClsids, decoded.PropertyClsids);
        Assert.Equal(header.PropertySizes, decoded.PropertySizes);
    }

    [Fact]
    public void A_built_blob_carries_the_framed_header_size_and_its_own_total_size()
    {
        // Windows sends headerSize = the header's size including its type-serialization framing (192 for a
        // 176-byte header) and totalSize = dwSize; a real server rejects any other headerSize.
        var blob = ActivationPropertiesBlob.Build([new UnknownActivationProperty(ClsidA, new byte[8]), new UnknownActivationProperty(ClsidB, new byte[16])]);

        var dwSize = BitConverter.ToUInt32(blob, 0);
        var framedHeaderLength = 16 + (int)BitConverter.ToUInt32(blob, 8 + 8);
        var headerNdr = 8 + 16;
        Assert.Equal(dwSize, BitConverter.ToUInt32(blob, headerNdr));
        Assert.Equal((uint)framedHeaderLength, BitConverter.ToUInt32(blob, headerNdr + 4));
    }

    [Fact]
    public void A_header_is_rejected_without_at_least_one_property()
    {
        Assert.Throws<ArgumentException>(() => new DcomCustomHeader([], []));
        Assert.Throws<ArgumentException>(() => new DcomCustomHeader([ClsidA], []));
    }

    [Fact]
    public void Decode_rejects_mismatched_array_lengths_and_absurd_counts()
    {
        var encoded = new DcomCustomHeader([ClsidA], [10]).Encode(0, 0);

        // cIfs field, at offset 16, claims 2 properties while only 1 is present.
        var mismatched = (byte[])encoded.Clone();
        mismatched[16] = 2;
        Assert.Throws<NdrFormatException>(() => DcomCustomHeader.Decode(mismatched));

        var huge = (byte[])encoded.Clone();
        BitConverter.TryWriteBytes(huge.AsSpan(16), 0x7FFFFFFFu);
        Assert.Throws<NdrFormatException>(() => DcomCustomHeader.Decode(huge));
    }

    [Fact]
    public void Corrupt_headers_only_ever_throw_NdrFormatException()
    {
        var original = new DcomCustomHeader([ClsidA, ClsidB], [10, 20]).Encode(500, 0);
        var random = new Random(4040);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                DcomCustomHeader.Decode(mutated);
            }
            catch (NdrFormatException)
            {
                // Expected for corrupt input; any other exception type fails the test.
            }
        }
    }
}

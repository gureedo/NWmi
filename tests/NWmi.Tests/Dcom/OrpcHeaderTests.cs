using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class OrpcHeaderTests
{
    private static readonly Guid SampleGuid = Guid.Parse("12345678-9abc-def0-1122-334455667788");

    [Fact]
    public void ORPCTHIS_has_the_documented_layout()
    {
        var writer = new NdrWriter();
        OrpcHeader.WriteThis(writer, SampleGuid);

        Assert.Equal(
            Hex.Parse("0500 0700 | 00000000 | 00000000 | 78563412 bc9a f0de 1122334455667788 | 00000000"),
            writer.ToArray());
    }

    [Fact]
    public void ORPCTHIS_is_followed_directly_by_the_arguments()
    {
        var writer = new NdrWriter();
        OrpcHeader.WriteThis(writer, Guid.NewGuid());
        writer.WriteUInt32(0xCAFEBABE);

        Assert.Equal(36, writer.Position);
        Assert.Equal(Hex.Parse("bebafeca"), writer.ToArray()[32..]);
    }

    [Fact]
    public void ORPCTHAT_without_extensions_is_eight_bytes()
    {
        var reader = new NdrReader(Hex.Parse("07000000 00000000 | aabbccdd"));
        var that = OrpcHeader.ReadThat(reader);

        Assert.Equal(7u, that.Flags);
        Assert.Empty(that.Extensions);
        Assert.Equal(4, reader.Remaining); // the results are still unread
    }

    [Fact]
    public void ORPCTHAT_extensions_are_parsed_so_the_results_line_up()
    {
        // flags, pointer | size 1, reserved, pointer | max_count 2, pointer, null | max_count 8, id, size 3, data + padding | results
        var bytes = Hex.Parse(
            "01000000 00000200 | 01000000 00000000 04000200 | 02000000 08000200 00000000 | " +
            "08000000 78563412 bc9a f0de 1122334455667788 03000000 0a0b0c0000000000 | 11223344");

        var reader = new NdrReader(bytes);
        var that = OrpcHeader.ReadThat(reader);

        Assert.Equal(1u, that.Flags);
        var extent = Assert.Single(that.Extensions);
        Assert.Equal(SampleGuid, extent.Id);
        Assert.Equal(new byte[] { 0x0a, 0x0b, 0x0c }, extent.Data);
        Assert.Equal(Hex.Parse("11223344"), reader.ReadBytes(reader.Remaining));
    }

    [Fact]
    public void ORPCTHAT_extension_with_two_entries()
    {
        // size 2: two pointers, no padding entry; each extent holds 8 bytes of data.
        var bytes = Hex.Parse(
            "00000000 00000200 | 02000000 00000000 04000200 | 02000000 08000200 0c000200 | " +
            "08000000 78563412 bc9a f0de 1122334455667788 08000000 0102030405060708 | " +
            "08000000 78563412 bc9a f0de 1122334455667788 08000000 1112131415161718");

        var that = OrpcHeader.ReadThat(new NdrReader(bytes));

        Assert.Equal(2, that.Extensions.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, that.Extensions[0].Data);
        Assert.Equal(new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 }, that.Extensions[1].Data);
    }

    [Fact]
    public void Inconsistent_extension_sizes_are_rejected()
    {
        // Array size 1 needs 2 pointer entries; here it announces 3.
        var wrongCount = Hex.Parse(
            "00000000 00000200 | 01000000 00000000 04000200 | 03000000 08000200 00000000 00000000");
        Assert.Throws<NdrFormatException>(() => OrpcHeader.ReadThat(new NdrReader(wrongCount)));

        // Extent size 3 needs 8 bytes of data; here max_count says 16.
        var wrongData = Hex.Parse(
            "00000000 00000200 | 01000000 00000000 04000200 | 02000000 08000200 00000000 | " +
            "10000000 78563412 bc9a f0de 1122334455667788 03000000 0a0b0c00000000000000000000000000");
        Assert.Throws<NdrFormatException>(() => OrpcHeader.ReadThat(new NdrReader(wrongData)));

        // A huge declared size must not wrap around the padding computation.
        var hugeSize = Hex.Parse(
            "00000000 00000200 | 01000000 00000000 04000200 | 02000000 08000200 00000000 | " +
            "00000000 78563412 bc9a f0de 1122334455667788 f9ffffff");
        Assert.Throws<NdrFormatException>(() => OrpcHeader.ReadThat(new NdrReader(hugeSize)));
    }

    [Fact]
    public void Truncated_or_corrupt_ORPCTHAT_only_ever_throws_NdrFormatException()
    {
        var original = Hex.Parse(
            "01000000 00000200 | 01000000 00000000 04000200 | 02000000 08000200 00000000 | " +
            "08000000 78563412 bc9a f0de 1122334455667788 03000000 0a0b0c0000000000 | 11223344");
        var random = new Random(2024);
        for (var round = 0; round < 5000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                OrpcHeader.ReadThat(new NdrReader(mutated));
            }
            catch (NdrFormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }

    [Fact]
    public void The_default_version_is_5_7()
    {
        Assert.Equal("5.7", DcomConstants.ClientVersion.ToString());
    }
}

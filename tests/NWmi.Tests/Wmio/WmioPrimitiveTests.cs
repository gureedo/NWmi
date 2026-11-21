using NWmi.Wmio;

namespace NWmi.Tests.Wmio;

public class WmioPrimitiveTests
{
    private static byte[] Hex(string text) =>
        Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal).Replace("|", "", StringComparison.Ordinal));

    [Fact]
    public void Integers_are_packed_with_no_alignment()
    {
        var w = new WmioWriter();
        w.WriteByte(1);
        w.WriteUInt32(2);
        w.WriteUInt16(3);
        Assert.Equal(Hex("01 02000000 0300"), w.ToArray());

        var r = new WmioReader(w.ToArray());
        Assert.Equal((byte)1, r.ReadByte());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal((ushort)3, r.ReadUInt16());
        Assert.True(r.IsAtEnd);
    }

    [Fact]
    public void Signed_and_floating_point_round_trip()
    {
        var w = new WmioWriter();
        w.WriteSByte(-5);
        w.WriteInt16(-1000);
        w.WriteInt32(int.MinValue);
        w.WriteInt64(long.MinValue);
        w.WriteReal32(1.5f);
        w.WriteReal64(-2.25);

        var r = new WmioReader(w.ToArray());
        Assert.Equal((sbyte)-5, r.ReadSByte());
        Assert.Equal((short)-1000, r.ReadInt16());
        Assert.Equal(int.MinValue, r.ReadInt32());
        Assert.Equal(long.MinValue, r.ReadInt64());
        Assert.Equal(1.5f, r.ReadReal32());
        Assert.Equal(-2.25, r.ReadReal64());
    }

    [Theory]
    [InlineData(false, "0000")]
    [InlineData(true, "FFFF")]
    public void Bool_uses_all_bits_set_or_clear(bool value, string hex)
    {
        var w = new WmioWriter();
        w.WriteBool(value);
        Assert.Equal(Hex(hex), w.ToArray());
        Assert.Equal(value, new WmioReader(w.ToArray()).ReadBool());
    }

    [Fact]
    public void An_invalid_bool_value_is_rejected()
    {
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("0001")).ReadBool());
    }

    [Fact]
    public void HeapLength_sets_the_top_bit()
    {
        var w = new WmioWriter();
        w.WriteHeapLength(0x26);
        Assert.Equal(Hex("26000080"), w.ToArray());
        Assert.Equal(0x26, new WmioReader(w.ToArray()).ReadHeapLength());
    }

    [Fact]
    public void A_HeapLength_without_the_top_bit_is_rejected()
    {
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("26000000")).ReadHeapLength());
    }

    [Fact]
    public void Compressible_strings_use_one_octet_per_character()
    {
        var w = new WmioWriter();
        w.WriteEncodedString("Base");
        Assert.Equal(Hex("00 42617365 00"), w.ToArray());
        Assert.Equal(6, WmioWriter.EncodedStringLength("Base"));
        Assert.Equal("Base", new WmioReader(w.ToArray()).ReadEncodedString());
    }

    [Fact]
    public void An_empty_string_is_just_the_flag_and_terminator()
    {
        var w = new WmioWriter();
        w.WriteEncodedString("");
        Assert.Equal(Hex("0000"), w.ToArray());
        Assert.Equal("", new WmioReader(w.ToArray()).ReadEncodedString());
    }

    [Fact]
    public void Non_latin1_strings_use_utf16()
    {
        var w = new WmioWriter();
        w.WriteEncodedString("K中"); // contains a character above U+00FF
        Assert.Equal(Hex("01 4B00 2D4E 0000"), w.ToArray());
        Assert.Equal(7, WmioWriter.EncodedStringLength("K中"));
        Assert.Equal("K中", new WmioReader(w.ToArray()).ReadEncodedString());
    }

    [Fact]
    public void A_string_with_only_latin1_characters_above_ascii_is_still_compressed()
    {
        var w = new WmioWriter();
        w.WriteEncodedString("é"); // e-acute, U+00E9, within U+0000..U+00FF
        Assert.Equal(Hex("00 E9 00"), w.ToArray());
        Assert.Equal("é", new WmioReader(w.ToArray()).ReadEncodedString());
    }

    [Fact]
    public void An_unterminated_string_is_rejected()
    {
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("00 42617365")).ReadEncodedString());
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("01 4200")).ReadEncodedString());
    }

    [Fact]
    public void An_invalid_string_flag_is_rejected()
    {
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("02 00")).ReadEncodedString());
    }

    [Fact]
    public void Length_prefixed_blocks_are_written_and_read_back()
    {
        var w = new WmioWriter();
        w.WriteLengthPrefixedBlock(b =>
        {
            b.WriteByte(1);
            b.WriteByte(2);
        });
        Assert.Equal(Hex("06000000 0102"), w.ToArray());

        var outer = new WmioReader(w.ToArray());
        var block = outer.ReadLengthPrefixedBlock();
        Assert.Equal(6, block.Length);
        Assert.Equal(6u, block.ReadUInt32());
        Assert.Equal((byte)1, block.ReadByte());
        Assert.Equal((byte)2, block.ReadByte());
        Assert.True(outer.IsAtEnd);
    }

    [Fact]
    public void An_empty_length_prefixed_block_is_just_its_own_length()
    {
        var w = new WmioWriter();
        w.WriteLengthPrefixedBlock(_ => { });
        Assert.Equal(Hex("04000000"), w.ToArray());
    }

    [Fact]
    public void A_length_prefixed_block_claiming_more_than_available_is_rejected()
    {
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("FFFFFFFF 00")).ReadLengthPrefixedBlock());
        Assert.Throws<WmioFormatException>(() => new WmioReader(Hex("00000000")).ReadEncodingLength());
    }

    [Fact]
    public void Reader_rejects_reads_past_the_end_without_throwing_an_unrelated_exception_type()
    {
        Assert.Throws<WmioFormatException>(() => new WmioReader(new byte[2]).ReadUInt32());
        Assert.Throws<WmioFormatException>(() => new WmioReader(Array.Empty<byte>()).ReadByte());
    }

    [Fact]
    public void Expect_checks_a_literal_value()
    {
        var reader = new WmioReader(Hex("78563412"));
        reader.Expect(0x12345678, "Signature");

        var wrong = new WmioReader(Hex("00000000"));
        Assert.Throws<WmioFormatException>(() => wrong.Expect(0x12345678, "Signature"));
    }

    [Fact]
    public void Position_can_be_set_within_bounds_but_not_outside()
    {
        var reader = new WmioReader(new byte[10]);
        reader.Position = 5;
        Assert.Equal(5, reader.Position);
        Assert.Throws<WmioFormatException>(() => reader.Position = 11);
        Assert.Throws<WmioFormatException>(() => reader.Position = -1);
    }
}

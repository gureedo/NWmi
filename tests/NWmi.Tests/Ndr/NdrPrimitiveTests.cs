using NWmi.Ndr;

namespace NWmi.Tests.Ndr;

public class NdrPrimitiveTests
{
    [Fact]
    public void Primitives_are_aligned_to_their_size()
    {
        var w = new NdrWriter();
        w.WriteByte(1);
        w.WriteUInt32(2);
        Assert.Equal(Hex.Parse("01 000000 | 02000000"), w.ToArray());

        w = new NdrWriter();
        w.WriteByte(1);
        w.WriteUInt64(2);
        Assert.Equal(Hex.Parse("01 00000000000000 | 0200000000000000"), w.ToArray());

        w = new NdrWriter();
        w.WriteByte(1);
        w.WriteUInt16(0x0302);
        Assert.Equal(Hex.Parse("01 00 | 0203"), w.ToArray());
    }

    [Fact]
    public void Integers_are_little_endian_and_signed_values_wrap()
    {
        var w = new NdrWriter();
        w.WriteInt32(-2);
        w.WriteInt16(-1);
        Assert.Equal(Hex.Parse("FEFFFFFF | FFFF"), w.ToArray());
    }

    [Fact]
    public void Guid_uses_the_mixed_endian_wire_layout()
    {
        var iid = Guid.Parse("F309AD18-D86A-11d0-A075-00C04FB68820"); // IID_IWbemLevel1Login
        var w = new NdrWriter();
        w.WriteGuid(iid);
        Assert.Equal(Hex.Parse("18AD09F3 6AD8 D011 A075 00C04FB68820"), w.ToArray());
        Assert.Equal(iid, new NdrReader(w.ToArray()).ReadGuid());
    }

    [Fact]
    public void Enums_are_16_bit_unless_v1_enum()
    {
        var w = new NdrWriter();
        w.WriteEnum16(7);
        w.WriteEnum32(7);
        Assert.Equal(Hex.Parse("0700 0000 | 07000000"), w.ToArray());
    }

    [Fact]
    public void String_is_conformant_varying_with_terminator()
    {
        var w = new NdrWriter();
        w.WriteConformantVaryingString("ab");
        Assert.Equal(Hex.Parse("03000000 | 00000000 | 03000000 | 6100 6200 0000"), w.ToArray());
        Assert.Equal("ab", new NdrReader(w.ToArray()).ReadConformantVaryingString());
    }

    [Fact]
    public void Empty_string_is_just_the_terminator()
    {
        var w = new NdrWriter();
        w.WriteConformantVaryingString("");
        Assert.Equal(Hex.Parse("01000000 | 00000000 | 01000000 | 0000"), w.ToArray());
        Assert.Equal("", new NdrReader(w.ToArray()).ReadConformantVaryingString());
    }

    [Fact]
    public void String_with_surrogate_pairs_round_trips()
    {
        var w = new NdrWriter();
        w.WriteConformantVaryingString("a\U0001F600b");
        Assert.Equal("a\U0001F600b", new NdrReader(w.ToArray()).ReadConformantVaryingString());
    }

    [Fact]
    public void Conformant_bytes_have_max_count_first()
    {
        var w = new NdrWriter();
        w.WriteConformantBytes([1, 2, 3]);
        Assert.Equal(Hex.Parse("03000000 | 010203"), w.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, new NdrReader(w.ToArray()).ReadConformantBytes());
    }

    [Fact]
    public void All_primitives_round_trip()
    {
        var w = new NdrWriter();
        w.WriteBoolean(true);
        w.WriteInt16(-5);
        w.WriteUInt16(65000);
        w.WriteInt32(int.MinValue);
        w.WriteUInt32(uint.MaxValue);
        w.WriteInt64(long.MinValue);
        w.WriteUInt64(ulong.MaxValue);
        w.WriteSingle(1.5f);
        w.WriteDouble(-2.25);

        var r = new NdrReader(w.ToArray());
        Assert.True(r.ReadBoolean());
        Assert.Equal(-5, r.ReadInt16());
        Assert.Equal(65000, r.ReadUInt16());
        Assert.Equal(int.MinValue, r.ReadInt32());
        Assert.Equal(uint.MaxValue, r.ReadUInt32());
        Assert.Equal(long.MinValue, r.ReadInt64());
        Assert.Equal(ulong.MaxValue, r.ReadUInt64());
        Assert.Equal(1.5f, r.ReadSingle());
        Assert.Equal(-2.25, r.ReadDouble());
        Assert.True(r.IsAtEnd);
    }

    [Fact]
    public void Reader_rejects_truncated_input()
    {
        Assert.Throws<NdrFormatException>(() => new NdrReader(new byte[3]).ReadUInt32());
        Assert.Throws<NdrFormatException>(() => new NdrReader(Hex.Parse("01 00")).ReadUInt32());
    }

    [Fact]
    public void Reader_rejects_counts_larger_than_the_buffer_without_allocating()
    {
        Assert.Throws<NdrFormatException>(() => new NdrReader(Hex.Parse("F0FFFF7F 00")).ReadConformantBytes());
        Assert.Throws<NdrFormatException>(() => new NdrReader(Hex.Parse("FFFFFFFF 00")).ReadConformantBytes());
    }

    [Fact]
    public void Reader_rejects_inconsistent_string_bounds()
    {
        // max 2, offset 1, actual 2 runs past max.
        var bytes = Hex.Parse("02000000 | 01000000 | 02000000 | 6100 0000");
        Assert.Throws<NdrFormatException>(() => new NdrReader(bytes).ReadConformantVaryingString());
    }
}

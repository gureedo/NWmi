using NWmi.Dcom;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class TypeSerialization1Tests
{
    [Fact]
    public void Wrap_adds_both_headers_and_pads_to_eight_bytes()
    {
        var framed = TypeSerialization1.Wrap([1, 2, 3, 4, 5]);

        Assert.Equal(
            Hex.Parse("01 10 0800 cccccccc | 08000000 00000000 | 0102030405 000000"),
            framed);
    }

    [Fact]
    public void Data_that_is_already_aligned_gets_no_padding()
    {
        var framed = TypeSerialization1.Wrap(new byte[16]);
        Assert.Equal(32, framed.Length);
        Assert.Equal(16u, BitConverter.ToUInt32(framed, 8));
    }

    [Fact]
    public void Empty_data_is_just_the_headers()
    {
        var framed = TypeSerialization1.Wrap([]);
        Assert.Equal(Hex.Parse("01 10 0800 cccccccc | 00000000 00000000"), framed);
        Assert.Empty(TypeSerialization1.Unwrap(framed, out var consumed));
        Assert.Equal(16, consumed);
    }

    [Fact]
    public void Unwrap_returns_the_padded_data_and_reports_how_much_it_used()
    {
        var framed = TypeSerialization1.Wrap([9, 8, 7]).Concat(new byte[] { 0xAA, 0xBB }).ToArray();

        var data = TypeSerialization1.Unwrap(framed, out var consumed);

        Assert.Equal(Hex.Parse("0908070000000000"), data);
        Assert.Equal(24, consumed);
    }

    [Fact]
    public void Two_framed_values_can_be_read_one_after_the_other()
    {
        var both = TypeSerialization1.Wrap([1]).Concat(TypeSerialization1.Wrap([2, 2])).ToArray();

        var first = TypeSerialization1.Unwrap(both, out var used);
        var second = TypeSerialization1.Unwrap(both.AsSpan(used), out _);

        Assert.Equal(1, first[0]);
        Assert.Equal(2, second[0]);
    }

    [Theory]
    [InlineData("01 10 0800")] // too short
    [InlineData("02 10 0800 cccccccc 00000000 00000000")] // wrong version
    [InlineData("01 00 0800 cccccccc 00000000 00000000")] // big-endian
    [InlineData("01 10 1000 cccccccc 00000000 00000000")] // wrong common header length
    [InlineData("01 10 0800 cccccccc 07000000 00000000 0000000000000000")] // length not a multiple of 8
    [InlineData("01 10 0800 cccccccc 10000000 00000000 0000000000000000")] // longer than the data
    [InlineData("01 10 0800 cccccccc ffffffff 00000000")] // absurd length
    public void Invalid_framing_is_rejected(string hex)
    {
        Assert.Throws<FormatException>(() => TypeSerialization1.Unwrap(Hex.Parse(hex), out _));
    }
}

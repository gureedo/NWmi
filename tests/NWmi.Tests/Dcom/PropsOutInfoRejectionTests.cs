using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;

namespace NWmi.Tests.Dcom;

public class PropsOutInfoRejectionTests
{
    private static byte[] Valid() =>
        new PropsOutInfo([new InterfaceResult(DcomIds.IUnknown, 0, [1, 2, 3, 4])]).Encode();

    [Fact]
    public void A_zero_or_absurd_interface_count_is_rejected()
    {
        var zero = Valid();
        zero[0] = 0;
        Assert.Throws<NdrFormatException>(() => PropsOutInfo.Decode(zero));

        var huge = Valid();
        BitConverter.TryWriteBytes(huge.AsSpan(0), 0xFFFFFFFFu);
        Assert.Throws<NdrFormatException>(() => PropsOutInfo.Decode(huge));
    }

    [Fact]
    public void A_count_that_disagrees_with_the_arrays_is_rejected()
    {
        var data = Valid();
        data[0] = 2; // cIfs says 2, the arrays hold 1
        Assert.Throws<NdrFormatException>(() => PropsOutInfo.Decode(data));
    }

    [Fact]
    public void A_missing_array_is_rejected()
    {
        var data = Valid();
        data[4] = data[5] = data[6] = data[7] = 0; // piid pointer
        Assert.Throws<NdrFormatException>(() => PropsOutInfo.Decode(data));
    }

    [Fact]
    public void A_success_without_an_object_reference_is_rejected()
    {
        var data = new PropsOutInfo([new InterfaceResult(DcomIds.IUnknown, 0, null)]).Encode();
        Assert.Throws<NdrFormatException>(() => PropsOutInfo.Decode(data));
    }

    [Fact]
    public void Corrupt_data_only_ever_throws_NdrFormatException()
    {
        var original = new PropsOutInfo(
        [
            new InterfaceResult(DcomIds.IUnknown, 0, [1, 2, 3, 4]),
            new InterfaceResult(DcomIds.IActivationPropertiesOut, unchecked((int)0x80004002), null),
        ]).Encode();

        var random = new Random(909);
        for (var round = 0; round < 5000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                PropsOutInfo.Decode(mutated);
            }
            catch (NdrFormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}

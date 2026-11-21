using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class InstantiationInfoDataTests
{
    private static readonly Guid ClassId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Login = Guid.Parse("f309ad18-d86a-11d0-a075-00c04fb68820"); // IWbemLevel1Login
    private static readonly Guid Services = Guid.Parse("9556dc99-828c-11cf-a37e-00aa003240c7"); // IWbemServices

    [Fact]
    public void One_interface_has_the_documented_layout()
    {
        var info = new InstantiationInfoData(ClassId, [Login]);

        var expected = Hex.Parse(
            "11111111 2222 3333 4444 555555555555 | " + // classId
            "10000000 | 00000000 | 00000000 | 01000000 | 00000000 | " + // classCtx, actvflags, fIsSurrogate, cIID, instFlag
            "00000200 | 58000000 | 0500 0700 | " + // pIID referent, thisSize = 88, client version 5.7
            "01000000 | 18ad09f3 6ad8 d011 a075 00c04fb68820"); // pIID: max_count, the IID

        Assert.Equal(expected, info.Encode());
    }

    [Fact]
    public void ThisSize_is_the_size_of_the_framed_structure()
    {
        var info = new InstantiationInfoData(ClassId, [Login, Services]);
        var encoded = info.Encode();

        var framed = TypeSerialization1.Wrap(encoded);
        Assert.Equal((uint)framed.Length, BitConverter.ToUInt32(encoded, 40));
    }

    [Fact]
    public void Several_interfaces_and_flags_round_trip()
    {
        var info = new InstantiationInfoData(ClassId, [Login, Services, DcomIds.IUnknown], activationFlags: 0x20)
        {
            ClientVersion = new Version(5, 6),
        };

        var decoded = InstantiationInfoData.Decode(info.Encode());

        Assert.Equal(ClassId, decoded.ClassId);
        Assert.Equal(info.RequestedInterfaces, decoded.RequestedInterfaces);
        Assert.Equal(0x20u, decoded.ActivationFlags);
        Assert.Equal(new Version(5, 6), decoded.ClientVersion);
    }

    [Fact]
    public void The_number_of_requested_interfaces_is_limited()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstantiationInfoData(ClassId, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InstantiationInfoData(ClassId, new Guid[InstantiationInfoData.MaxRequestedInterfaces + 1]));
    }

    [Fact]
    public void Decode_rejects_a_mismatched_or_absurd_interface_count()
    {
        var encoded = new InstantiationInfoData(ClassId, [Login]).Encode();

        var mismatched = (byte[])encoded.Clone();
        mismatched[28] = 3; // cIID, while only one IID follows
        Assert.Throws<NdrFormatException>(() => InstantiationInfoData.Decode(mismatched));

        var zero = (byte[])encoded.Clone();
        zero[28] = 0;
        Assert.Throws<NdrFormatException>(() => InstantiationInfoData.Decode(zero));

        var huge = (byte[])encoded.Clone();
        BitConverter.TryWriteBytes(huge.AsSpan(28), 0xFFFFFFFFu);
        Assert.Throws<NdrFormatException>(() => InstantiationInfoData.Decode(huge));
    }

    [Fact]
    public void A_null_interface_pointer_is_rejected()
    {
        var encoded = new InstantiationInfoData(ClassId, [Login]).Encode();
        encoded[36] = encoded[37] = encoded[38] = encoded[39] = 0; // pIID
        Assert.Throws<NdrFormatException>(() => InstantiationInfoData.Decode(encoded));
    }

    [Fact]
    public void Corrupt_data_only_ever_throws_NdrFormatException()
    {
        var original = new InstantiationInfoData(ClassId, [Login, Services]).Encode();
        var random = new Random(77);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                InstantiationInfoData.Decode(mutated);
            }
            catch (NdrFormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}

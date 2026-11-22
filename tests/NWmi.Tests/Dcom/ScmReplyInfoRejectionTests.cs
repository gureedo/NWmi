using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Options;

namespace NWmi.Tests.Dcom;

public class ScmReplyInfoRejectionTests
{
    private static byte[] Valid() => new ScmReplyInfoData(
        1,
        new DualStringArray([new StringBinding(7, "a")], []),
        Guid.NewGuid(),
        RpcAuthLevel.Connect,
        new Version(5, 7)).Encode();

    [Fact]
    public void A_missing_reply_structure_is_rejected()
    {
        var data = Valid();
        data[4] = data[5] = data[6] = data[7] = 0; // remoteReply pointer
        Assert.Throws<NdrFormatException>(() => ScmReplyInfoData.Decode(data));
    }

    [Fact]
    public void Missing_bindings_are_rejected()
    {
        var data = Valid();
        data[16] = data[17] = data[18] = data[19] = 0; // pdsaOxidBindings pointer
        Assert.Throws<NdrFormatException>(() => ScmReplyInfoData.Decode(data));
    }

    [Fact]
    public void Corrupt_data_only_ever_throws_NdrFormatException()
    {
        var original = Valid();
        var random = new Random(12);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                ScmReplyInfoData.Decode(mutated);
            }
            catch (NdrFormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}

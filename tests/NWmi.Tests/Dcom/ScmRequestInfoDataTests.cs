using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class ScmRequestInfoDataTests
{
    [Fact]
    public void One_protocol_sequence_has_the_documented_layout()
    {
        var info = new ScmRequestInfoData([DualStringArray.TcpTowerId]);

        var expected = Hex.Parse(
            "00000000 | 00000200 | " + // pdwReserved (null), remoteRequest
            "02000000 | 0100 0000 | 04000200 | " + // ClientImpLevel, cRequestedProtseqs + padding, pRequestedProtseqs
            "01000000 | 0700"); // the array: max_count, TCP

        Assert.Equal(expected, info.Encode());
    }

    [Fact]
    public void Several_protocol_sequences_round_trip()
    {
        var info = new ScmRequestInfoData([7, 15, 16]);
        var decoded = ScmRequestInfoData.Decode(info.Encode());
        Assert.Equal([7, 15, 16], decoded.RequestedProtocolSequences);
    }

    [Fact]
    public void An_empty_list_is_allowed_and_round_trips()
    {
        var decoded = ScmRequestInfoData.Decode(new ScmRequestInfoData([]).Encode());
        Assert.Empty(decoded.RequestedProtocolSequences);
    }

    [Fact]
    public void The_number_of_protocol_sequences_is_limited()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScmRequestInfoData(new ushort[ScmRequestInfoData.MaxRequestedProtocolSequences + 1]));
    }

    [Fact]
    public void A_missing_request_structure_is_rejected()
    {
        var encoded = new ScmRequestInfoData([7]).Encode();
        encoded[4] = encoded[5] = encoded[6] = encoded[7] = 0; // remoteRequest pointer
        Assert.Throws<NdrFormatException>(() => ScmRequestInfoData.Decode(encoded));
    }

    [Fact]
    public void A_count_that_disagrees_with_the_array_is_rejected()
    {
        var encoded = new ScmRequestInfoData([7]).Encode();
        encoded[12] = 3; // cRequestedProtseqs
        Assert.Throws<NdrFormatException>(() => ScmRequestInfoData.Decode(encoded));
    }

    [Fact]
    public void Corrupt_data_only_ever_throws_NdrFormatException()
    {
        var original = new ScmRequestInfoData([7, 15]).Encode();
        var random = new Random(8);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                ScmRequestInfoData.Decode(mutated);
            }
            catch (NdrFormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}

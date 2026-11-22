using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Options;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class ResolveOxid2Tests
{
    private static readonly Guid Ipid = Guid.Parse("12345678-9abc-def0-1122-334455667788");

    [Fact]
    public void The_request_has_the_documented_layout()
    {
        var request = ObjectExporter.BuildResolveOxid2Request(0x1122334455667788, [DualStringArray.TcpTowerId]);

        // OXID, number of protocol sequences, two bytes of padding, the array's max_count, the sequence.
        Assert.Equal(Hex.Parse("8877665544332211 | 0100 0000 | 01000000 | 0700"), request);
    }

    [Fact]
    public void Several_protocol_sequences_are_sent_as_one_array()
    {
        var request = ObjectExporter.BuildResolveOxid2Request(1, [7, 15]);
        Assert.Equal(Hex.Parse("0100000000000000 | 0200 0000 | 02000000 | 0700 0f00"), request);
    }

    private static byte[] Reply(uint status)
    {
        var bindings = new DualStringArray([new StringBinding(7, "1.2.3.4[135]")], []);
        var (entries, securityOffset) = bindings.ToEntries();

        var writer = new NdrWriter();
        writer.WriteUInt32(NdrWriter.FirstReferentId); // pointer to the bindings
        writer.WriteMaxCount(entries.Length);
        writer.WriteUInt16((ushort)entries.Length);
        writer.WriteUInt16(securityOffset);
        foreach (var entry in entries)
            writer.WriteUInt16(entry);

        writer.WriteGuid(Ipid); // aligns to 4 first
        writer.WriteUInt32(2); // authentication hint
        writer.WriteUInt16(5);
        writer.WriteUInt16(7);
        writer.WriteUInt32(status);
        return writer.ToArray();
    }

    [Fact]
    public void The_reply_is_decoded()
    {
        var result = ObjectExporter.ParseResolveOxid2Reply(Reply(0));

        Assert.Equal("1.2.3.4[135]", Assert.Single(result.Bindings.StringBindings).NetworkAddress);
        Assert.Equal(Ipid, result.RemUnknownIpid);
        Assert.Equal(RpcAuthLevel.Connect, result.AuthnHint);
        Assert.Equal(new Version(5, 7), result.Version);
    }

    [Fact]
    public void An_error_status_is_raised()
    {
        var ex = Assert.Throws<DcomException>(() => ObjectExporter.ParseResolveOxid2Reply(Reply(0x80070057)));
        Assert.Equal(unchecked((int)0x80070057u), ex.HResult);
    }

    [Fact]
    public void A_null_binding_pointer_is_rejected()
    {
        var reply = Reply(0);
        reply[0] = reply[1] = reply[2] = reply[3] = 0;
        Assert.Throws<NdrFormatException>(() => ObjectExporter.ParseResolveOxid2Reply(reply));
    }

    [Fact]
    public void Corrupt_replies_only_ever_throw_NdrFormatException_or_DcomException()
    {
        var original = Reply(0);
        var random = new Random(555);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                ObjectExporter.ParseResolveOxid2Reply(mutated);
            }
            catch (Exception ex) when (ex is NdrFormatException or DcomException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}

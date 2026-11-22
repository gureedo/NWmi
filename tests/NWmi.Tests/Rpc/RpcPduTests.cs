using NWmi.Options;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Rpc;

public class RpcPduTests
{
    private static readonly RpcSyntaxId ExporterInterface = RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a");

    [Fact]
    public void Bind_pdu_bytes()
    {
        var bind = new RpcBindPdu
        {
            CallId = 1,
            MaxXmitFrag = 5840,
            MaxRecvFrag = 5840,
            Contexts = [new RpcContextElement(0, ExporterInterface, [RpcSyntaxId.Ndr20])],
        };

        Assert.Equal(
            Hex.Parse(
                "05 00 0B 03 10000000 | 4800 0000 | 01000000 | " +
                "D016 D016 | 00000000 | 01 00 0000 | " +
                "0000 01 00 | C4FEFC99 6052 1B10 BB CB 00AA0021347A | 00000000 | " +
                "045D888A EB1C C911 9F E8 08002B104860 | 02000000"),
            bind.Serialize());
    }

    [Fact]
    public void Bind_pdu_round_trips()
    {
        var bind = new RpcBindPdu
        {
            CallId = 7,
            MaxXmitFrag = 4280,
            MaxRecvFrag = 4280,
            AssocGroupId = 99,
            Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment | RpcPfcFlags.PendingCancelOrSupportHeaderSign,
            Contexts =
            [
                new RpcContextElement(0, ExporterInterface, [RpcSyntaxId.Ndr20]),
                new RpcContextElement(1, RpcSyntaxId.Parse("00000143-0000-0000-c000-000000000046", 1, 2), [RpcSyntaxId.Ndr20]),
            ],
        };

        var parsed = Assert.IsType<RpcBindPdu>(RpcPdu.Parse(bind.Serialize()));
        Assert.Equal(RpcPduType.Bind, parsed.Type);
        Assert.Equal(7u, parsed.CallId);
        Assert.Equal(bind.Flags, parsed.Flags);
        Assert.Equal(4280, parsed.MaxXmitFrag);
        Assert.Equal(99u, parsed.AssocGroupId);
        Assert.Equal(2, parsed.Contexts.Count);
        Assert.Equal(bind.Contexts[1].AbstractSyntax, parsed.Contexts[1].AbstractSyntax);
        Assert.Equal((ushort)1, parsed.Contexts[1].ContextId);
        Assert.Equal(RpcSyntaxId.Ndr20, Assert.Single(parsed.Contexts[1].TransferSyntaxes));
    }

    [Fact]
    public void Alter_context_uses_the_same_layout_with_its_own_type()
    {
        var alter = new RpcBindPdu
        {
            PduType = RpcPduType.AlterContext,
            CallId = 2,
            MaxXmitFrag = 1000,
            MaxRecvFrag = 1000,
            Contexts = [new RpcContextElement(3, ExporterInterface, [RpcSyntaxId.Ndr20])],
        };

        var bytes = alter.Serialize();
        Assert.Equal((byte)RpcPduType.AlterContext, bytes[2]);
        Assert.Equal(RpcPduType.AlterContext, RpcPdu.Parse(bytes).Type);
    }

    [Fact]
    public void Bind_ack_pdu_bytes_and_parse()
    {
        var bytes = Hex.Parse(
            "05 00 0C 03 10000000 | 3C00 0000 | 01000000 | " +
            "B810 B810 | 934C0000 | 0400 3133 3500 | 0000 | " +
            "01 00 0000 | 0000 0000 | 045D888A EB1C C911 9F E8 08002B104860 | 02000000");

        var ack = Assert.IsType<RpcBindAckPdu>(RpcPdu.Parse(bytes));
        Assert.Equal(4280, ack.MaxXmitFrag);
        Assert.Equal(4280, ack.MaxRecvFrag);
        Assert.Equal(0x4C93u, ack.AssocGroupId);
        Assert.Equal("135", ack.SecondaryAddress);
        var result = Assert.Single(ack.Results);
        Assert.Equal(RpcContextResult.Acceptance, result.Result);
        Assert.Equal(RpcSyntaxId.Ndr20, result.TransferSyntax);

        var again = new RpcBindAckPdu
        {
            CallId = 1,
            MaxXmitFrag = 4280,
            MaxRecvFrag = 4280,
            AssocGroupId = 0x4C93,
            SecondaryAddress = "135",
            Results = [new RpcContextResultElement(RpcContextResult.Acceptance, 0, RpcSyntaxId.Ndr20)],
        };
        Assert.Equal(bytes, again.Serialize());
    }

    [Fact]
    public void Bind_ack_without_secondary_address_is_padded()
    {
        var ack = new RpcBindAckPdu
        {
            CallId = 1,
            MaxXmitFrag = 1,
            MaxRecvFrag = 1,
            Results = [new RpcContextResultElement(RpcContextResult.ProviderRejection, 2, default)],
        };

        var bytes = ack.Serialize();
        // 16 header + 8 + sec_addr length (2) + 2 pad, then the result list starts on a 4-byte boundary.
        Assert.Equal(16 + 8 + 4 + 4 + 24, bytes.Length);
        var parsed = Assert.IsType<RpcBindAckPdu>(RpcPdu.Parse(bytes));
        Assert.Equal("", parsed.SecondaryAddress);
        Assert.Equal(RpcContextResult.ProviderRejection, parsed.Results[0].Result);
        Assert.Equal((ushort)2, parsed.Results[0].Reason);
    }

    [Fact]
    public void Bind_nak_bytes_and_parse()
    {
        var bytes = Hex.Parse("05 00 0D 03 10000000 | 1500 0000 | 01000000 | 0400 | 01 | 0500");
        var nak = Assert.IsType<RpcBindNakPdu>(RpcPdu.Parse(bytes));
        Assert.Equal(RpcBindRejectReason.ProtocolVersionNotSupported, nak.Reason);
        Assert.Equal((byte)5, Assert.Single(nak.SupportedVersions).Major);
        Assert.Equal(bytes, nak.Serialize());
    }

    [Fact]
    public void Request_with_object_uuid_bytes()
    {
        var request = new RpcRequestPdu
        {
            CallId = 3,
            AllocHint = 8,
            ContextId = 0,
            OpNum = 5,
            ObjectUuid = Guid.Parse("12345678-9abc-def0-1122-334455667788"),
            Stub = [1, 2, 3, 4, 5, 6, 7, 8],
        };

        var bytes = request.Serialize();
        Assert.Equal(
            Hex.Parse(
                "05 00 00 83 10000000 | 3000 0000 | 03000000 | " +
                "08000000 | 0000 0500 | 78563412 BC9A F0DE 1122334455667788 | 0102030405060708"),
            bytes);

        var parsed = Assert.IsType<RpcRequestPdu>(RpcPdu.Parse(bytes));
        Assert.Equal(request.ObjectUuid, parsed.ObjectUuid);
        Assert.Equal((ushort)5, parsed.OpNum);
        Assert.Equal(request.Stub, parsed.Stub);
        Assert.Equal(RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment | RpcPfcFlags.ObjectUuid, parsed.Flags);
    }

    [Fact]
    public void Request_without_object_uuid_clears_the_flag()
    {
        var request = new RpcRequestPdu { CallId = 1, Flags = RpcPfcFlags.ObjectUuid | RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment, Stub = [9] };
        var bytes = request.Serialize();
        Assert.Equal(0x03, bytes[3]);
        Assert.Null(Assert.IsType<RpcRequestPdu>(RpcPdu.Parse(bytes)).ObjectUuid);
    }

    [Fact]
    public void Response_bytes()
    {
        var response = new RpcResponsePdu { CallId = 3, AllocHint = 2, Stub = [0xAA, 0xBB] };
        var bytes = response.Serialize();
        Assert.Equal(Hex.Parse("05 00 02 03 10000000 | 1A00 0000 | 03000000 | 02000000 | 0000 00 00 | AABB"), bytes);

        var parsed = Assert.IsType<RpcResponsePdu>(RpcPdu.Parse(bytes));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, parsed.Stub);
    }

    [Fact]
    public void Fault_bytes_and_status()
    {
        var fault = new RpcFaultPdu { CallId = 7, Status = 5 };
        var bytes = fault.Serialize();
        Assert.Equal(
            Hex.Parse("05 00 03 03 10000000 | 2000 0000 | 07000000 | 20000000 | 0000 00 00 | 05000000 | 00000000"),
            bytes);
        Assert.Equal(5u, Assert.IsType<RpcFaultPdu>(RpcPdu.Parse(bytes)).Status);
    }

    [Fact]
    public void Fault_keeps_extended_error_info_only_when_flagged()
    {
        var withInfo = new RpcFaultPdu { CallId = 1, Status = 0x6BE, ExtendedErrorInfo = [1, 2, 3] };
        var bytes = withInfo.Serialize();
        Assert.Equal(1, bytes[23]); // low bit of the reserved byte
        var parsed = Assert.IsType<RpcFaultPdu>(RpcPdu.Parse(bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.ExtendedErrorInfo);
        Assert.Equal(0x20u + 3, parsed.AllocHint);

        // Stub data after the status is ignored when the flag is clear.
        var plain = new RpcFaultPdu { CallId = 1, Status = 0x6BE }.Serialize().Concat(new byte[] { 9, 9, 9, 9 }).ToArray();
        plain[8] = (byte)plain.Length;
        Assert.Null(Assert.IsType<RpcFaultPdu>(RpcPdu.Parse(plain)).ExtendedErrorInfo);
    }

    [Fact]
    public void Authenticated_request_pads_the_stub_to_a_multiple_of_16_bytes_before_the_trailer()
    {
        var request = new RpcRequestPdu
        {
            CallId = 1,
            AllocHint = 5,
            OpNum = 1,
            Stub = [1, 2, 3, 4, 5],
            Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.PacketPrivacy, 1, [0xAA, 0xBB, 0xCC, 0xDD]),
        };

        var bytes = request.Serialize();
        Assert.Equal(
            Hex.Parse(
                "05 00 00 03 10000000 | 3400 0400 | 01000000 | 05000000 | 0000 0100 | 0102030405 | 00000000 00000000 000000 | " +
                "0A 06 0B 00 | 01000000 | AABBCCDD"),
            bytes);

        var parsed = Assert.IsType<RpcRequestPdu>(RpcPdu.Parse(bytes));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, parsed.Stub);
        Assert.NotNull(parsed.Auth);
        Assert.Equal(RpcAuthType.WinNt, parsed.Auth.AuthType);
        Assert.Equal(RpcAuthLevel.PacketPrivacy, parsed.Auth.AuthLevel);
        Assert.Equal(11, parsed.Auth.PadLength);
        Assert.Equal(1u, parsed.Auth.ContextId);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, parsed.Auth.Value);
    }

    [Fact]
    public void Aligned_stub_needs_no_padding()
    {
        var request = new RpcRequestPdu { CallId = 1, Stub = new byte[16], Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.Connect, 0, [1]) };
        var parsed = Assert.IsType<RpcRequestPdu>(RpcPdu.Parse(request.Serialize()));
        Assert.Equal(0, parsed.Auth!.PadLength);
        Assert.Equal(16, parsed.Stub.Length);
    }

    [Fact]
    public void Bind_with_authentication_round_trips()
    {
        var bind = new RpcBindPdu
        {
            CallId = 1,
            MaxXmitFrag = 5840,
            MaxRecvFrag = 5840,
            Contexts = [new RpcContextElement(0, ExporterInterface, [RpcSyntaxId.Ndr20])],
            Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.PacketPrivacy, 0, new byte[40]),
        };

        var bytes = bind.Serialize();
        Assert.Equal(0, bytes.Length % 8);
        var parsed = Assert.IsType<RpcBindPdu>(RpcPdu.Parse(bytes));
        Assert.Single(parsed.Contexts);
        Assert.Equal(40, parsed.Auth!.Value.Length);
    }

    [Fact]
    public void Auth3_carries_only_the_trailer()
    {
        var auth3 = new RpcAuth3Pdu { CallId = 4, Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.PacketPrivacy, 0, [1, 2, 3]) };
        var parsed = Assert.IsType<RpcAuth3Pdu>(RpcPdu.Parse(auth3.Serialize()));
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Auth!.Value);
    }

    [Fact]
    public void Unknown_pdu_types_are_kept_as_raw_bodies()
    {
        var bytes = Hex.Parse("05 00 11 03 10000000 | 1400 0000 | 09000000 | AABBCCDD");
        var other = Assert.IsType<RpcOtherPdu>(RpcPdu.Parse(bytes));
        Assert.Equal(RpcPduType.Shutdown, other.Type);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, other.Body);
    }

    [Theory]
    [InlineData("05 00 02 03 00000000 | 1400 0000 | 01000000 | 00000000", "data representation")] // big-endian
    [InlineData("05 00 02 03 10010000 | 1400 0000 | 01000000 | 00000000", "data representation")] // not IEEE
    [InlineData("06 00 02 03 10000000 | 1400 0000 | 01000000 | 00000000", "version")]
    [InlineData("05 01 02 03 10000000 | 1400 0000 | 01000000 | 00000000", "version")]
    [InlineData("05 00 02 03 10000000 | 1500 0000 | 01000000 | 00000000", "frag_length")]
    [InlineData("05 00 02 03 10000000 | 1400 0900 | 01000000 | 00000000", "auth_length")]
    public void Malformed_headers_are_rejected(string hex, string expectedFragment)
    {
        var ex = Assert.Throws<RpcProtocolException>(() => RpcPdu.Parse(Hex.Parse(hex)));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_pdus_are_rejected()
    {
        Assert.Throws<RpcProtocolException>(() => RpcPdu.Parse(Hex.Parse("05 00 02 03")));

        // A response whose fixed fields are cut short (frag_length matches, body does not).
        Assert.Throws<RpcProtocolException>(() => RpcPdu.Parse(Hex.Parse("05 00 02 03 10000000 | 1200 0000 | 01000000 | 0000")));
    }

    [Fact]
    public void A_pad_length_that_does_not_fit_is_rejected()
    {
        var bytes = new RpcRequestPdu { CallId = 1, Stub = [1], Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.Connect, 0, [1, 2]) }.Serialize();
        bytes[bytes.Length - (2 + 8) + 2] = 200; // auth_pad_length
        Assert.Throws<RpcProtocolException>(() => RpcPdu.Parse(bytes));
    }

    [Fact]
    public void Serialize_rejects_oversized_pdus()
    {
        var request = new RpcRequestPdu { CallId = 1, Stub = new byte[70_000] };
        Assert.Throws<RpcProtocolException>(request.Serialize);
    }

    [Fact]
    public void Corrupt_pdus_only_ever_throw_RpcProtocolException()
    {
        var samples = new RpcPdu[]
        {
            new RpcBindPdu
            {
                CallId = 1,
                MaxXmitFrag = 5840,
                MaxRecvFrag = 5840,
                Contexts = [new RpcContextElement(0, ExporterInterface, [RpcSyntaxId.Ndr20])],
                Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.PacketPrivacy, 0, new byte[24]),
            },
            new RpcBindAckPdu
            {
                CallId = 1,
                SecondaryAddress = "135",
                Results = [new RpcContextResultElement(RpcContextResult.Acceptance, 0, RpcSyntaxId.Ndr20)],
            },
            new RpcBindNakPdu { CallId = 1, Reason = RpcBindRejectReason.TemporaryCongestion, SupportedVersions = [(5, 0)] },
            new RpcRequestPdu { CallId = 1, ObjectUuid = Guid.NewGuid(), Stub = new byte[40], Auth = new RpcAuthTrailer(RpcAuthType.WinNt, RpcAuthLevel.PacketPrivacy, 0, new byte[16]) },
            new RpcResponsePdu { CallId = 1, Stub = new byte[33] },
            new RpcFaultPdu { CallId = 1, Status = 5, ExtendedErrorInfo = new byte[10] },
        };

        var random = new Random(777);
        foreach (var sample in samples)
        {
            var original = sample.Serialize();
            RpcPdu.Parse(original);
            for (var round = 0; round < 3000; round++)
            {
                var mutated = (byte[])original.Clone();
                for (var i = random.Next(1, 4); i > 0; i--)
                    mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

                if (random.Next(5) == 0)
                    mutated = mutated[..random.Next(mutated.Length)];

                try
                {
                    RpcPdu.Parse(mutated);
                }
                catch (RpcProtocolException)
                {
                    // Expected for corrupt input; any other exception type fails the test.
                }
            }
        }
    }
}

using System.Formats.Asn1;
using System.Text;
using NWmi.Auth;
using NWmi.Options;
using NWmi.Rpc;

namespace NWmi.Tests.Auth;

public class SpnegoTests
{
    private const string NtlmOid = "1.3.6.1.4.1.311.2.2.10";
    private const string KerberosOid = "1.2.840.113554.1.2.2";
    private static readonly Credential Alice = new("alice", "Passw0rd!", "CORP");

    /// <summary>A mechanism that hands out fixed tokens, so the SPNEGO framing can be checked on its own.</summary>
    private sealed class StubMechanism(string oid, byte[] initialToken, bool completeUpFront = false)
        : ISpnegoMechanism
    {
        public byte[]? LastServerToken { get; private set; }

        public RpcAuthType AuthType => RpcAuthType.WinNt;

        public string MechanismOid => oid;

        public int SignatureSize => oid == KerberosOid ? 60 : 16;

        public bool IsComplete { get; private set; } = completeUpFront;

        public bool SupportsMechListMic => false;

        public ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default) => new(initialToken);

        public ValueTask<byte[]> GetInitialMechTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default) => new(initialToken);

        public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token, CancellationToken cancellationToken = default) =>
            new(ProcessMechToken(token.Span));

        public byte[]? ProcessMechToken(ReadOnlySpan<byte> token)
        {
            LastServerToken = [.. token];
            IsComplete = true;
            return [.. "CD"u8];
        }

        public byte[] CreateMechListMic(ReadOnlySpan<byte> mechTypes) => throw new NotSupportedException();

        public void VerifyMechListMic(ReadOnlySpan<byte> mechTypes, ReadOnlySpan<byte> mic) => throw new NotSupportedException();

        public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, Span<byte> signature)
        {
        }

        public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature)
        {
        }

        public IRpcSecurityProvider CreateForNewConnection() => new StubMechanism(oid, initialToken);

        ISpnegoMechanism ISpnegoMechanism.CreateForNewConnection() => new StubMechanism(oid, initialToken);
    }

    private static SpnegoSecurityProvider Spnego(params ISpnegoMechanism[] mechanisms) => new(mechanisms, null);

    private static byte[] ServerResponse(int? negState, string? mechOid, byte[]? responseToken, byte[]? mechListMic = null)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
        using (writer.PushSequence())
        {
            if (negState is { } state)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    writer.WriteEnumeratedValue((SpnegoSecurityProvider.NegState)state);

            if (mechOid is not null)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                    writer.WriteObjectIdentifier(mechOid);

            if (responseToken is not null)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    writer.WriteOctetString(responseToken);

            if (mechListMic is not null)
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                    writer.WriteOctetString(mechListMic);
        }

        return writer.Encode();
    }

    /// <summary>Pulls the mechTypes (as DER bytes), their OIDs and the mechToken out of a NegTokenInit.</summary>
    private static (byte[] MechTypes, string[] Oids, byte[] MechToken) ParseInit(byte[] initialContextToken)
    {
        var reader = new AsnReader(initialContextToken, AsnEncodingRules.DER);
        var app = reader.ReadSequence(new Asn1Tag(TagClass.Application, 0, isConstructed: true));
        Assert.Equal("1.3.6.1.5.5.2", app.ReadObjectIdentifier());
        var init = app.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        var typesField = init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var mechTypes = typesField.PeekEncodedValue().ToArray();
        var list = typesField.ReadSequence();
        var oids = new List<string>();
        while (list.HasData)
            oids.Add(list.ReadObjectIdentifier());

        return (mechTypes, [.. oids], init.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadOctetString());
    }

    /// <summary>Reads our NegTokenResp: its responseToken and mechListMIC, either of which may be absent.</summary>
    private static (byte[]? Token, byte[]? Mic) ParseResponse(byte[] negTokenResp)
    {
        var reader = new AsnReader(negTokenResp, AsnEncodingRules.DER);
        var seq = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadSequence();
        byte[]? token = null;
        byte[]? mic = null;
        while (seq.HasData)
        {
            var tag = seq.PeekTag();
            var field = seq.ReadSequence(tag);
            switch (tag.TagValue)
            {
                case 2:
                    token = field.ReadOctetString();
                    break;
                case 3:
                    mic = field.ReadOctetString();
                    break;
                default:
                    Assert.Fail($"unexpected field [{tag.TagValue}]");
                    break;
            }
        }

        return (token, mic);
    }

    [Fact]
    public async Task Initial_token_lists_every_mechanism_and_carries_the_first_ones_token()
    {
        var spnego = Spnego(new StubMechanism(KerberosOid, [0x41, 0x42]), new StubMechanism(NtlmOid, [0x99]));

        var (_, oids, mechToken) = ParseInit(await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken));

        Assert.Equal([KerberosOid, NtlmOid], oids);
        Assert.Equal(new byte[] { 0x41, 0x42 }, mechToken);
    }

    [Fact]
    public async Task A_single_ntlm_offer_has_the_expected_bytes()
    {
        var token = await Spnego(new StubMechanism(NtlmOid, [0x41, 0x42])).GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        Assert.Equal(
            "6022" + "0606" + "2b06010505" + "02" + "a018" + "3016" + "a00e" + "300c" + "060a2b06010401823702020a" + "a204" + "0402" + "4142",
            Convert.ToHexString(token).ToLowerInvariant());
    }

    [Fact]
    public async Task The_optimistic_mechanism_being_accepted_completes_with_one_server_token()
    {
        var first = new StubMechanism(KerberosOid, [1]);
        var spnego = Spnego(first, new StubMechanism(NtlmOid, [2]));
        await spnego.GetInitialTokenAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken);

        var reply = await spnego.ProcessServerTokenAsync(ServerResponse(0, KerberosOid, [0x10, 0x20, 0x30]), TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, first.LastServerToken);
        Assert.Equal(new byte[] { 0x43, 0x44 }, ParseResponse(reply!).Token);
        Assert.True(spnego.IsComplete);
    }

    [Fact]
    public async Task A_completion_notice_without_a_token_needs_no_reply()
    {
        // Like Kerberos, which has built its whole token before the server says anything.
        var first = new StubMechanism(KerberosOid, [1], completeUpFront: true);
        var spnego = Spnego(first);
        await spnego.GetInitialTokenAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken);

        Assert.Null(await spnego.ProcessServerTokenAsync(ServerResponse(0, null, null), TestContext.Current.CancellationToken));
        Assert.Null(first.LastServerToken);
        Assert.True(spnego.IsComplete);
    }

    [Fact]
    public async Task The_server_picking_another_mechanism_drops_the_optimistic_token_and_starts_that_one()
    {
        var optimistic = new StubMechanism(KerberosOid, [1]);
        var fallback = new StubMechanism(NtlmOid, [0x77, 0x88]);
        var spnego = Spnego(optimistic, fallback);
        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        var reply = await spnego.ProcessServerTokenAsync(ServerResponse(3, NtlmOid, null), TestContext.Current.CancellationToken); // request-mic, no token

        Assert.Null(optimistic.LastServerToken);
        Assert.Equal(new byte[] { 0x77, 0x88 }, ParseResponse(reply!).Token);
        Assert.False(spnego.IsComplete);

        // Later server tokens now go to the chosen mechanism.
        await spnego.ProcessServerTokenAsync(ServerResponse(1, null, [9]), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 9 }, fallback.LastServerToken);
    }

    [Fact]
    public async Task A_server_that_answers_a_mechanism_it_chose_over_the_first_with_a_token_is_refused()
    {
        var spnego = Spnego(new StubMechanism(KerberosOid, [1]), new StubMechanism(NtlmOid, [2]));
        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(ServerResponse(1, NtlmOid, [9]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_mechanism_that_was_not_offered_is_refused()
    {
        var spnego = Spnego(new StubMechanism(NtlmOid, [1]));
        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(ServerResponse(1, KerberosOid, [9]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rejection_is_an_authentication_failure()
    {
        var spnego = Spnego(new StubMechanism(NtlmOid, [1]));
        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(ServerResponse(2, null, null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Malformed_server_tokens_are_authentication_failures()
    {
        var spnego = Spnego(new StubMechanism(NtlmOid, [1]));
        var init = await spnego.GetInitialTokenAsync(AuthProtection.Connect, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(new byte[] { 0xA1, 0x05, 0x30 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(init, TestContext.Current.CancellationToken)); // a NegTokenInit, not a response
        await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tokens_are_refused_before_the_handshake_starts_and_after_it_ends()
    {
        var spnego = Spnego(new StubMechanism(NtlmOid, [1]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await spnego.ProcessServerTokenAsync(ServerResponse(1, null, [1]), TestContext.Current.CancellationToken));

        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);
        await spnego.ProcessServerTokenAsync(ServerResponse(0, NtlmOid, [1]), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await spnego.ProcessServerTokenAsync(ServerResponse(0, null, null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Auth_type_signature_size_and_protection_before_completion()
    {
        var spnego = Spnego(new StubMechanism(KerberosOid, [1]), new StubMechanism(NtlmOid, [2]));
        Assert.Equal(RpcAuthType.GssNegotiate, spnego.AuthType);
        Assert.Null(spnego.MechanismOid);
        Assert.Equal(60, spnego.SignatureSize); // the largest, until the server has chosen
        Assert.Throws<InvalidOperationException>(() => spnego.Protect(false, [], new byte[1], [], new byte[16]));

        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);
        await spnego.ProcessServerTokenAsync(ServerResponse(3, NtlmOid, null), TestContext.Current.CancellationToken);
        await spnego.ProcessServerTokenAsync(ServerResponse(1, null, [9]), TestContext.Current.CancellationToken);
        Assert.True(spnego.IsComplete);
        Assert.Equal(16, spnego.SignatureSize);
    }

    [Fact]
    public async Task A_provider_for_a_new_connection_starts_over_with_the_same_mechanisms()
    {
        var spnego = Spnego(new StubMechanism(KerberosOid, [1]), new StubMechanism(NtlmOid, [2]));
        await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);
        await spnego.ProcessServerTokenAsync(ServerResponse(0, KerberosOid, [3]), TestContext.Current.CancellationToken);

        var fresh = spnego.CreateForNewConnection();

        Assert.False(fresh.IsComplete);
        Assert.Equal([KerberosOid, NtlmOid], ParseInit(await fresh.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken)).Oids);
    }

    [Fact]
    public void At_least_one_mechanism_is_required()
    {
        Assert.Throws<ArgumentException>(() => new SpnegoSecurityProvider([], null));
    }

    // --- NTLM through SPNEGO, against the independent NTLM test server ---

    private static async Task<(SpnegoSecurityProvider Spnego, NtlmTestServer Server, byte[] MechTypes, byte[] ClientToken)> StartNtlm(
        AuthProtection protection = AuthProtection.Privacy)
    {
        var spnego = new SpnegoSecurityProvider(Alice);
        var server = new NtlmTestServer("Passw0rd!");
        var (mechTypes, _, mechToken) = ParseInit(await spnego.GetInitialTokenAsync(protection, TestContext.Current.CancellationToken));
        return (spnego, server, mechTypes, mechToken);
    }

    [Fact]
    public async Task Ntlm_authenticate_travels_with_a_mechListMIC_over_the_offered_mechanism_list()
    {
        var (spnego, server, mechTypes, negotiate) = await StartNtlm();
        var challenge = server.ProcessNegotiate(negotiate);

        var reply = await spnego.ProcessServerTokenAsync(ServerResponse(1, NtlmOid, challenge), TestContext.Current.CancellationToken);

        var (authenticate, mic) = ParseResponse(reply!);
        server.ProcessAuthenticate(authenticate!);
        Assert.Equal("alice", server.AuthenticatedUser);
        Assert.NotNull(mic);
        server.Session!.VerifyMic(mechTypes, mic); // throws if the MIC does not cover the mechTypes as sent
        Assert.False(spnego.IsComplete); // the server's answer to the MIC is still to come
    }

    [Fact]
    public async Task The_servers_mechListMIC_in_its_final_token_completes_the_handshake_and_messages_then_flow()
    {
        var (spnego, server, mechTypes, negotiate) = await StartNtlm();
        var challenge = server.ProcessNegotiate(negotiate);
        var (authenticate, mic) = ParseResponse((await spnego.ProcessServerTokenAsync(ServerResponse(1, NtlmOid, challenge), TestContext.Current.CancellationToken))!);
        server.ProcessAuthenticate(authenticate!);
        server.Session!.VerifyMic(mechTypes, mic!);

        Assert.Null(await spnego.ProcessServerTokenAsync(ServerResponse(0, null, null, server.Session.MakeMic(mechTypes)), TestContext.Current.CancellationToken));
        Assert.True(spnego.IsComplete);

        // Both MICs took sequence 0, so the first protected message in each direction is 1 and still verifies.
        var body = Encoding.ASCII.GetBytes("through spnego");
        var signature = new byte[16];
        spnego.Protect(true, [], body, [], signature);
        server.Session.Unprotect(true, [], body, [], signature);
        Assert.Equal("through spnego", Encoding.ASCII.GetString(body));

        var reply = Encoding.ASCII.GetBytes("and back");
        var replySignature = new byte[16];
        server.Session.Protect(true, [], reply, [], replySignature);
        spnego.Unprotect(true, [], reply, [], replySignature);
        Assert.Equal("and back", Encoding.ASCII.GetString(reply));
    }

    [Fact]
    public async Task A_final_token_with_a_wrong_or_missing_mechListMIC_is_refused()
    {
        foreach (var mic in new byte[]?[] { null, new byte[16], [1, 0, 0, 0, .. new byte[12]] })
        {
            var (spnego, server, _, negotiate) = await StartNtlm();
            var (authenticate, _) = ParseResponse((await spnego.ProcessServerTokenAsync(
                ServerResponse(1, NtlmOid, server.ProcessNegotiate(negotiate)), TestContext.Current.CancellationToken))!);
            server.ProcessAuthenticate(authenticate!);

            await Assert.ThrowsAsync<SecurityProviderException>(async () => await spnego.ProcessServerTokenAsync(ServerResponse(0, null, null, mic), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_final_token_that_does_not_say_completed_is_refused()
    {
        var (spnego, server, mechTypes, negotiate) = await StartNtlm();
        var (authenticate, _) = ParseResponse((await spnego.ProcessServerTokenAsync(
            ServerResponse(1, NtlmOid, server.ProcessNegotiate(negotiate)), TestContext.Current.CancellationToken))!);
        server.ProcessAuthenticate(authenticate!);

        await Assert.ThrowsAsync<SecurityProviderException>(async () =>
            await spnego.ProcessServerTokenAsync(ServerResponse(1, null, null, server.Session!.MakeMic(mechTypes)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kerberos_offered_first_then_ntlm_chosen_runs_the_whole_ntlm_handshake_in_place()
    {
        var kerberos = new StubMechanism(KerberosOid, [0x6E, 1]);
        var spnego = Spnego(kerberos, new NtlmSecurityProvider(Alice));
        var server = new NtlmTestServer("Passw0rd!");
        var (mechTypes, oids, _) = ParseInit(await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken));
        Assert.Equal([KerberosOid, NtlmOid], oids);

        // The server cannot use the Kerberos token: it names NTLM and asks for a MIC, with no token of its own.
        var negotiate = ParseResponse((await spnego.ProcessServerTokenAsync(ServerResponse(3, NtlmOid, null), TestContext.Current.CancellationToken))!).Token;
        Assert.Equal("NTLMSSP\0", Encoding.ASCII.GetString(negotiate!, 0, 8));

        var challenge = server.ProcessNegotiate(negotiate!);
        var (authenticate, mic) = ParseResponse((await spnego.ProcessServerTokenAsync(ServerResponse(1, null, challenge), TestContext.Current.CancellationToken))!);
        server.ProcessAuthenticate(authenticate!);
        server.Session!.VerifyMic(mechTypes, mic!);

        Assert.Null(await spnego.ProcessServerTokenAsync(ServerResponse(0, null, null, server.Session.MakeMic(mechTypes)), TestContext.Current.CancellationToken));
        Assert.True(spnego.IsComplete);
        Assert.Null(kerberos.LastServerToken);
    }

    [Fact]
    public async Task CreateAsync_without_kerberos_settings_offers_ntlm_only()
    {
        var spnego = await SpnegoSecurityProvider.CreateAsync(null, Alice, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([NtlmOid], ParseInit(await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken)).Oids);
    }

    [Fact]
    public async Task CreateAsync_falls_back_to_ntlm_when_the_kdc_cannot_be_used()
    {
        var messages = new List<string>();
        var options = new SpnegoOptions
        {
            Realm = "EXAMPLE.COM",
            KdcHost = "127.0.0.1",
            KdcPort = 1, // nothing listens here
            ServicePrincipalName = "host/server01.example.com",
            LoggerFactory = new ListLoggerFactory(messages),
        };

        var spnego = await SpnegoSecurityProvider.CreateAsync(null, Alice, options, TestContext.Current.CancellationToken);

        Assert.Equal([NtlmOid], ParseInit(await spnego.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken)).Oids);
        Assert.Contains(messages, m => m.StartsWith("No Kerberos ticket", StringComparison.Ordinal));
    }
}

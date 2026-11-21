using System.Text;
using NWmi.Auth;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Auth;

namespace NWmi.Tests.Rpc;

public class RpcAuthenticatedClientTests
{
    private const string Password = "Passw0rd!";
    private static readonly RpcSyntaxId Exporter = RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a");
    private static readonly RpcSyntaxId Other = RpcSyntaxId.Parse("0000143a-0000-0000-c000-000000000046");

    private sealed class Rig : IAsyncDisposable
    {
        public required RpcClient Client { get; init; }

        public required FakeRpcServer Server { get; init; }

        public required TappingTransport Wire { get; init; }

        public required NtlmTestServer Ntlm { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static Rig CreateRig(Action<FakeRpcServer>? configureServer = null, string serverPassword = Password)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var wire = new TappingTransport(clientSide);
        var ntlm = new NtlmTestServer(serverPassword);
        var server = new FakeRpcServer(serverSide) { Ntlm = ntlm };
        configureServer?.Invoke(server);
        server.Start();
        var client = new RpcClient(wire, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) });
        return new Rig { Client = client, Server = server, Wire = wire, Ntlm = ntlm };
    }

    private static RpcSecurity Security(RpcAuthLevel level, string password = Password) =>
        new(new NtlmSecurityProvider(new Credential("alice", password, "CORP")), level);

    private static byte[] Payload(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 13 + 5))];

    [Fact]
    public async Task A_new_connection_after_an_orphaned_call_authenticates_and_protects_again()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var first = new FakeRpcServer(serverSide) { Ntlm = new NtlmTestServer(Password) };
        first.CallHandler = fragments => fragments[0].OpNum == 1 ? null : first.EchoResponses(fragments);
        first.Start();

        FakeRpcServer? second = null;
        await using var client = new RpcClient(clientSide, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) },
            _ =>
            {
                var (newClientSide, newServerSide) = LoopbackTransport.CreatePair();
                second = new FakeRpcServer(newServerSide) { Ntlm = new NtlmTestServer(Password) };
                second.Start();
                return new(newClientSide);
            });
        try
        {
            var context = (await client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallAsync(context, 1, Payload(8), cancellationToken: cancel.Token));
            await first.WaitForOrphanAsync();

            var payload = Payload(300);
            Assert.Equal(payload, await client.CallAsync(context, 2, payload, cancellationToken: TestContext.Current.CancellationToken));

            Assert.NotNull(second);
            Assert.True(second.Authenticated);
            Assert.True(second.HeaderSigningAgreed);
            var request = Assert.Single(second.Requests);
            Assert.Equal(RpcAuthLevel.PacketPrivacy, request.Auth!.AuthLevel);
            Assert.Equal(payload, request.Stub); // the server decrypted it with the new connection's keys
        }
        finally
        {
            if (second is not null)
                await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Privacy_level_encrypts_requests_on_the_wire_and_round_trips()
    {
        await using var rig = CreateRig();
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];

        var payload = Encoding.ASCII.GetBytes("a secret value that must not be visible on the wire");
        var response = await rig.Client.CallAsync(context, 3, payload, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, response);
        Assert.True(rig.Server.Authenticated);
        Assert.Equal("alice", rig.Ntlm.AuthenticatedUser);

        var request = RpcPdu.Parse(rig.Wire.Sent[^1]);
        var onWire = Assert.IsType<RpcRequestPdu>(request).Stub;
        Assert.DoesNotContain("secret", Encoding.ASCII.GetString(onWire), StringComparison.Ordinal);
        Assert.Equal(16, request.Auth!.Value.Length);
        Assert.Equal(RpcAuthLevel.PacketPrivacy, request.Auth.AuthLevel);

        var reply = RpcPdu.Parse(rig.Wire.Received[^1]);
        Assert.DoesNotContain("secret", Encoding.ASCII.GetString(Assert.IsType<RpcResponsePdu>(reply).Stub), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Integrity_level_signs_but_does_not_encrypt()
    {
        await using var rig = CreateRig();
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketIntegrity), TestContext.Current.CancellationToken))[0];

        var payload = Encoding.ASCII.GetBytes("visible but signed");
        Assert.Equal(payload, await rig.Client.CallAsync(context, 1, payload, cancellationToken: TestContext.Current.CancellationToken));

        var request = Assert.IsType<RpcRequestPdu>(RpcPdu.Parse(rig.Wire.Sent[^1]));
        Assert.StartsWith("visible but signed", Encoding.ASCII.GetString(request.Stub), StringComparison.Ordinal);
        Assert.NotNull(request.Auth);
        Assert.Equal(RpcAuthLevel.PacketIntegrity, request.Auth.AuthLevel);
    }

    [Fact]
    public async Task Connect_level_authenticates_at_bind_and_leaves_calls_unprotected()
    {
        await using var rig = CreateRig();
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.Connect), TestContext.Current.CancellationToken))[0];

        Assert.Equal(new byte[] { 1, 2, 3 }, await rig.Client.CallAsync(context, 1, new byte[] { 1, 2, 3 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(rig.Server.Authenticated);
        Assert.Null(RpcPdu.Parse(rig.Wire.Sent[^1]).Auth);
    }

    [Fact]
    public async Task The_bind_carries_the_first_token_and_an_auth3_follows_the_ack()
    {
        await using var rig = CreateRig();
        await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken);

        var bind = Assert.IsType<RpcBindPdu>(RpcPdu.Parse(rig.Wire.Sent[0]));
        Assert.Equal(RpcAuthType.WinNt, bind.Auth!.AuthType);
        Assert.Equal("NTLMSSP\0", Encoding.ASCII.GetString(bind.Auth.Value, 0, 8));

        var auth3 = Assert.IsType<RpcAuth3Pdu>(RpcPdu.Parse(rig.Wire.Sent[1]));
        Assert.Equal(bind.CallId, auth3.CallId);
        Assert.Equal(3, BitConverter.ToInt32(auth3.Auth!.Value, 8));
    }

    [Theory]
    [InlineData(RpcAuthLevel.PacketIntegrity)]
    [InlineData(RpcAuthLevel.PacketPrivacy)]
    public async Task Header_signing_is_used_when_both_sides_agree(RpcAuthLevel level)
    {
        await using var rig = CreateRig();
        var context = (await rig.Client.BindAsync([Exporter], Security(level), TestContext.Current.CancellationToken))[0];

        Assert.True(rig.Server.HeaderSigningAgreed);
        Assert.True(((RpcPfcFlags)rig.Wire.Sent[0][3]).HasFlag(RpcPfcFlags.PendingCancelOrSupportHeaderSign));
        var payload = Payload(300);
        Assert.Equal(payload, await rig.Client.CallAsync(context, 2, payload, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Header_signing_falls_back_when_the_server_does_not_agree()
    {
        await using var rig = CreateRig(s => s.AgreeToHeaderSigning = false);
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];

        Assert.False(rig.Server.HeaderSigningAgreed);
        Assert.Equal(new byte[] { 9 }, await rig.Client.CallAsync(context, 2, new byte[] { 9 }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(RpcAuthLevel.PacketIntegrity, false)]
    [InlineData(RpcAuthLevel.PacketPrivacy, false)]
    [InlineData(RpcAuthLevel.PacketPrivacy, true)]
    public async Task Fragmented_calls_protect_every_fragment_in_sequence(RpcAuthLevel level, bool headerSigning)
    {
        await using var rig = CreateRig(s =>
        {
            s.MaxRecvFrag = 600;
            s.AgreeToHeaderSigning = headerSigning;
            s.ResponseFragmentSize = 200;
        });
        var context = (await rig.Client.BindAsync([Exporter], Security(level), TestContext.Current.CancellationToken))[0];

        var payload = Payload(4000);
        Assert.Equal(payload, await rig.Client.CallAsync(context, 5, payload, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(payload, await rig.Client.CallAsync(context, 5, payload, cancellationToken: TestContext.Current.CancellationToken)); // sequence numbers carry on across calls

        var requests = rig.Wire.Sent.Skip(2).Select(p => RpcPdu.Parse(p)).OfType<RpcRequestPdu>().ToList();
        Assert.True(requests.Count > 10);
        Assert.All(requests, r => Assert.Equal(16, r.Auth!.Value.Length));
        Assert.All(requests, r => Assert.True(r.Auth!.Value.Length + 24 + r.Stub.Length + r.Auth.PadLength + 8 <= 600));
    }

    [Fact]
    public async Task A_wrong_password_shows_up_as_an_access_denied_fault_on_the_first_call()
    {
        await using var rig = CreateRig(serverPassword: "something else");
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];

        var fault = await Assert.ThrowsAsync<RpcFaultException>(() => rig.Client.CallAsync(context, 1, new byte[8], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(RpcStatus.AccessDenied, fault.Status);
        Assert.False(rig.Server.Authenticated);
    }

    [Fact]
    public async Task A_tampered_response_is_rejected_and_breaks_the_connection()
    {
        await using var rig = CreateRig();
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];
        rig.Wire.RewriteReceived = pdu =>
        {
            pdu[30] ^= 0x40; // inside the encrypted stub
            return pdu;
        };

        await Assert.ThrowsAsync<SecurityProviderException>(() => rig.Client.CallAsync(context, 1, Payload(64), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Client.CallAsync(context, 1, Payload(64), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tampered_header_is_caught_only_when_header_signing_is_on()
    {
        // With header signing the opnum-side fields of the response header are signed; flipping alloc_hint must fail.
        await using var signed = CreateRig();
        var signedContext = (await signed.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketIntegrity), TestContext.Current.CancellationToken))[0];
        signed.Wire.RewriteReceived = pdu =>
        {
            pdu[16] ^= 1; // alloc_hint, part of the PDU header
            return pdu;
        };
        await Assert.ThrowsAsync<SecurityProviderException>(() => signed.Client.CallAsync(signedContext, 1, Payload(32), cancellationToken: TestContext.Current.CancellationToken));

        await using var unsigned = CreateRig(s => s.AgreeToHeaderSigning = false);
        var unsignedContext = (await unsigned.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketIntegrity), TestContext.Current.CancellationToken))[0];
        unsigned.Wire.RewriteReceived = pdu =>
        {
            pdu[16] ^= 1;
            return pdu;
        };
        Assert.Equal(32, (await unsigned.Client.CallAsync(unsignedContext, 1, Payload(32), cancellationToken: TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task An_unprotected_response_on_a_protected_connection_is_refused()
    {
        await using var rig = CreateRig(s => s.UnprotectedResponses = true);
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];

        var ex = await Assert.ThrowsAsync<RpcProtocolException>(() => rig.Client.CallAsync(context, 1, Payload(16), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("unprotected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_with_a_different_security_context_is_refused()
    {
        await using var rig = CreateRig();
        var context = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketIntegrity), TestContext.Current.CancellationToken))[0];
        rig.Wire.RewriteReceived = pdu =>
        {
            pdu[pdu.Length - 16 - 8 + 4] ^= 1; // auth_context_id in the sec_trailer (8 bytes before the 16-byte signature)
            return pdu;
        };

        await Assert.ThrowsAsync<RpcProtocolException>(() => rig.Client.CallAsync(context, 1, Payload(16), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Interfaces_added_after_bind_work_on_the_authenticated_connection()
    {
        await using var rig = CreateRig();
        var first = (await rig.Client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken))[0];
        var second = (await rig.Client.AlterContextAsync([Other], TestContext.Current.CancellationToken))[0];

        Assert.Equal(new byte[] { 1 }, await rig.Client.CallAsync(first, 0, new byte[] { 1 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 2 }, await rig.Client.CallAsync(second, 0, new byte[] { 2 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(RpcPdu.Parse(rig.Wire.Sent.First(p => p[2] == (byte)RpcPduType.AlterContext)).Auth);
    }

    [Fact]
    public async Task A_server_that_skips_authentication_is_refused()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide); // no Ntlm: answers the bind without a token
        server.Start();
        await using var client = new RpcClient(clientSide);

        await Assert.ThrowsAsync<RpcProtocolException>(() => client.BindAsync([Exporter], Security(RpcAuthLevel.PacketPrivacy), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Only_supported_levels_are_accepted()
    {
        var provider = new NtlmSecurityProvider(new Credential("a", "b"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RpcSecurity(provider, RpcAuthLevel.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RpcSecurity(provider, RpcAuthLevel.Packet));
    }
}

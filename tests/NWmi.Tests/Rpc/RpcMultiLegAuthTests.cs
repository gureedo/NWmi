using NWmi.Auth;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;

namespace NWmi.Tests.Rpc;

/// <summary>A handshake with more than the bind, bind_ack and auth3 legs: the extra legs ride in alter_context PDUs.</summary>
public class RpcMultiLegAuthTests
{
    private static readonly RpcSyntaxId Exporter = RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a");

    /// <summary>Returns [2] to the first server token (and wants an answer), then [3] to the second (and is done).</summary>
    private sealed class ThreeLegProvider : IRpcSecurityProvider
    {
        public List<byte[]> ServerTokens { get; } = [];

        public RpcAuthType AuthType => RpcAuthType.GssNegotiate;

        public string? MechanismOid => null;

        public int SignatureSize => 16;

        public bool IsComplete { get; private set; }

        public ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default) => new([1]);

        public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token, CancellationToken cancellationToken = default) =>
            new(ProcessServerToken(token.Span));

        private byte[]? ProcessServerToken(ReadOnlySpan<byte> token)
        {
            ServerTokens.Add(token.ToArray());
            if (ServerTokens.Count == 1)
                return [2];

            IsComplete = true;
            return ServerTokens.Count == 2 ? [3] : null;
        }

        public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, Span<byte> signature)
        {
        }

        public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature)
        {
        }

        public IRpcSecurityProvider CreateForNewConnection() => new ThreeLegProvider();
    }

    private static RpcBindAckPdu Ack(RpcBindPdu request, byte[]? authToken) => new()
    {
        PduType = request.Type == RpcPduType.Bind ? RpcPduType.BindAck : RpcPduType.AlterContextResponse,
        CallId = request.CallId,
        MaxXmitFrag = 5840,
        MaxRecvFrag = 5840,
        AssocGroupId = 0x77,
        SecondaryAddress = "135",
        Results = [.. request.Contexts.Select(_ => new RpcContextResultElement(RpcContextResult.Acceptance, 0, RpcSyntaxId.Ndr20))],
        Auth = authToken is null ? null : new RpcAuthTrailer(request.Auth!.AuthType, request.Auth.AuthLevel, request.Auth.ContextId, authToken),
    };

    private static (RpcClient Client, FakeRpcServer Server, TappingTransport Wire) Create(Func<RpcBindPdu, RpcPdu?> onBind)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var wire = new TappingTransport(clientSide);
        var server = new FakeRpcServer(serverSide) { BindOverride = onBind };
        server.Start();
        return (new RpcClient(wire, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) }), server, wire);
    }

    [Fact]
    public async Task A_provider_that_needs_the_servers_answer_gets_it_through_alter_context()
    {
        var (client, server, wire) = Create(bind => Ack(bind, bind.Type == RpcPduType.Bind ? [0xA] : [0xB]));
        await using var _ = server;
        await using var __ = client;
        var provider = new ThreeLegProvider();

        var contexts = await client.BindAsync([Exporter], new RpcSecurity(provider, RpcAuthLevel.Connect), TestContext.Current.CancellationToken);

        Assert.Single(contexts);
        Assert.Equal([new byte[] { 0xA }, new byte[] { 0xB }], provider.ServerTokens);

        var bind = Assert.IsType<RpcBindPdu>(RpcPdu.Parse(wire.Sent[0]));
        var alter = Assert.IsType<RpcBindPdu>(RpcPdu.Parse(wire.Sent[1]));
        Assert.Equal(RpcPduType.AlterContext, alter.Type);
        Assert.Equal(new byte[] { 2 }, alter.Auth!.Value);
        Assert.Equal(0x77u, alter.AssocGroupId);
        Assert.NotEqual(bind.CallId, alter.CallId);
        Assert.Equal(bind.Contexts.Select(c => c.ContextId), alter.Contexts.Select(c => c.ContextId));

        // The last leg, which expects no answer, is an auth3 on the bind's call id.
        var auth3 = Assert.IsType<RpcAuth3Pdu>(RpcPdu.Parse(wire.Sent[2]));
        Assert.Equal(bind.CallId, auth3.CallId);
        Assert.Equal(new byte[] { 3 }, auth3.Auth!.Value);
        Assert.Equal(3, wire.Sent.Count);

        Assert.Equal(new byte[] { 5 }, await client.CallAsync(contexts[0], 1, new byte[] { 5 }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_new_connection_after_an_orphaned_call_runs_every_authentication_leg_again()
    {
        FakeRpcServer? second = null;
        Func<RpcBindPdu, RpcPdu?> onBind = bind => Ack(bind, bind.Type == RpcPduType.Bind ? [0xA] : [0xB]);
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var first = new FakeRpcServer(serverSide) { BindOverride = onBind };
        first.CallHandler = fragments => fragments[0].OpNum == 1 ? null : first.EchoResponses(fragments);
        first.Start();
        await using var client = new RpcClient(clientSide, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) },
            _ =>
            {
                var (newClientSide, newServerSide) = LoopbackTransport.CreatePair();
                second = new FakeRpcServer(newServerSide) { BindOverride = onBind };
                second.Start();
                return new(newClientSide);
            });
        try
        {
            var context = (await client.BindAsync([Exporter], new RpcSecurity(new ThreeLegProvider(), RpcAuthLevel.Connect), TestContext.Current.CancellationToken))[0];

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.CallAsync(context, 1, new byte[] { 1 }, cancellationToken: cancel.Token));
            await first.WaitForOrphanAsync();

            Assert.Equal(new byte[] { 5 }, await client.CallAsync(context, 2, new byte[] { 5 }, cancellationToken: TestContext.Current.CancellationToken));

            // The bind, then the alter_context that carries the extra leg, with a token from a provider of its own.
            Assert.NotNull(second);
            Assert.Equal([RpcPduType.Bind, RpcPduType.AlterContext], second.Binds.Select(b => b.Type));
            Assert.Equal(new byte[] { 1 }, second.Binds[0].Auth!.Value);
            Assert.Equal(new byte[] { 2 }, second.Binds[1].Auth!.Value);
            Assert.Equal(context.ContextId, Assert.Single(second.Requests).ContextId);
        }
        finally
        {
            if (second is not null)
                await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_authentication_leg_answered_without_a_token_is_a_protocol_error()
    {
        var (client, server, _) = Create(bind => Ack(bind, bind.Type == RpcPduType.Bind ? [0xA] : null));
        await using var _1 = server;
        await using var _2 = client;

        await Assert.ThrowsAsync<RpcProtocolException>(() =>
            client.BindAsync([Exporter], new RpcSecurity(new ThreeLegProvider(), RpcAuthLevel.Connect), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rejected_authentication_leg_fails_the_bind()
    {
        var (client, server, _) = Create(bind => bind.Type == RpcPduType.Bind
            ? Ack(bind, [0xA])
            : new RpcBindNakPdu { CallId = bind.CallId, Reason = RpcBindRejectReason.LocalLimitExceeded });
        await using var _1 = server;
        await using var _2 = client;

        await Assert.ThrowsAsync<RpcBindRejectedException>(() =>
            client.BindAsync([Exporter], new RpcSecurity(new ThreeLegProvider(), RpcAuthLevel.Connect), TestContext.Current.CancellationToken));
    }
}

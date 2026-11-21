using NWmi.Auth;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Transport;

namespace NWmi.Tests.Rpc;

/// <summary>What a connection survives: a given-up call must not take the whole session with it, and a failed handshake must.</summary>
public class RpcClientRecoveryTests
{
    private const ushort SlowOp = 1;
    private const ushort EchoOp = 9;

    private static readonly RpcSyntaxId Exporter = RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a");
    private static readonly RpcSyntaxId Activator = RpcSyntaxId.Parse("000001a0-0000-0000-c000-000000000046");

    private static RpcResponsePdu Response(RpcRequestPdu request, byte[] stub, RpcPfcFlags flags) => new()
    {
        CallId = request.CallId,
        ContextId = request.ContextId,
        Flags = flags,
        Stub = stub,
    };

    /// <summary>A server still working on a call: it finishes its reply only once the test sets <see cref="Finish"/>.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(TimeSpan? timeout = null, bool canReconnect = false)
        {
            var (clientSide, serverSide) = LoopbackTransport.CreatePair();
            Wire = new TappingTransport(clientSide);
            Server = new FakeRpcServer(serverSide);
            Server.CallHandler = fragments => fragments[0].OpNum switch
            {
                SlowOp => Slow(fragments[0]),
                _ => Server.EchoResponses(fragments),
            };
            Server.Start();
            Client = new RpcClient(Wire, new RpcClientOptions { Timeout = timeout ?? TimeSpan.FromSeconds(10) },
                canReconnect ? Reconnect : null);
        }

        /// <summary>The servers behind the connections opened after the first, as a client that reconnects sees them.</summary>
        public List<FakeRpcServer> Reconnected { get; } = [];

        /// <summary>How many of the next reconnection attempts fail, as when the server is unreachable.</summary>
        public int ReconnectFailures { get; set; }

        /// <summary>Whether the server of the next new connection answers the bind with a bind_nak.</summary>
        public bool RejectNextBind { get; set; }

        private ValueTask<IPduTransport> Reconnect(CancellationToken cancellationToken)
        {
            if (ReconnectFailures > 0)
            {
                ReconnectFailures--;
                throw new IOException("The server is unreachable.");
            }

            var (clientSide, serverSide) = LoopbackTransport.CreatePair();
            var server = new FakeRpcServer(serverSide) { CallHandler = Server.CallHandler };
            if (RejectNextBind)
            {
                RejectNextBind = false;
                server.BindOverride = bind => new RpcBindNakPdu
                {
                    CallId = bind.CallId,
                    Reason = RpcBindRejectReason.TemporaryCongestion,
                };
            }

            server.Start();
            Reconnected.Add(server);
            return new(clientSide);
        }

        public RpcClient Client { get; }

        public FakeRpcServer Server { get; }

        public TappingTransport Wire { get; }

        public ManualResetEventSlim Finish { get; } = new();

        public int RequestsSent => Wire.Sent.Count(pdu => pdu[2] == (byte)RpcPduType.Request);

        private IEnumerable<RpcPdu> Slow(RpcRequestPdu request)
        {
            Finish.Wait();
            yield return Response(request, [0xBE, 0xEF], RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment);
        }

        public async ValueTask DisposeAsync()
        {
            Finish.Set();
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            foreach (var server in Reconnected)
                await server.DisposeAsync();

            Finish.Dispose();
        }
    }

    private static Task<IReadOnlyList<RpcPresentationContext>> Bind(RpcClient client) =>
        client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken);

    private static Task<byte[]> Echo(RpcClient client, RpcPresentationContext context, byte value) =>
        client.CallAsync(context, EchoOp, new[] { value }, cancellationToken: TestContext.Current.CancellationToken);

    private static async Task CancelCallAsync(RpcClient client, RpcPresentationContext context, ushort opnum)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CallAsync(context, opnum, new byte[] { 1 }, cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task A_cancelled_call_is_orphaned_and_the_next_operation_uses_a_new_connection()
    {
        await using var rig = new Rig(canReconnect: true);
        var context = (await Bind(rig.Client))[0];

        await CancelCallAsync(rig.Client, context, SlowOp);

        await rig.Server.WaitForOrphanAsync();

        // The server is still working on the call (Finish is not set), and the next call does not wait for it.
        Assert.Equal(new byte[] { 7 }, await Echo(rig.Client, context, 7));
        var next = Assert.Single(rig.Reconnected);
        Assert.Single(next.Binds);
        Assert.Equal(7, Assert.Single(next.Requests).Stub[0]);
        Assert.Equal(context.ContextId, next.Requests[0].ContextId);
        Assert.Equal(new byte[] { 8 }, await Echo(rig.Client, context, 8));
        Assert.Single(rig.Reconnected);
    }

    [Fact]
    public async Task A_timed_out_call_is_orphaned_too()
    {
        await using var rig = new Rig(TimeSpan.FromMilliseconds(200), canReconnect: true);
        var context = (await Bind(rig.Client))[0];

        await Assert.ThrowsAsync<TimeoutException>(() =>
            rig.Client.CallAsync(context, SlowOp, new byte[] { 1 }, cancellationToken: TestContext.Current.CancellationToken));

        await rig.Server.WaitForOrphanAsync();
        Assert.Equal(new byte[] { 7 }, await Echo(rig.Client, context, 7));
        Assert.Single(rig.Reconnected);
    }

    [Fact]
    public async Task A_new_connection_binds_every_context_again_under_the_same_ids()
    {
        await using var rig = new Rig(canReconnect: true);
        var exporter = (await Bind(rig.Client))[0];
        var activator = (await rig.Client.AlterContextAsync([Activator], TestContext.Current.CancellationToken))[0];

        await CancelCallAsync(rig.Client, exporter, SlowOp);
        Assert.Equal(new byte[] { 5 }, await Echo(rig.Client, activator, 5));

        var next = Assert.Single(rig.Reconnected);
        Assert.Equal(2, next.Binds.Count);
        Assert.Equal([exporter.ContextId, activator.ContextId], next.Binds.SelectMany(b => b.Contexts.Select(c => c.ContextId)));
        Assert.Equal(activator.ContextId, Assert.Single(next.Requests).ContextId);
        Assert.Same(activator, (await rig.Client.AlterContextAsync([Activator], TestContext.Current.CancellationToken))[0]);
    }

    [Fact]
    public async Task A_failed_reconnection_is_not_fatal_and_the_next_operation_tries_again()
    {
        await using var rig = new Rig(canReconnect: true);
        var context = (await Bind(rig.Client))[0];
        await CancelCallAsync(rig.Client, context, SlowOp);

        rig.ReconnectFailures = 2;
        await Assert.ThrowsAsync<IOException>(() => Echo(rig.Client, context, 1));
        await Assert.ThrowsAsync<IOException>(() => Echo(rig.Client, context, 2));
        Assert.Empty(rig.Reconnected);

        Assert.Equal(new byte[] { 3 }, await Echo(rig.Client, context, 3));
        Assert.Single(rig.Reconnected);
    }

    [Fact]
    public async Task A_server_that_stays_unreachable_fails_every_operation_with_its_own_error_and_can_still_be_disposed()
    {
        await using var rig = new Rig(canReconnect: true);
        var context = (await Bind(rig.Client))[0];
        await CancelCallAsync(rig.Client, context, SlowOp);

        rig.ReconnectFailures = int.MaxValue;
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<IOException>(() => Echo(rig.Client, context, 1));

        Assert.Empty(rig.Reconnected);
        await rig.Client.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Echo(rig.Client, context, 1));
    }

    [Fact]
    public async Task A_reconnection_the_server_rejects_is_not_fatal_either()
    {
        await using var rig = new Rig(canReconnect: true);
        var context = (await Bind(rig.Client))[0];
        await CancelCallAsync(rig.Client, context, SlowOp);

        rig.RejectNextBind = true;
        await Assert.ThrowsAsync<RpcBindRejectedException>(() => Echo(rig.Client, context, 1));
        Assert.Equal(new byte[] { 2 }, await Echo(rig.Client, context, 2));
        Assert.Equal(2, rig.Reconnected.Count);
    }

    [Fact]
    public async Task A_disposed_client_does_not_reconnect()
    {
        await using var rig = new Rig(canReconnect: true);
        var context = (await Bind(rig.Client))[0];
        await CancelCallAsync(rig.Client, context, SlowOp);

        await rig.Client.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Echo(rig.Client, context, 1));
        Assert.Empty(rig.Reconnected);
    }

    [Fact]
    public async Task A_client_that_cannot_reconnect_is_unusable_after_a_cancelled_call()
    {
        await using var rig = new Rig();
        var context = (await Bind(rig.Client))[0];

        await CancelCallAsync(rig.Client, context, SlowOp);

        await rig.Server.WaitForOrphanAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Echo(rig.Client, context, 7));
    }

    [Fact]
    public async Task A_send_that_outlasts_the_timeout_breaks_the_connection()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var wire = new StallingTransport(clientSide);
        var server = new FakeRpcServer(serverSide);
        server.Start();
        await using var _ = server;
        await using var client = new RpcClient(wire, new RpcClientOptions { Timeout = TimeSpan.FromMilliseconds(300) });
        var context = (await Bind(client))[0];

        wire.Stalled = true;
        await Assert.ThrowsAsync<TimeoutException>(() => Echo(client, context, 1));
        wire.Stalled = false;

        // The server may hold part of a request, so nothing more can safely be sent on this connection.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Echo(client, context, 1));
    }

    [Fact]
    public async Task Cancelling_while_a_request_is_being_sent_lets_it_finish_and_the_next_call_reconnects()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var wire = new SlowSendTransport(clientSide);
        var server = new FakeRpcServer(serverSide);
        server.Start();
        FakeRpcServer? second = null;
        await using var _ = server;
        await using var client = new RpcClient(wire, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) },
            _ =>
            {
                var (newClientSide, newServerSide) = LoopbackTransport.CreatePair();
                second = new FakeRpcServer(newServerSide);
                second.Start();
                return new(newClientSide);
            });
        try
        {
            var context = (await Bind(client))[0];

            wire.Delay = TimeSpan.FromMilliseconds(400);
            await CancelCallAsync(client, context, EchoOp);
            wire.Delay = TimeSpan.Zero;

            // The request went out in full, so the server has it whole and is told the call is orphaned.
            await server.WaitForOrphanAsync();
            Assert.Equal(new byte[] { 7 }, await Echo(client, context, 7));
            Assert.NotNull(second);
        }
        finally
        {
            if (second is not null)
                await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelling_during_an_alter_context_lets_it_finish_and_keeps_the_connection()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var wire = new SlowSendTransport(clientSide);
        var server = new FakeRpcServer(serverSide);
        server.Start();
        await using var _ = server;
        await using var client = new RpcClient(wire, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) });
        var context = (await Bind(client))[0];

        wire.Delay = TimeSpan.FromMilliseconds(400);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var altered = await client.AlterContextAsync([Activator], cancel.Token);
        wire.Delay = TimeSpan.Zero;

        Assert.Single(altered);
        Assert.Equal(new byte[] { 7 }, await Echo(client, context, 7));
    }

    [Fact]
    public async Task A_fault_during_the_handshake_breaks_the_connection_rather_than_leave_it_unprotected()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide)
        {
            BindOverride = bind => bind.Type == RpcPduType.Bind
                ? new RpcBindAckPdu
                {
                    PduType = RpcPduType.BindAck,
                    CallId = bind.CallId,
                    MaxXmitFrag = 5840,
                    MaxRecvFrag = 5840,
                    AssocGroupId = 0x77,
                    SecondaryAddress = "135",
                    Results = [.. bind.Contexts.Select(_ => new RpcContextResultElement(RpcContextResult.Acceptance, 0, RpcSyntaxId.Ndr20))],
                    Auth = new RpcAuthTrailer(bind.Auth!.AuthType, bind.Auth.AuthLevel, bind.Auth.ContextId, [0xA]),
                }
                : new RpcFaultPdu { CallId = bind.CallId, Status = RpcStatus.AccessDenied },
        };
        server.Start();
        await using var _ = server;
        await using var client = new RpcClient(clientSide, new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) });

        await Assert.ThrowsAsync<RpcFaultException>(() =>
            client.BindAsync([Exporter], new RpcSecurity(new TwoLegProvider()), TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AlterContextAsync([Activator], TestContext.Current.CancellationToken));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Altering_to_an_interface_already_bound_reuses_its_context()
    {
        await using var rig = new Rig();

        var bound = (await Bind(rig.Client))[0];
        var again = (await rig.Client.AlterContextAsync([Exporter], TestContext.Current.CancellationToken))[0];
        var added = (await rig.Client.AlterContextAsync([Activator], TestContext.Current.CancellationToken))[0];
        var addedAgain = (await rig.Client.AlterContextAsync([Activator], TestContext.Current.CancellationToken))[0];

        Assert.Same(bound, again);
        Assert.Same(added, addedAgain);
        Assert.Equal(2, rig.Server.Binds.Count); // the bind, and one alter_context for the new interface
    }

    /// <summary>While <see cref="Stalled"/>, a send waits until it is cancelled, as on a connection whose peer stopped reading.</summary>
    private sealed class StallingTransport(IPduTransport inner) : IPduTransport
    {
        public bool Stalled { get; set; }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
        {
            if (Stalled)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            await inner.SendAsync(pdu, cancellationToken);
        }

        public ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default) => inner.ReceiveAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Takes <see cref="Delay"/> over each send, and honours the token only after that, as a real stream does on entry.</summary>
    private sealed class SlowSendTransport(IPduTransport inner) : IPduTransport
    {
        public TimeSpan Delay { get; set; }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Delay, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            await inner.SendAsync(pdu, cancellationToken);
        }

        public ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default) => inner.ReceiveAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Wants one more round trip after the bind, so a fault can arrive with the handshake half done.</summary>
    private sealed class TwoLegProvider : IRpcSecurityProvider
    {
        public RpcAuthType AuthType => RpcAuthType.GssNegotiate;

        public string? MechanismOid => null;

        public int SignatureSize => 16;

        public bool IsComplete => false;

        public ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default) => new([1]);

        public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token, CancellationToken cancellationToken = default) => new([2]);

        public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, Span<byte> signature)
        {
        }

        public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature)
        {
        }

        public IRpcSecurityProvider CreateForNewConnection() => new TwoLegProvider();
    }
}

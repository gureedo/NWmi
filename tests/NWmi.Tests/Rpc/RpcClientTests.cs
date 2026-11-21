using System.Net;
using System.Net.Sockets;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Transport;

namespace NWmi.Tests.Rpc;

public class RpcClientTests
{
    private static readonly RpcSyntaxId Exporter = RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a");
    private static readonly RpcSyntaxId Activator = RpcSyntaxId.Parse("000001a0-0000-0000-c000-000000000046");
    private static readonly RpcSyntaxId Other = RpcSyntaxId.Parse("0000143a-0000-0000-c000-000000000046");

    private static (RpcClient Client, FakeRpcServer Server) Create(Action<RpcClientOptions>? configureClient = null, Action<FakeRpcServer>? configureServer = null)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var options = new RpcClientOptions { Timeout = TimeSpan.FromSeconds(10) };
        configureClient?.Invoke(options);
        var server = new FakeRpcServer(serverSide);
        configureServer?.Invoke(server);
        server.Start();
        return (new RpcClient(clientSide, options), server);
    }

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 3))];

    [Fact]
    public async Task Bind_assigns_context_ids_and_negotiates_the_fragment_size()
    {
        var (client, server) = Create(configureServer: s => s.MaxRecvFrag = 1024);
        await using (client)
        await using (server)
        {
            var contexts = await client.BindAsync([Exporter, Activator], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal([(ushort)0, (ushort)1], contexts.Select(c => c.ContextId));
            Assert.Equal(Exporter, contexts[0].Interface);
            Assert.Equal(RpcSyntaxId.Ndr20, contexts[0].TransferSyntax);
            Assert.Equal(0x1234u, client.AssocGroupId);
            Assert.Equal(1024, client.MaxSendFragment);

            var bind = Assert.Single(server.Binds);
            Assert.Equal(5840, bind.MaxXmitFrag);
            Assert.Equal(0u, bind.AssocGroupId);
            Assert.All(bind.Contexts, c => Assert.Equal(RpcSyntaxId.Ndr20, Assert.Single(c.TransferSyntaxes)));
        }
    }

    [Fact]
    public async Task Bind_time_feature_negotiation_adds_a_marker_element_that_is_not_an_error()
    {
        var (client, server) = Create(o => o.OfferBindTimeFeatureNegotiation = true);
        await using (client)
        await using (server)
        {
            var contexts = await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(contexts);

            var offered = Assert.Single(server.Binds).Contexts;
            Assert.Equal(2, offered.Count);
            Assert.Equal(offered[0].AbstractSyntax, offered[1].AbstractSyntax);
            var marker = Assert.Single(offered[1].TransferSyntaxes);
            Assert.Equal(new Version(1, 0), new Version(marker.VersionMajor, marker.VersionMinor));
            Assert.Equal(
                new byte[] { 0x2C, 0x1C, 0xB7, 0x6C, 0x12, 0x98, 0x40, 0x45 },
                marker.Uuid.ToByteArray()[..8]);
        }
    }

    [Fact]
    public async Task Bind_nak_is_reported_and_the_connection_is_then_unusable()
    {
        var (client, server) = Create(configureServer: s => s.BindOverride = b => new RpcBindNakPdu
        {
            CallId = b.CallId,
            Reason = RpcBindRejectReason.TemporaryCongestion,
        });
        await using (client)
        await using (server)
        {
            var ex = await Assert.ThrowsAsync<RpcBindRejectedException>(() => client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("TemporaryCongestion", ex.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Rejected_interface_names_the_interface_and_reason()
    {
        var (client, server) = Create(configureServer: s => s.BindOverride = b => new RpcBindAckPdu
        {
            CallId = b.CallId,
            MaxXmitFrag = 5840,
            MaxRecvFrag = 5840,
            Results =
            [
                new RpcContextResultElement(RpcContextResult.Acceptance, 0, RpcSyntaxId.Ndr20),
                new RpcContextResultElement(
                    RpcContextResult.ProviderRejection, (ushort)RpcContextRejectReason.AbstractSyntaxNotSupported, default),
            ],
        });
        await using (client)
        await using (server)
        {
            var ex = await Assert.ThrowsAsync<RpcBindRejectedException>(() => client.BindAsync([Exporter, Other], cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(Other.Uuid.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Contains("AbstractSyntaxNotSupported", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Server_choosing_another_transfer_syntax_is_a_protocol_error()
    {
        var other = new RpcSyntaxId(Guid.NewGuid(), 1, 0);
        var (client, server) = Create(configureServer: s => s.BindOverride = b => new RpcBindAckPdu
        {
            CallId = b.CallId,
            MaxXmitFrag = 5840,
            MaxRecvFrag = 5840,
            Results = [new RpcContextResultElement(RpcContextResult.Acceptance, 0, other)],
        });
        await using (client)
        await using (server)
            await Assert.ThrowsAsync<RpcProtocolException>(() => client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Alter_context_continues_the_numbering_and_survives_a_rejection()
    {
        var reject = false;
        var (client, server) = Create(configureServer: s => s.BindOverride = b =>
            reject && b.Type == RpcPduType.AlterContext
                ? new RpcBindAckPdu
                {
                    PduType = RpcPduType.AlterContextResponse,
                    CallId = b.CallId,
                    Results = [new RpcContextResultElement(RpcContextResult.ProviderRejection, 1, default)],
                }
                : null);
        await using (client)
        await using (server)
        {
            var first = await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken);
            var second = await client.AlterContextAsync([Activator], TestContext.Current.CancellationToken);
            Assert.Equal((ushort)1, Assert.Single(second).ContextId);
            Assert.Equal(0x1234u, Assert.Single(server.Binds, b => b.Type == RpcPduType.AlterContext).AssocGroupId);

            reject = true;
            await Assert.ThrowsAsync<RpcBindRejectedException>(() => client.AlterContextAsync([Other], TestContext.Current.CancellationToken));

            // The association is still fine, and both earlier contexts still work.
            Assert.Equal(new byte[] { 1 }, await client.CallAsync(first[0], 0, new byte[] { 1 }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 2 }, await client.CallAsync(second[0], 0, new byte[] { 2 }, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Alter_context_before_bind_and_second_bind_are_usage_errors()
    {
        var (client, server) = Create();
        await using (client)
        await using (server)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.AlterContextAsync([Exporter], TestContext.Current.CancellationToken));
            await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Call_sends_one_fragment_with_the_object_uuid_and_returns_the_response_stub()
    {
        var (client, server) = Create();
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            var ipid = Guid.NewGuid();
            var stub = Pattern(100);

            var response = await client.CallAsync(context, 5, stub, ipid, TestContext.Current.CancellationToken);

            Assert.Equal(stub, response);
            var request = Assert.Single(server.Requests);
            Assert.Equal((ushort)5, request.OpNum);
            Assert.Equal(context.ContextId, request.ContextId);
            Assert.Equal(ipid, request.ObjectUuid);
            Assert.Equal(100u, request.AllocHint);
            Assert.Equal(RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment | RpcPfcFlags.ObjectUuid, request.Flags);
        }
    }

    [Fact]
    public async Task Call_ids_increase_and_an_empty_stub_is_one_pdu()
    {
        var (client, server) = Create();
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            Assert.Empty(await client.CallAsync(context, 1, ReadOnlyMemory<byte>.Empty, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(await client.CallAsync(context, 2, ReadOnlyMemory<byte>.Empty, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(2, server.Requests.Count);
            Assert.Equal(server.Requests[0].CallId + 1, server.Requests[1].CallId);
            Assert.All(server.Requests, r => Assert.Equal(RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment, r.Flags));
            Assert.Null(server.Requests[0].ObjectUuid);
        }
    }

    [Fact]
    public async Task Large_requests_are_fragmented_and_large_responses_reassembled()
    {
        var (client, server) = Create(configureServer: s =>
        {
            s.MaxRecvFrag = 512;
            s.ResponseFragmentSize = 300;
        });
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            var stub = Pattern(5000);

            var response = await client.CallAsync(context, 3, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(stub, response);
            var fragments = server.Requests;
            Assert.True(fragments.Count > 10);
            Assert.Equal(RpcPfcFlags.FirstFragment, fragments[0].Flags);
            Assert.All(fragments[1..^1], f => Assert.Equal(RpcPfcFlags.None, f.Flags));
            Assert.Equal(RpcPfcFlags.LastFragment, fragments[^1].Flags);
            Assert.All(fragments[..^1], f => Assert.Equal(0, f.Stub.Length % 8));
            Assert.All(fragments, f => Assert.True(16 + 8 + f.Stub.Length <= 512));
            Assert.Equal(stub, FakeRpcServer.Join(fragments));
            Assert.Single(fragments.Select(f => f.CallId).Distinct());
            Assert.Equal(5000u, fragments[0].AllocHint);
        }
    }

    [Fact]
    public async Task Fault_surfaces_as_an_exception_and_leaves_the_connection_usable()
    {
        var fail = true;
        var (client, server) = Create();
        server.CallHandler = fragments => fail
            ? [new RpcFaultPdu { CallId = fragments[0].CallId, Status = RpcStatus.RpcProcnumOutOfRange, Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment | RpcPfcFlags.DidNotExecute }]
            : server.EchoResponses(fragments);
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];

            var ex = await Assert.ThrowsAsync<RpcFaultException>(() => client.CallAsync(context, 99, new byte[8], cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(RpcStatus.RpcProcnumOutOfRange, ex.Status);
            Assert.True(ex.DidNotExecute);
            Assert.Contains("RPC_S_PROCNUM_OUT_OF_RANGE", ex.Message, StringComparison.Ordinal);

            fail = false;
            Assert.Equal(new byte[] { 7 }, await client.CallAsync(context, 1, new byte[] { 7 }, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Dce_fault_codes_map_to_win32_errors()
    {
        var (client, server) = Create();
        server.CallHandler = fragments =>
            [new RpcFaultPdu { CallId = fragments[0].CallId, Status = RpcStatus.NcaUnknownInterface }];
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            var ex = await Assert.ThrowsAsync<RpcFaultException>(() => client.CallAsync(context, 0, new byte[4], cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(RpcStatus.NcaUnknownInterface, ex.Status);
            Assert.Equal(RpcStatus.RpcUnknownInterface, ex.Win32Status);
        }
    }

    [Fact]
    public async Task Fault_in_the_middle_of_a_fragmented_response_is_reported()
    {
        var (client, server) = Create();
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu { CallId = fragments[0].CallId, Flags = RpcPfcFlags.FirstFragment, Stub = new byte[16] },
            new RpcFaultPdu { CallId = fragments[0].CallId, Status = RpcStatus.AccessDenied },
        ];
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            var ex = await Assert.ThrowsAsync<RpcFaultException>(() => client.CallAsync(context, 0, new byte[4], cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(RpcStatus.AccessDenied, ex.Status);
        }
    }

    [Fact]
    public async Task Response_for_another_call_is_a_protocol_error_and_breaks_the_connection()
    {
        var (client, server) = Create();
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId + 100, Stub = new byte[4] }];
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            await Assert.ThrowsAsync<RpcProtocolException>(() => client.CallAsync(context, 0, new byte[4], cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync(context, 0, new byte[4], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Response_for_another_context_is_a_protocol_error()
    {
        var (client, server) = Create();
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = 9, Stub = new byte[4] }];
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            await Assert.ThrowsAsync<RpcProtocolException>(() => client.CallAsync(context, 0, new byte[4], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Responses_larger_than_the_limit_are_refused()
    {
        var (client, server) = Create(o => o.MaxResponseSize = 1000);
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            await Assert.ThrowsAsync<RpcProtocolException>(() => client.CallAsync(context, 0, new byte[2000], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Calls_need_a_bound_context_that_belongs_to_the_connection()
    {
        var (client, server) = Create();
        var (otherClient, otherServer) = Create();
        await using (client)
        await using (server)
        await using (otherClient)
        await using (otherServer)
        {
            var foreign = (await otherClient.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync(foreign, 0, new byte[1], cancellationToken: TestContext.Current.CancellationToken));

            await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => client.CallAsync(foreign, 0, new byte[1], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_call_that_takes_too_long_times_out()
    {
        var (client, server) = Create(o => o.Timeout = TimeSpan.FromMilliseconds(150));
        server.CallHandler = _ => null;
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
            await Assert.ThrowsAsync<TimeoutException>(() => client.CallAsync(context, 0, new byte[4], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Concurrent_calls_are_serialized_and_each_gets_its_own_response()
    {
        var (client, server) = Create(configureServer: s => s.ResponseFragmentSize = 64);
        await using (client)
        await using (server)
        {
            var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];

            var calls = Enumerable.Range(1, 20)
                .Select(i => client.CallAsync(context, (ushort)i, Pattern(i * 40)))
                .ToArray();
            var responses = await Task.WhenAll(calls);

            for (var i = 1; i <= 20; i++)
                Assert.Equal(Pattern(i * 40), responses[i - 1]);
        }
    }

    [Fact]
    public void Options_reject_degenerate_fragment_sizes()
    {
        var (clientSide, _) = LoopbackTransport.CreatePair();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RpcClient(clientSide, new RpcClientOptions { MaxXmitFrag = 64 }));
    }

    [Fact]
    public async Task Works_over_a_real_tcp_connection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serving = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await using var server = new FakeRpcServer(new StreamPduTransport(accepted.GetStream()))
            {
                MaxRecvFrag = 700,
                ResponseFragmentSize = 500,
            };
            server.Start();
            await server.Completion;
        }, TestContext.Current.CancellationToken);

        await using var client = await RpcClient.ConnectTcpAsync("127.0.0.1", port, cancellationToken: TestContext.Current.CancellationToken);
        var context = (await client.BindAsync([Exporter], cancellationToken: TestContext.Current.CancellationToken))[0];
        var stub = Pattern(3000);

        Assert.Equal(stub, await client.CallAsync(context, 1, stub, Guid.NewGuid(), TestContext.Current.CancellationToken));
        await client.DisposeAsync();
        await serving;
    }
}

using System.Threading.Channels;
using NWmi.Auth;
using NWmi.Auth.Ntlm;
using NWmi.Options;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Auth;
using NWmi.Transport;

namespace NWmi.Tests.Rpc;

/// <summary>An in-memory pair of transports: what one side sends, the other receives.</summary>
internal sealed class LoopbackTransport : IPduTransport
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;

    private LoopbackTransport(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (LoopbackTransport Client, LoopbackTransport Server) CreatePair()
    {
        var toServer = Channel.CreateUnbounded<byte[]>();
        var toClient = Channel.CreateUnbounded<byte[]>();
        return (new LoopbackTransport(toClient, toServer), new LoopbackTransport(toServer, toClient));
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default) =>
        _outgoing.Writer.WriteAsync(pdu.ToArray(), cancellationToken);

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new EndOfStreamException();
        }
    }

    public ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A scriptable stand-in for an RPC server. By default it accepts every context with NDR 2.0 and
/// echoes the reassembled request stub back, split into <see cref="ResponseFragmentSize"/>-byte fragments.
/// </summary>
internal sealed class FakeRpcServer : IAsyncDisposable
{
    private readonly IPduTransport _transport;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, List<RpcRequestPdu>> _pending = [];
    private Task? _loop;
    private Task _work = Task.CompletedTask;
    private RpcSecurity? _serverSecurity;
    private RpcAuthLevel? _pendingLevel;
    private bool _headerSigning;

    public FakeRpcServer(IPduTransport transport)
    {
        _transport = transport;
    }

    public ushort MaxXmitFrag { get; set; } = 5840;

    public ushort MaxRecvFrag { get; set; } = 5840;

    public uint AssocGroupId { get; set; } = 0x1234;

    public int ResponseFragmentSize { get; set; } = 4000;

    /// <summary>Replace to answer binds differently (e.g. bind_nak).</summary>
    public Func<RpcBindPdu, RpcPdu?>? BindOverride { get; set; }

    /// <summary>Called with the whole request once its last fragment has arrived. Return null to stay silent.</summary>
    public Func<List<RpcRequestPdu>, IEnumerable<RpcPdu>?>? CallHandler { get; set; }

    /// <summary>Set to require NTLM authentication. Requests made before a successful handshake get an access-denied fault.</summary>
    public NtlmTestServer? Ntlm { get; set; }

    /// <summary>Whether to agree to RPC header signing when the client offers it.</summary>
    public bool AgreeToHeaderSigning { get; set; } = true;

    /// <summary>Send responses without protection even though the connection is authenticated (a downgrade attempt).</summary>
    public bool UnprotectedResponses { get; set; }

    /// <summary>True after a client authenticated successfully.</summary>
    public bool Authenticated => _serverSecurity is not null;

    /// <summary>Set after the handshake: whether header signing was agreed.</summary>
    public bool HeaderSigningAgreed => _headerSigning;

    public List<RpcBindPdu> Binds { get; } = [];

    public List<RpcRequestPdu> Requests { get; } = [];

    /// <summary>Call ids of the orphaned PDUs received.</summary>
    public List<uint> Orphans { get; } = [];

    /// <summary>Waits for an orphaned PDU, which the server reads after the client has already moved on.</summary>
    public async Task<uint> WaitForOrphanAsync()
    {
        for (var i = 0; i < 250 && Orphans.Count == 0; i++)
            await Task.Delay(20);

        return Assert.Single(Orphans);
    }

    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>Completes when the client has disconnected or the server was disposed.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (EndOfStreamException)
            {
            }

        await _work;
        await _transport.DisposeAsync();
        _stop.Dispose();
    }

    public static byte[] Join(IEnumerable<RpcRequestPdu> fragments) =>
        [.. fragments.SelectMany(f => f.Stub)];

    public IEnumerable<RpcPdu> EchoResponses(List<RpcRequestPdu> fragments)
    {
        var stub = Join(fragments);
        var callId = fragments[0].CallId;
        var contextId = fragments[0].ContextId;
        var offset = 0;
        do
        {
            var length = Math.Min(ResponseFragmentSize, stub.Length - offset);
            var flags = RpcPfcFlags.None;
            if (offset == 0)
                flags |= RpcPfcFlags.FirstFragment;

            if (offset + length == stub.Length)
                flags |= RpcPfcFlags.LastFragment;

            yield return new RpcResponsePdu
            {
                CallId = callId,
                Flags = flags,
                ContextId = contextId,
                AllocHint = (uint)(stub.Length - offset),
                Stub = stub[offset..(offset + length)],
            };
            offset += length;
        }
        while (offset < stub.Length);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var raw = await _transport.ReceiveAsync(_stop.Token);
                if (_serverSecurity is { ProtectsMessages: true } security && raw[2] == (byte)RpcPduType.Request)
                {
                    var hasObject = (raw[3] & (byte)RpcPfcFlags.ObjectUuid) != 0;
                    RpcPduProtection.Unprotect(raw, RpcPdu.HeaderSize + 8 + (hasObject ? 16 : 0), security, _headerSigning);
                }

                var pdu = RpcPdu.Parse(raw);
                switch (pdu)
                {
                    case RpcBindPdu bind:
                        Binds.Add(bind);
                        await SendAsync(BindOverride?.Invoke(bind) ?? Accept(bind));
                        break;
                    case RpcAuth3Pdu auth3:
                        CompleteHandshake(auth3);
                        break;
                    case RpcRequestPdu request:
                        Requests.Add(request);
                        if (Ntlm is not null && _serverSecurity is null)
                        {
                            await SendAsync(new RpcFaultPdu { CallId = request.CallId, Status = RpcStatus.AccessDenied });
                            break;
                        }

                        // Handled off the read loop, one call at a time, so a cancel can arrive while a call is running.
                        _work = RunAfterAsync(_work, request);
                        break;
                    case RpcOtherPdu { Type: RpcPduType.Orphaned }:
                        Orphans.Add(pdu.CallId);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
        {
            // The client went away or the test is over.
        }
    }

    private async Task RunAfterAsync(Task previous, RpcRequestPdu request)
    {
        try
        {
            await Task.Yield(); // a handler that blocks must not hold up the read loop
            await previous;
            await HandleRequestAsync(request);
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
        {
            // The client went away or the test is over.
        }
    }

    private async Task HandleRequestAsync(RpcRequestPdu request)
    {
        if (!_pending.TryGetValue(request.CallId, out var fragments))
            _pending[request.CallId] = fragments = [];

        fragments.Add(request);
        if ((request.Flags & RpcPfcFlags.LastFragment) == 0)
            return;

        _pending.Remove(request.CallId);
        var responses = CallHandler is null ? EchoResponses(fragments) : CallHandler(fragments);
        if (responses is null)
            return;

        foreach (var response in responses)
            await SendAsync(response);
    }

    private void CompleteHandshake(RpcAuth3Pdu auth3)
    {
        if (Ntlm is null || auth3.Auth is null || _pendingLevel is null)
            return;

        try
        {
            Ntlm.ProcessAuthenticate(auth3.Auth.Value);
        }
        catch (SecurityProviderException)
        {
            return; // stays unauthenticated; requests will be refused
        }

        _serverSecurity = new RpcSecurity(new NtlmServerProvider(Ntlm.Session), _pendingLevel.Value)
        {
            ContextId = auth3.Auth.ContextId,
        };
    }

    private RpcBindAckPdu Accept(RpcBindPdu bind)
    {
        var results = bind.Contexts.Select(context =>
            context.TransferSyntaxes.Contains(RpcSyntaxId.Ndr20)
                ? new RpcContextResultElement(RpcContextResult.Acceptance, 0, RpcSyntaxId.Ndr20)
                : new RpcContextResultElement(RpcContextResult.NegotiateAck, 0, default)).ToList();

        var ack = new RpcBindAckPdu
        {
            PduType = bind.Type == RpcPduType.Bind ? RpcPduType.BindAck : RpcPduType.AlterContextResponse,
            CallId = bind.CallId,
            MaxXmitFrag = MaxXmitFrag,
            MaxRecvFrag = MaxRecvFrag,
            AssocGroupId = AssocGroupId,
            SecondaryAddress = "135",
            Results = results,
        };

        if (Ntlm is not null && bind.Type == RpcPduType.Bind && bind.Auth is { } auth)
        {
            var challenge = Ntlm.ProcessNegotiate(auth.Value);
            ack.Auth = new RpcAuthTrailer(auth.AuthType, auth.AuthLevel, auth.ContextId, challenge);
            _pendingLevel = auth.AuthLevel;
            var offered = (bind.Flags & RpcPfcFlags.PendingCancelOrSupportHeaderSign) != 0;
            _headerSigning = offered && AgreeToHeaderSigning && _pendingLevel >= RpcAuthLevel.PacketIntegrity;
            if (_headerSigning)
                ack.Flags |= RpcPfcFlags.PendingCancelOrSupportHeaderSign;
        }

        return ack;
    }

    private ValueTask SendAsync(RpcPdu pdu) => _transport.SendAsync(SerializeForSending(pdu), _stop.Token);

    private byte[] SerializeForSending(RpcPdu pdu)
    {
        if (pdu is RpcResponsePdu response && !UnprotectedResponses && _serverSecurity is { ProtectsMessages: true } security)
        {
            response.Auth = new RpcAuthTrailer(
                security.Provider.AuthType, security.Level, security.ContextId, new byte[security.Provider.SignatureSize]);
            var bytes = response.Serialize();
            RpcPduProtection.Protect(bytes, response.StubOffset, security, _headerSigning);
            return bytes;
        }

        return pdu.Serialize();
    }
}

/// <summary>Adapts the server end of an NTLM session to the provider interface so the PDU protection code can drive it.</summary>
internal sealed class NtlmServerProvider(NtlmSession? session) : IRpcSecurityProvider
{
    public RpcAuthType AuthType => NtlmSecurityProvider.MyAuthType;

    public string MechanismOid => NtlmSecurityProvider.NtlmMechanismOid;

    public int SignatureSize => NtlmSession.SignatureSize;

    public bool IsComplete => true;

    public ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, Span<byte> signature) =>
        session!.Protect(encryptBody, signedBefore, body, signedAfter, signature);

    public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature) =>
        session!.Unprotect(decryptBody, signedBefore, body, signedAfter, signature);

    public IRpcSecurityProvider CreateForNewConnection() => throw new NotSupportedException();
}

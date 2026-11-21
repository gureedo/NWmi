using NWmi.Rpc.Pdu;
using NWmi.Transport;

namespace NWmi.Rpc;

/// <summary>
/// An unauthenticated connection-oriented RPC client: bind, alter_context and (fragmented) calls over one
/// <see cref="IPduTransport"/>. Calls are made one at a time. After a transport or protocol failure the connection
/// is unusable and every later operation fails. A call cancelled or timed out while waiting for its response does
/// not break it: the client orphans the call and drops the connection, and the next operation binds the same contexts
/// on a new one. A client made without a reconnect function cannot do that, so it is unusable after such a call.
/// Never more than one call is outstanding, since Windows fails overlapping calls.
/// </summary>
internal sealed class RpcClient : IAsyncDisposable
{
    private static readonly TimeSpan OrphanSendLimit = TimeSpan.FromSeconds(2);

    private readonly RpcClientOptions _options;
    private readonly Func<CancellationToken, ValueTask<IPduTransport>>? _reconnect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ushort, RpcPresentationContext> _contexts = [];
    private readonly List<NegotiationStep> _steps = [];
    private IPduTransport _transport;
    private RpcSecurity? _securityTemplate;
    private bool _reconnectPending;
    private bool _transportDisposed;
    private Task<byte[]>? _pendingReceive;
    private Exception? _broken;
    private uint _callId;
    private ushort _nextContextId;
    private bool _bound;
    private RpcSecurity? _security;
    private bool _headerSigning;

    /// <param name="transport">The connection to use.</param>
    /// <param name="options">Timeouts and fragment sizes.</param>
    /// <param name="reconnect">
    /// Opens a new connection to the same server. A call that is cancelled or timed out and not ended by the server is
    /// orphaned and its connection dropped, and the next operation reconnects and binds the same contexts again, as
    /// Windows clients do. Without it, such a call leaves the client unusable.
    /// </param>
    public RpcClient(IPduTransport transport, RpcClientOptions? options = null,
        Func<CancellationToken, ValueTask<IPduTransport>>? reconnect = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _reconnect = reconnect;
        _options = options ?? new RpcClientOptions();
        _options.Validate();
        MaxSendFragment = _options.MaxXmitFrag;
    }

    public static async ValueTask<RpcClient> ConnectTcpAsync(
        string host, int port, RpcClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var transport = await StreamPduTransport.ConnectTcpAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new RpcClient(transport, options, ct => StreamPduTransport.ConnectTcpAsync(host, port, ct));
    }

    /// <summary>The server's association group, learned from the bind_ack.</summary>
    public uint AssocGroupId { get; private set; }

    /// <summary>Largest PDU this client sends, after negotiation with the server.</summary>
    public int MaxSendFragment { get; private set; }

    /// <summary>
    /// Starts the association: the first bind. Offers NDR 2.0 for every interface. With <paramref name="security"/>
    /// the bind also authenticates, and later requests and responses are protected as its level says.
    /// </summary>
    /// <remarks>
    /// Include every interface you will need: <see cref="AlterContextAsync"/> adds more, but on an authenticated
    /// connection it sends no authentication data of its own.
    /// </remarks>
    public Task<IReadOnlyList<RpcPresentationContext>> BindAsync(
        IReadOnlyList<RpcSyntaxId> interfaces, RpcSecurity? security = null,
        CancellationToken cancellationToken = default) =>
        NegotiateContextsAsync(interfaces, alter: false, security, cancellationToken);

    /// <summary>Adds interfaces to the existing association (alter_context).</summary>
    public Task<IReadOnlyList<RpcPresentationContext>> AlterContextAsync(
        IReadOnlyList<RpcSyntaxId> interfaces, CancellationToken cancellationToken = default) =>
        NegotiateContextsAsync(interfaces, alter: true, security: null, cancellationToken);

    /// <summary>
    /// Calls procedure <paramref name="opNum"/> and returns the response stub data. The request is fragmented to
    /// fit the negotiated size and the response is reassembled.
    /// </summary>
    /// <param name="context">A context returned by <see cref="BindAsync"/> or <see cref="AlterContextAsync"/>.</param>
    /// <param name="opNum">The procedure number within the interface.</param>
    /// <param name="stub">The NDR-encoded arguments.</param>
    /// <param name="objectUuid">Sent as the object UUID; DCOM uses it to carry the target interface pointer id.</param>
    /// <param name="cancellationToken">
    /// Stops the wait, never the sending: a request that has started goes out in full, so the connection stays usable. A call
    /// cancelled after that is orphaned and its connection dropped, and the next operation reconnects.
    /// </param>
    public async Task<byte[]> CallAsync(
        RpcPresentationContext context,
        ushort opNum,
        ReadOnlyMemory<byte> stub,
        Guid? objectUuid = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeToStop = false;
        return await RunExclusiveAsync(
            async (waitToken, exchangeToken) =>
            {
                safeToStop = true;
                waitToken.ThrowIfCancellationRequested();
                var callId = ++_callId;
                safeToStop = false;
                await SendRequestAsync(context, opNum, stub, objectUuid, callId, exchangeToken).ConfigureAwait(false);
                safeToStop = true;
                try
                {
                    // A cancellation that arrived while the send could not be stopped still counts: without this check
                    // the call would succeed or throw depending on whether the reply happened to be read first.
                    waitToken.ThrowIfCancellationRequested();
                    return await ReceiveResponseAsync(context, callId, waitToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Windows clients orphan the call and drop the connection, which frees the caller at once; the next
                    // operation reconnects.
                    await OrphanAndDropAsync(callId).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken,
            recoverable: ex => ex is RpcFaultException || (ex is OperationCanceledException && safeToStop),
            precondition: () => RequireContext(context)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _broken ??= new ObjectDisposedException(nameof(RpcClient));
        await DropTransportAsync().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RpcPresentationContext>> NegotiateContextsAsync(
        IReadOnlyList<RpcSyntaxId> interfaces, bool alter, RpcSecurity? security, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        if (interfaces.Count == 0)
            throw new ArgumentException("At least one interface is required.", nameof(interfaces));

        security = security?.ClaimForConnection();

        var safeToStop = false;
        return await RunExclusiveAsync(
            async (waitToken, exchangeToken) =>
            {
                // A context serves every object of its interface (the object UUID picks the object), so one is enough.
                if (alter && FindContexts(interfaces) is { } known)
                    return known;

                // An alter_context is quick, and stopping halfway would lose the connection: once it starts it runs to the end.
                // A bind has no session to save, so the caller can stop it at any point.
                var token = waitToken;
                if (alter)
                {
                    safeToStop = true;
                    waitToken.ThrowIfCancellationRequested();
                    safeToStop = false;
                    token = exchangeToken;
                }

                var offered = new List<RpcContextElement>();
                foreach (var iface in interfaces)
                    offered.Add(new RpcContextElement(_nextContextId++, iface, [RpcSyntaxId.Ndr20]));

                var featureElementOffered = false;
                if (!alter && _options.OfferBindTimeFeatureNegotiation)
                {
                    // Same abstract syntax as the previous element, one transfer syntax: the feature marker.
                    offered.Add(new RpcContextElement(_nextContextId++, interfaces[^1],
                        [RpcSyntaxId.FeatureNegotiation]));
                    featureElementOffered = true;
                }

                var step = new NegotiationStep(alter, offered, interfaces.Count, featureElementOffered);
                var accepted = await ExchangeContextsAsync(step, security, token).ConfigureAwait(false);

                // Kept so a new connection can bind the same contexts again, under the same ids.
                _steps.Add(step);
                if (!alter)
                    _securityTemplate = security;

                return accepted;
            },
            cancellationToken,
            // A failed bind stays fatal even for a fault: an unfinished handshake would otherwise leave a bound
            // connection with no security, whose later calls would go out unprotected.
            recoverable: ex => alter && (ex is RpcBindRejectedException or RpcFaultException ||
                                         (ex is OperationCanceledException && safeToStop)),
            precondition: () =>
            {
                if (alter && !_bound)
                    throw new InvalidOperationException("Call BindAsync before AlterContextAsync.");

                if (!alter && _bound)
                    throw new InvalidOperationException("The connection is already bound; use AlterContextAsync.");
            }).ConfigureAwait(false);
    }

    /// <summary>One bind or alter_context that succeeded, with the context ids it was given.</summary>
    private sealed record NegotiationStep(
        bool Alter,
        List<RpcContextElement> Offered,
        int InterfaceCount,
        bool FeatureElementOffered);

    private async Task<List<RpcPresentationContext>> ExchangeContextsAsync(
        NegotiationStep step, RpcSecurity? security, CancellationToken token)
    {
        var (alter, offered, interfaceCount, featureElementOffered) = step;
        var callId = ++_callId;
        var request = new RpcBindPdu
        {
            PduType = alter ? RpcPduType.AlterContext : RpcPduType.Bind,
            CallId = callId,
            MaxXmitFrag = _options.MaxXmitFrag,
            MaxRecvFrag = _options.MaxRecvFrag,
            AssocGroupId = alter ? AssocGroupId : 0,
            Contexts = offered,
        };

        if (security is not null)
        {
            var initialToken = await security.Provider.GetInitialTokenAsync(security.Protection, token)
                .ConfigureAwait(false);
            request.Auth = new RpcAuthTrailer(security.Provider.AuthType, security.Level, security.ContextId,
                initialToken);
            if (security.OffersHeaderSigning)
                request.Flags |= RpcPfcFlags.PendingCancelOrSupportHeaderSign;
        }

        await _transport.SendAsync(request.Serialize(), token).ConfigureAwait(false);
        var reply = await ReceiveAsync(token).ConfigureAwait(false);
        var expected = alter ? RpcPduType.AlterContextResponse : RpcPduType.BindAck;

        switch (reply)
        {
            case RpcBindNakPdu nak:
                throw new RpcBindRejectedException(
                    $"The server rejected the {(alter ? "alter_context" : "bind")}: {nak.Reason}.");
            case RpcFaultPdu fault:
                throw new RpcFaultException(fault.Status, (fault.Flags & RpcPfcFlags.DidNotExecute) != 0,
                    fault.ExtendedErrorInfo);
            case RpcBindAckPdu ack when ack.Type == expected && ack.CallId == callId:
            {
                var accepted = Accept(ack, offered, interfaceCount, featureElementOffered, alter);
                if (security is not null)
                    await CompleteAuthenticationAsync(security, ack, callId, offered, token)
                        .ConfigureAwait(false);

                return accepted;
            }
            default:
                throw new RpcProtocolException(
                    $"Expected {expected} for call {callId} but received {reply.Type} for call {reply.CallId}.");
        }
    }

    /// <summary>
    /// Opens a new connection after the previous one was dropped, and replays every bind and alter_context that
    /// succeeded on it, so the contexts the callers hold stay valid. The new connection is a new association.
    /// </summary>
    private async Task ReconnectAsync(CancellationToken token)
    {
        var transport = await _reconnect!(token).ConfigureAwait(false);
        _transport = transport;
        _transportDisposed = false;
        _pendingReceive = null;
        _security = null;
        _headerSigning = false;
        try
        {
            foreach (var step in _steps)
                await ExchangeContextsAsync(step, step.Alter ? null : _securityTemplate?.ClaimForConnection(), token)
                    .ConfigureAwait(false);
        }
        catch
        {
            // Leaves nothing half bound: the next operation starts the reconnection over.
            await DropTransportAsync().ConfigureAwait(false);
            throw;
        }

        _reconnectPending = false;
    }

    private async Task DropTransportAsync()
    {
        if (_pendingReceive is { } pending)
        {
            // Its failure is expected once the transport goes away.
            _ = pending.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _pendingReceive = null;
        }

        if (_transportDisposed)
            return;

        _transportDisposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes the handshake that the bind started. The provider's reply to the server's token goes in an auth3 PDU
    /// (which the server does not answer) when it completes the handshake; when the provider needs the server's
    /// answer to it, the reply goes in an alter_context PDU instead and the answer comes back in the alter_context_resp
    /// (MS-RPCE 3.3.1.5.2.1), as many times as the provider asks.
    /// </summary>
    private async Task CompleteAuthenticationAsync(RpcSecurity security, RpcBindAckPdu ack, uint bindCallId,
        List<RpcContextElement> offered, CancellationToken token)
    {
        var provider = security.Provider;
        if (ack.Auth is null)
            throw new RpcProtocolException("The server accepted the bind but did not take part in authentication.");

        var reply = await provider.ProcessServerTokenAsync(ack.Auth.Value, token).ConfigureAwait(false);
        var lastFlags = ack.Flags;
        while (reply is not null)
        {
            var trailer = new RpcAuthTrailer(provider.AuthType, security.Level, security.ContextId, reply);
            if (provider.IsComplete)
            {
                var auth3 = new RpcAuth3Pdu { CallId = bindCallId, Auth = trailer };
                await _transport.SendAsync(auth3.Serialize(), token).ConfigureAwait(false);
                break;
            }

            // The context list SHOULD repeat the previous leg's (MS-RPCE 3.3.1.5.2.1).
            var callId = ++_callId;
            var alter = new RpcBindPdu
            {
                PduType = RpcPduType.AlterContext,
                CallId = callId,
                MaxXmitFrag = _options.MaxXmitFrag,
                MaxRecvFrag = _options.MaxRecvFrag,
                AssocGroupId = AssocGroupId,
                Contexts = offered,
                Auth = trailer,
            };
            if (security.OffersHeaderSigning)
                // Repeat the bind's header-signing offer in each leg.
                alter.Flags |= RpcPfcFlags.PendingCancelOrSupportHeaderSign;

            await _transport.SendAsync(alter.Serialize(), token).ConfigureAwait(false);
            switch (await ReceiveAsync(token).ConfigureAwait(false))
            {
                case RpcBindNakPdu nak:
                    throw new RpcBindRejectedException($"The server rejected an authentication leg: {nak.Reason}.");
                case RpcFaultPdu fault:
                    throw new RpcFaultException(fault.Status, (fault.Flags & RpcPfcFlags.DidNotExecute) != 0,
                        fault.ExtendedErrorInfo);
                case RpcBindAckPdu { Type: RpcPduType.AlterContextResponse } next when next.CallId == callId:
                    if (next.Auth is null)
                        throw new RpcProtocolException("The server answered an authentication leg without a token.");

                    reply = await provider.ProcessServerTokenAsync(next.Auth.Value, token).ConfigureAwait(false);
                    lastFlags = next.Flags;
                    break;
                case var other:
                    throw new RpcProtocolException(
                        $"Expected an alter_context_resp for call {callId} but received {other.Type} for call {other.CallId}.");
            }
        }

        if (!provider.IsComplete)
            throw new RpcProtocolException(
                "The security provider has no further token to send but its handshake is not complete.");

        _security = security;
        _headerSigning = security.OffersHeaderSigning
                         && (lastFlags & RpcPfcFlags.PendingCancelOrSupportHeaderSign) != 0;
    }

    private List<RpcPresentationContext> Accept(
        RpcBindAckPdu ack, List<RpcContextElement> offered, int interfaceCount, bool featureElementOffered, bool alter)
    {
        if (ack.Results.Count != offered.Count)
            throw new RpcProtocolException(
                $"The server answered {ack.Results.Count} contexts but {offered.Count} were offered.");

        var accepted = new List<RpcPresentationContext>(interfaceCount);
        for (var i = 0; i < interfaceCount; i++)
        {
            var element = offered[i];
            var result = ack.Results[i];
            if (result.Result != RpcContextResult.Acceptance)
            {
                var reason = result.Result == RpcContextResult.ProviderRejection
                    ? (RpcContextRejectReason)result.Reason
                    : (object)result.Reason;
                throw new RpcBindRejectedException(
                    $"The server did not accept interface {element.AbstractSyntax}: {result.Result} ({reason}).");
            }

            if (result.TransferSyntax != RpcSyntaxId.Ndr20)
                throw new RpcProtocolException(
                    $"The server chose transfer syntax {result.TransferSyntax} instead of NDR 2.0.");

            // A context bound again on a new connection stays the object the callers already hold.
            accepted.Add(_contexts.TryGetValue(element.ContextId, out var existing)
                         && existing.Interface == element.AbstractSyntax
                ? existing
                : new RpcPresentationContext(element.ContextId, element.AbstractSyntax, result.TransferSyntax));
        }

        _ = featureElementOffered; // its answer (negotiate_ack or a rejection) needs no action here

        if (!alter)
        {
            AssocGroupId = ack.AssocGroupId;
            MaxSendFragment = Math.Min(_options.MaxXmitFrag, (int)ack.MaxRecvFrag);
            if (MaxSendFragment < 128)
                throw new RpcProtocolException($"The server accepts fragments of only {ack.MaxRecvFrag} bytes.");

            _bound = true;
        }

        foreach (var context in accepted)
            _contexts[context.ContextId] = context;

        return accepted;
    }

    private async Task SendRequestAsync(
        RpcPresentationContext context, ushort opNum, ReadOnlyMemory<byte> stub, Guid? objectUuid, uint callId,
        CancellationToken token)
    {
        // Every non-final fragment carries a multiple of 8 bytes of stub, so the NDR stream keeps its alignment.
        // A protected request also holds the sec_trailer, the signature and up to 15 bytes of alignment padding.
        var protect = _security is { ProtectsMessages: true };
        var trailerOverhead = protect ? 8 + _security!.Provider.SignatureSize + (RpcPdu.AuthAlignment - 1) : 0;
        var overhead = RpcPdu.HeaderSize + 8 + (objectUuid is null ? 0 : 16) + trailerOverhead;
        var chunkSize = (MaxSendFragment - overhead) & ~7;
        if (chunkSize <= 0)
            throw new RpcProtocolException(
                $"The negotiated fragment size of {MaxSendFragment} bytes is too small for protected calls.");

        var offset = 0;
        do
        {
            var length = Math.Min(chunkSize, stub.Length - offset);
            var flags = RpcPfcFlags.None;
            if (offset == 0)
                flags |= RpcPfcFlags.FirstFragment;

            if (offset + length == stub.Length)
                flags |= RpcPfcFlags.LastFragment;

            var pdu = new RpcRequestPdu
            {
                CallId = callId,
                Flags = flags,
                AllocHint = (uint)(stub.Length - offset),
                ContextId = context.ContextId,
                OpNum = opNum,
                ObjectUuid = objectUuid,
                Stub = stub.Slice(offset, length).ToArray(),
            };

            byte[] bytes;
            if (protect)
            {
                var security = _security!;
                pdu.Auth = new RpcAuthTrailer(
                    security.Provider.AuthType, security.Level, security.ContextId,
                    new byte[security.Provider.SignatureSize]);
                bytes = pdu.Serialize();
                RpcPduProtection.Protect(bytes, pdu.StubOffset, security, _headerSigning);
            }
            else
                bytes = pdu.Serialize();

            await _transport.SendAsync(bytes, token).ConfigureAwait(false);
            offset += length;
        } while (offset < stub.Length);
    }

    private async Task<byte[]> ReceiveResponseAsync(RpcPresentationContext context, uint callId,
        CancellationToken token)
    {
        var result = new MemoryStream();
        while (true)
        {
            var pdu = await ReceiveAsync(token).ConfigureAwait(false);
            if (pdu.CallId != callId)
                throw new RpcProtocolException(
                    $"Received a {pdu.Type} for call {pdu.CallId} while waiting for call {callId}.");

            switch (pdu)
            {
                case RpcFaultPdu fault:
                    throw new RpcFaultException(fault.Status, (fault.Flags & RpcPfcFlags.DidNotExecute) != 0,
                        fault.ExtendedErrorInfo);
                case RpcResponsePdu response:
                    if (response.ContextId != context.ContextId)
                        throw new RpcProtocolException(
                            $"Response for context {response.ContextId}, expected {context.ContextId}.");

                    if (result.Length + response.Stub.Length > _options.MaxResponseSize)
                        throw new RpcProtocolException(
                            $"The response exceeds the {_options.MaxResponseSize}-byte limit.");

                    result.Write(response.Stub);
                    if ((response.Flags & RpcPfcFlags.LastFragment) != 0)
                        return result.ToArray();

                    break;
                default:
                    throw new RpcProtocolException(
                        $"Unexpected {pdu.Type} PDU while waiting for the response to call {callId}.");
            }
        }
    }

    /// <summary>
    /// The next PDU. The read itself is not cancelled, only the wait for it: a read stopped halfway through a PDU would
    /// lose the framing, so it stays pending for the next caller instead.
    /// </summary>
    private async Task<RpcPdu> ReceiveAsync(CancellationToken token)
    {
        _pendingReceive ??= _transport.ReceiveAsync(CancellationToken.None).AsTask();
        var bytes = await _pendingReceive.WaitAsync(token).ConfigureAwait(false);
        _pendingReceive = null;

        // Responses on a protected connection must carry a valid signature; faults and bind replies are not signed.
        if (_security is { ProtectsMessages: true } security && bytes.Length >= RpcPdu.HeaderSize &&
            bytes[2] == (byte)RpcPduType.Response)
            RpcPduProtection.Unprotect(bytes, RpcPdu.HeaderSize + 8, security, _headerSigning);

        return RpcPdu.Parse(bytes);
    }

    /// <summary>
    /// Gives up on the abandoned call: tells the server with an orphaned PDU, as Windows clients do, and drops the connection.
    /// The next operation opens a new one and binds the same contexts again. Windows services ignore a cancel request for a
    /// call that is blocked, so none is sent first.
    /// </summary>
    private async Task OrphanAndDropAsync(uint callId)
    {
        var orphaned = new RpcOtherPdu(RpcPduType.Orphaned, [])
        {
            CallId = callId,
            Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment,
        };

        try
        {
            using var sendLimit = new CancellationTokenSource(OrphanSendLimit);
            await _transport.SendAsync(orphaned.Serialize(), sendLimit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is being dropped anyway.
        }

        await DropTransportAsync().ConfigureAwait(false);
        if (_reconnect is null)
            _broken = new InvalidOperationException("A call was given up on and the connection cannot be reopened.");
        else
            _reconnectPending = true;
    }

    /// <summary>The contexts already negotiated for <paramref name="interfaces"/>, or null unless every one has one.</summary>
    private List<RpcPresentationContext>? FindContexts(IReadOnlyList<RpcSyntaxId> interfaces)
    {
        var found = new List<RpcPresentationContext>(interfaces.Count);
        foreach (var iface in interfaces)
        {
            var context = _contexts.Values.FirstOrDefault(c => c.Interface == iface);
            if (context is null)
                return null;

            found.Add(context);
        }

        return found;
    }

    private void RequireContext(RpcPresentationContext context)
    {
        if (!_bound)
            throw new InvalidOperationException("Call BindAsync first.");

        if (!_contexts.TryGetValue(context.ContextId, out var known) || !ReferenceEquals(known, context))
            throw new ArgumentException("The context does not belong to this connection.", nameof(context));
    }

    /// <summary>
    /// Runs one exchange with the connection to itself. <paramref name="precondition"/> checks API usage under the
    /// lock but before any I/O, so a mistake by the caller does not poison the connection. <paramref name="operation"/> gets two
    /// tokens: the first ends on the caller's cancellation or the timeout, the second on the timeout alone, for work that
    /// cannot stop halfway, such as a send.
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        Func<Exception, bool>? recoverable = null,
        Action? precondition = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_broken is not null)
                throw new InvalidOperationException("The RPC connection is no longer usable.", _broken);

            precondition?.Invoke();

            using var timeout = new CancellationTokenSource(_options.Timeout);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var reconnecting = false;
            try
            {
                if (_reconnectPending)
                {
                    // A failed reconnection leaves nothing bound, so the next operation tries again.
                    reconnecting = true;
                    await ReconnectAsync(wait.Token).ConfigureAwait(false);
                    reconnecting = false;
                }

                return await operation(wait.Token, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException cancelled) when (!cancellationToken.IsCancellationRequested)
            {
                var ex = new TimeoutException($"The RPC operation did not complete within {_options.Timeout}.");
                if (!reconnecting && (recoverable is null || !recoverable(cancelled)))
                    _broken = ex;

                throw ex;
            }
            catch (Exception ex) when (!reconnecting && (recoverable is null || !recoverable(ex)))
            {
                // A failed exchange leaves the stream in an unknown state, so it cannot be reused.
                _broken = ex;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

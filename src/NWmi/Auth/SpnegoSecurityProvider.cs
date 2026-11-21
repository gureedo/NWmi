using System.Formats.Asn1;
using System.Security.Cryptography;
using Kerberos.NET.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Auth.Kerberos;
using NWmi.Rpc;

namespace NWmi.Auth;

/// <summary>
/// SPNEGO (RFC 4178, MS-SPNG) as RPC_C_AUTHN_GSS_NEGOTIATE, client side. It owns the credentials and offers
/// Kerberos (when a ticket could be obtained) and NTLM, in that order, sending Kerberos's AP-REQ as the optimistic
/// mechToken. Everything happens on the one RPC connection: if the server accepts the optimistic mechanism the
/// handshake takes the usual two legs; if it picks another one, the optimistic token is dropped and that
/// mechanism's handshake runs in further legs (<see cref="RpcClient"/> carries them in alter_context PDUs), ending
/// with the mechListMIC that RFC 4178 §5 asks for when the mechanism was not the first choice.
/// </summary>
internal sealed class SpnegoSecurityProvider : IRpcSecurityProvider
{
    public const RpcAuthType MyAuthType = RpcAuthType.GssNegotiate;
    public const string SpnegoOid = "1.3.6.1.5.5.2";

    private static readonly Asn1Tag NegTokenRespTag = new(TagClass.ContextSpecific, 1);

    private readonly IReadOnlyList<ISpnegoMechanism> _mechanisms;
    private readonly ILogger _logger;
    private ISpnegoMechanism? _active;
    private byte[]? _mechTypes;
    private AuthProtection _protection;
    private bool _awaitingFinal;

    public RpcAuthType AuthType => MyAuthType;

    /// <summary>SPNEGO wrapping SPNEGO is not a thing.</summary>
    public string? MechanismOid => null;

    /// <summary>Until the server has chosen, the largest any offered mechanism needs.</summary>
    public int SignatureSize => IsComplete ? _active!.SignatureSize : _mechanisms.Max(m => m.SignatureSize);

    public bool IsComplete { get; private set; }

    /// <summary>NTLM only, with no round trip needed to set it up.</summary>
    public SpnegoSecurityProvider(Credential credential, NtlmSecurityOptions? ntlm = null)
        : this([new NtlmSecurityProvider(credential, ntlm)], null)
    {
    }

    internal SpnegoSecurityProvider(IReadOnlyList<ISpnegoMechanism> mechanisms, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(mechanisms);
        if (mechanisms.Count == 0)
            throw new ArgumentException("At least one mechanism is required.", nameof(mechanisms));

        _mechanisms = mechanisms;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Builds a provider that offers Kerberos first when a ticket can be had (the AS and TGS exchanges run here, because a
    /// bind cannot wait on the KDC), and NTLM after it. Without <paramref name="host"/> the options must state the SPN, or
    /// Kerberos is not offered.
    /// </summary>
    public static async Task<SpnegoSecurityProvider> CreateAsync(
        string? host, Credential credential, SpnegoOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        options ??= new SpnegoOptions();
        var logger = options.LoggerFactory?.CreateLogger<SpnegoSecurityProvider>() ?? (ILogger)NullLogger.Instance;
        var mechanisms = new List<ISpnegoMechanism>();
        var target = await KerberosDiscovery.ResolveAsync(host, credential, options, cancellationToken, logger: logger)
            .ConfigureAwait(false);
        if (target is not null)
        {
            var kerberosCredential = new KerberosCredential(credential.UserName, credential.Password, target.Realm);
            try
            {
                mechanisms.Add(await KerberosSecurityProvider.NegotiateAsync(
                        kerberosCredential, target.Kdcs, target.ServicePrincipalName, options.LoggerFactory,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false));
                logger.LogDebug("Kerberos: ticket for {Spn} ({Realm}).", target.ServicePrincipalName, target.Realm);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("No Kerberos ticket from {Kdcs} ({ExceptionType}: {Reason}).",
                        string.Join(", ", target.Kdcs.Select(k => k.Host)), ex.GetType().Name, ex.Message);
            }
        }

        mechanisms.Add(new NtlmSecurityProvider(credential, options.Ntlm));
        return new SpnegoSecurityProvider(mechanisms, logger);
    }

    public async ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection,
        CancellationToken cancellationToken = default)
    {
        _protection = protection;
        IsComplete = false;
        _active = _mechanisms[0];
        _mechTypes = EncodeMechTypes();
        var mechToken = await _active.GetInitialMechTokenAsync(protection, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("SPNEGO: offering {Mechanisms}; optimistic {Optimistic}.",
                string.Join(", ", _mechanisms.Select(m => m.MechanismOid)), _active.MechanismOid);
        return WrapInit(mechToken);
    }

    public async ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default)
    {
        if (_active is null || _mechTypes is null || IsComplete)
            throw new InvalidOperationException(
                "Unexpected token: the SPNEGO handshake is not waiting for a server token.");

        var response = ParseResponse(token.Span);
        _logger.LogDebug("SPNEGO: server sent {State}, mech {Mechanism}, token {TokenLength}, mic {MicLength}.",
            response.State, response.SupportedMech ?? "-", response.ResponseToken?.Length,
            response.MechListMic?.Length);
        if (response.State == NegState.Reject)
            throw new SecurityProviderException("The server rejected the SPNEGO negotiation.");

        if (_awaitingFinal)
        {
            // The server's last word, to our last token: it completes the context on its side and answers our MIC.
            if (response.State != NegState.AcceptCompleted)
                throw new SecurityProviderException("The server did not complete the SPNEGO negotiation.");

            if (response.MechListMic is null)
                throw new SecurityProviderException(
                    "The server completed the SPNEGO negotiation without a mechListMIC.");

            _active.VerifyMechListMic(_mechTypes, response.MechListMic);
            _awaitingFinal = false;
            IsComplete = true;
            _logger.LogDebug("SPNEGO: {Mechanism} complete; server mechListMIC verified.", _active.MechanismOid);
            return null;
        }

        byte[]? mechToken;
        if (response.SupportedMech is { } chosen && chosen != _active.MechanismOid)
        {
            // The server did not take the optimistic mechanism, so its token is dead: start the chosen one fresh.
            var next = _mechanisms.FirstOrDefault(m => m.MechanismOid == chosen)
                       ?? throw new SecurityProviderException($"The server chose the unoffered mechanism {chosen}.");
            if (response.ResponseToken is not null)
                throw new SecurityProviderException($"The server answered {chosen} before it had a token to answer.");

            _logger.LogDebug("SPNEGO: server chose {Chosen} instead of {Active}; switching.", chosen,
                _active.MechanismOid);
            _active = next;
            mechToken = await _active.GetInitialMechTokenAsync(_protection, cancellationToken).ConfigureAwait(false);
        }
        else
            mechToken = response.ResponseToken is null ? null : _active.ProcessMechToken(response.ResponseToken);

        byte[]? mic = null;
        if (_active.SupportsMechListMic)
        {
            if (response.MechListMic is not null)
                _active.VerifyMechListMic(_mechTypes, response.MechListMic);

            // MS-SPNG 3.1.5.1: an NTLM AUTHENTICATE with a MIC makes the mechListMIC mandatory; RFC 4178 §5 also asks
            // for it whenever the mechanism was not the initiator's first. It travels with the last token.
            if (_active.IsComplete && mechToken is not null)
                mic = _active.CreateMechListMic(_mechTypes);
        }

        // A MIC we send is answered by the server's own in a final NegTokenResp, which only an alter_context leg
        // (not auth3) brings back (MS-RPCE 3.3.1.5.2.1: unless the leg count is known to be odd, assume even).
        _awaitingFinal = mic is not null;
        IsComplete = _active.IsComplete && !_awaitingFinal;
        _logger.LogDebug("SPNEGO: {Mechanism} {Progress}; replying with {TokenLength} token bytes{WithMic}.",
            _active.MechanismOid, IsComplete ? "complete" : "continues", mechToken?.Length ?? 0,
            mic is null ? string.Empty : " and a mechListMIC");
        return mechToken is null && mic is null ? null : WrapResponse(mechToken, mic);
    }

    public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body,
        ReadOnlySpan<byte> signedAfter, Span<byte> signature) =>
        RequireDone().Protect(encryptBody, signedBefore, body, signedAfter, signature);

    public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body,
        ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature) =>
        RequireDone().Unprotect(decryptBody, signedBefore, body, signedAfter, signature);

    public IRpcSecurityProvider CreateForNewConnection() =>
        new SpnegoSecurityProvider([.. _mechanisms.Select(m => m.CreateForNewConnection())], _logger);

    private ISpnegoMechanism RequireDone() =>
        IsComplete ? _active! : throw new InvalidOperationException("The SPNEGO handshake has not finished.");

    /// <summary>The DER SEQUENCE OF OID that the mechListMIC covers (RFC 4178 §5), exactly as sent in the NegTokenInit.</summary>
    private byte[] EncodeMechTypes()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
            foreach (var mechanism in _mechanisms)
                writer.WriteObjectIdentifier(mechanism.MechanismOid!);

        return writer.Encode();
    }

    /// <summary>[APPLICATION 0] { SPNEGO OID, [0] NegTokenInit { [0] mechTypes, [2] mechToken } }.</summary>
    private byte[] WrapInit(byte[] mechToken)
    {
        var init = new NegTokenInit
        {
            MechTypes = [.. _mechanisms.Select(m => new Oid(m.MechanismOid!))],
            MechToken = mechToken,
        };
        return GssApiToken.Encode(new Oid(SpnegoOid), new NegotiationToken { InitialToken = init }).ToArray();
    }

    /// <summary>[1] NegTokenResp { [2] responseToken, [3] mechListMIC }, either part optional.</summary>
    private static byte[] WrapResponse(byte[]? responseToken, byte[]? mechListMic) =>
        new NegotiationToken
        {
            ResponseToken = new NegTokenResp
            {
                ResponseToken = responseToken,
                MechListMic = mechListMic,
            },
        }.Encode().ToArray();

    private sealed record Response(NegState State, string? SupportedMech, byte[]? ResponseToken, byte[]? MechListMic);

    /// <summary>
    /// Reads a NegTokenResp by hand: Kerberos.NET's own decoder reads <c>negState</c> as an INTEGER where RFC 4178
    /// (and Windows) use ENUMERATED, and knows no <c>request-mic</c> state.
    /// </summary>
    private static Response ParseResponse(ReadOnlySpan<byte> token)
    {
        try
        {
            var outer = new AsnReader(token.ToArray(), AsnEncodingRules.DER);
            if (!outer.PeekTag().HasSameClassAndValue(NegTokenRespTag))
                throw new SecurityProviderException("The server's SPNEGO token is not a NegTokenResp.");

            var reader = outer.ReadSequence(NegTokenRespTag).ReadSequence();
            string? supportedMech = null;
            byte[]? responseToken = null;
            byte[]? mechListMic = null;
            var state = NegState.AcceptIncomplete;
            while (reader.HasData)
            {
                var tag = reader.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific)
                    throw new SecurityProviderException("Unexpected element in the server's SPNEGO token.");

                var field = reader.ReadSequence(tag);
                switch (tag.TagValue)
                {
                    case 0:
                        state = field.ReadEnumeratedValue<NegState>();
                        break;
                    case 1:
                        supportedMech = field.ReadObjectIdentifier();
                        break;
                    case 2:
                        responseToken = field.ReadOctetString();
                        break;
                    case 3:
                        mechListMic = field.ReadOctetString();
                        break;
                }
            }

            return new Response(state, supportedMech, responseToken, mechListMic);
        }
        catch (AsnContentException ex)
        {
            throw new SecurityProviderException("The server's SPNEGO token is malformed.", ex);
        }
    }

    internal enum NegState
    {
        AcceptCompleted = 0,
        AcceptIncomplete = 1,
        Reject = 2,
        RequestMic = 3,
    }
}

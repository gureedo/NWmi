using Kerberos.NET;
using Kerberos.NET.Client;
using Kerberos.NET.Configuration;
using Kerberos.NET.Credentials;
using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;
using Kerberos.NET.Transport;
using Kerberos.NET.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Auth.Kerberos;
using NWmi.Rpc;

namespace NWmi.Auth;

/// <summary>
/// Kerberos (MS-KILE) as an RPC security provider. The KDC exchanges (AS and TGS, over TCP) and the AP-REQ are the
/// Kerberos.NET package's; the per-message protection (RFC 4121 Wrap and MIC tokens, AES128/256) is this library's own.
/// Unlike <see cref="NtlmSecurityProvider"/>, the handshake needs no server round trip over the RPC connection:
/// everything the KDC has to say happens before the bind, in <see cref="NegotiateAsync"/>, because
/// <see cref="IRpcSecurityProvider.GetInitialTokenAsync"/> then only builds an AP-REQ with a fresh authenticator from the
/// service ticket, asking the KDC again only if the cached one is no longer usable.
/// </summary>
internal sealed class KerberosSecurityProvider : ISpnegoMechanism
{
    public const RpcAuthType MyAuthType = RpcAuthType.GssKerberos;

    /// <summary>The Kerberos V5 GSS-API mechanism OID (RFC 1964/4121), used when wrapped in SPNEGO.</summary>
    public const string Krb5MechanismOid = "1.2.840.113554.1.2.2";

    private readonly KerberosClient _client;
    private readonly string _servicePrincipalName;
    private readonly ILogger _logger;
    private ApplicationSessionContext _context;
    private Rfc4121AesSession? _session;
    private AuthProtection _protection = AuthProtection.Privacy;

    public RpcAuthType AuthType => MyAuthType;

    public string MechanismOid => Krb5MechanismOid;

    public int SignatureSize => _protection == AuthProtection.Privacy
        ? Rfc4121AesSession.SignatureSize
        : Rfc4121AesSession.UnsealedSignatureSize;

    public bool IsComplete { get; private set; }

    private KerberosSecurityProvider(KerberosClient client, string servicePrincipalName,
        ApplicationSessionContext context, ILogger logger)
    {
        _client = client;
        _servicePrincipalName = servicePrincipalName;
        _context = context;
        _logger = logger;
    }

    /// <summary>Reuses the cached service ticket; each connection still sends its own AP-REQ with a fresh authenticator.</summary>
    public IRpcSecurityProvider CreateForNewConnection() =>
        new KerberosSecurityProvider(_client, _servicePrincipalName, _context, _logger);

    ValueTask<byte[]> ISpnegoMechanism.GetInitialMechTokenAsync(AuthProtection protection,
        CancellationToken cancellationToken) =>
        GetInitialApReqAsync(protection, cancellationToken);

    byte[]? ISpnegoMechanism.ProcessMechToken(ReadOnlySpan<byte> token) => ProcessServerApRep(token);

    // A Windows acceptor sends no mechListMIC when the Kerberos it was offered first is the one it picks,
    // and Kerberos is always offered first, so no MIC is made for it, and one a server sends
    // anyway is ignored rather than verified. A downgrade to NTLM is caught by NTLM's own MIC over the same list.
    bool ISpnegoMechanism.SupportsMechListMic => false;

    byte[] ISpnegoMechanism.CreateMechListMic(ReadOnlySpan<byte> mechTypes) =>
        throw new NotSupportedException("Kerberos mechListMIC is not supported.");

    void ISpnegoMechanism.VerifyMechListMic(ReadOnlySpan<byte> mechTypes, ReadOnlySpan<byte> mic) =>
        throw new NotSupportedException("Kerberos mechListMIC is not supported.");

    ISpnegoMechanism ISpnegoMechanism.CreateForNewConnection() =>
        new KerberosSecurityProvider(_client, _servicePrincipalName, _context, _logger);

    /// <summary>
    /// Gets a TGT and a service ticket for <paramref name="servicePrincipalName"/> (e.g. <c>host/server01.corp.example.com</c>),
    /// talking to <paramref name="kdcs"/> (tried in order) over TCP directly (no DNS SRV discovery: that is <see cref="KerberosDiscovery"/>'s job).
    /// </summary>
    public static async Task<KerberosSecurityProvider> NegotiateAsync(
        KerberosCredential credential,
        IReadOnlyList<(string Host, int Port)> kdcs,
        string servicePrincipalName,
        ILoggerFactory? loggerFactory = null,
        IKerberosTransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(kdcs);
        if (kdcs.Count == 0 && !credential.UsesLogonSession)
            throw new ArgumentException("At least one KDC is required.", nameof(kdcs));

        var slash = servicePrincipalName.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == servicePrincipalName.Length - 1)
            throw new ArgumentException(
                $"'{servicePrincipalName}' is not a valid SPN; expected \"service/hostname\".",
                nameof(servicePrincipalName));

        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger<KerberosSecurityProvider>();

        var realm = credential.Realm.ToUpperInvariant();
        Krb5Config config;
        if (credential.UsesLogonSession)
            config = new Krb5Config(); // Windows finds the KDC itself
        else
        {
            // Kerberos.NET tries the KDCs in the order given and moves on from one that does not answer.
            List<string> lines =
            [
                "[libdefaults]",
                $"  default_realm = {realm}",
                "  dns_lookup_kdc = false",
                $"  default_tkt_enctypes = {SupportedEtypes}",
                $"  default_tgs_enctypes = {SupportedEtypes}",
                "[realms]", $"  {realm} = {{",
                .. kdcs.Select(k =>
                    $"    kdc = {(k.Host.Contains(':', StringComparison.Ordinal) ? $"[{k.Host}]" : k.Host)}:{k.Port}"),
                "  }"
            ];
            config = Krb5Config.Parse(string.Join('\n', lines));
        }

        var client = new KerberosClient(config, loggerFactory, transport ?? new TcpKerberosTransport(loggerFactory));
        try
        {
            if (credential.UsesLogonSession)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The Windows logon session is only available on Windows.");

                logger.LogDebug("Kerberos: using the Windows logon session's ticket cache.");
                // Built before CacheInMemory is cleared: with no cache set, the client falls back to its default
                // ticket cache file, which it creates (and, on dispose, fails on where that path is not writable).
                var lsaCache = new LsaCredentialCache(config, LsaInterop.Connect(), loggerFactory);
                client.Cache = lsaCache;
                client.CacheInMemory = false;
            }
            else if (credential.TicketCachePath is { } cachePath)
            {
                // The file is only read; the TGT in it replaces the AS exchange, and the service ticket stays in memory.
                logger.LogDebug("Kerberos: using the ticket-granting ticket in {Path}.", cachePath);
                var fileCache = new ReadOnlyFileTicketCache(cachePath, loggerFactory);
                client.Cache = fileCache;
                client.CacheInMemory = false;
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Kerberos: AS exchange for {User}@{Realm} with {Kdcs}.", credential.UserName, realm,
                        string.Join(", ", kdcs.Select(k => $"{k.Host}:{k.Port}")));
                await client
                    .Authenticate(new KerberosPasswordCredential(credential.UserName, credential.Password, realm))
                    .ConfigureAwait(false);
            }

            logger.LogDebug("Kerberos: TGS exchange for {Spn}.", servicePrincipalName);
            var context = await client
                .GetServiceTicket(ServiceTicketRequest(servicePrincipalName, AuthProtection.Privacy), cancellationToken)
                .ConfigureAwait(false);
            if (!IsSupported(context.ServiceTicketSessionKey.EType))
                throw new SecurityProviderException(
                    $"The service ticket is encrypted with {context.ServiceTicketSessionKey.EType}; message protection needs AES128 or AES256 enabled for the target.");

            return new KerberosSecurityProvider(client, servicePrincipalName, context, logger);
        }
        catch (Exception ex) when (ex is not (SecurityProviderException or OperationCanceledException
                                       or ArgumentException or PlatformNotSupportedException))
        {
            client.Dispose();
            throw new SecurityProviderException($"Kerberos login failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The encryption types the per-message protection implements: asking for these only makes a KDC that also offers
    /// the newer AES-SHA2 types (MIT Kerberos, FreeIPA) pick one whose session key can be used here.
    /// </summary>
    private const string SupportedEtypes = "aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96";

    private static bool IsSupported(EncryptionType type) =>
        type is EncryptionType.AES128_CTS_HMAC_SHA1_96 or EncryptionType.AES256_CTS_HMAC_SHA1_96;

    /// <summary>
    /// RFC 4121 section 4.1.1: the service flags a Windows acceptor expects in the Authenticator's GSS checksum.
    /// Sequence/replay detection is always on (the sequence numbers provide it); confidentiality and integrity
    /// reflect what <see cref="AuthProtection"/> the caller asked for. The authenticator must carry a sequence number.
    /// </summary>
    private static RequestServiceTicket ServiceTicketRequest(string servicePrincipalName, AuthProtection protection) =>
        new()
        {
            ServicePrincipalName = servicePrincipalName,
            ApOptions = ApOptions.MutualRequired,
            IncludeSequenceNumber = true,
            GssContextFlags = GssContextEstablishmentFlag.GSS_C_MUTUAL_FLAG
                              | GssContextEstablishmentFlag.GSS_C_SEQUENCE_FLAG
                              | GssContextEstablishmentFlag.GSS_C_REPLAY_FLAG
                              | protection switch
                              {
                                  AuthProtection.Privacy => GssContextEstablishmentFlag.GSS_C_CONF_FLAG
                                                            | GssContextEstablishmentFlag.GSS_C_INTEG_FLAG,
                                  AuthProtection.Integrity => GssContextEstablishmentFlag.GSS_C_INTEG_FLAG,
                                  _ => GssContextEstablishmentFlag.GSS_C_NONE,
                              },
        };

    /// <summary>Builds the AP-REQ (RFC 4120 section 5.5.1), wrapped as a bare GSS token (TOK_ID 01 00) for use as
    /// RPC_C_AUTHN_GSS_KERBEROS's own auth_value (no SPNEGO). See <see cref="GetInitialApReqAsync"/> for the
    /// unwrapped form SPNEGO's mechToken actually needs.</summary>
    public async ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection,
        CancellationToken cancellationToken = default) =>
        GssKerberosToken.WrapApReq(await GetInitialApReqAsync(protection, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Builds the same AP-REQ as <see cref="GetInitialTokenAsync"/>, without the 2-byte GSS TOK_ID prefix: a Windows
    /// acceptor's SPNEGO mechToken for the Kerberos mechanism is the bare AP-REQ (and its NegTokenResp's AP-REP is
    /// fully wrapped, see <see cref="ProcessServerApRep"/>), despite RFC 4121 section 4.1 implying the TOK_ID belongs on every context-establishment token, Windows omits
    /// it specifically inside SPNEGO's mechToken/responseToken, since SPNEGO's own mechTypes already identify
    /// the mechanism.
    /// </summary>
    internal async ValueTask<byte[]> GetInitialApReqAsync(AuthProtection protection,
        CancellationToken cancellationToken = default)
    {
        _protection = protection;

        // The client caches the ticket, so this normally only builds a new authenticator; the KDC is asked again only
        // if the cached ticket has expired.
        _context = await _client
            .GetServiceTicket(ServiceTicketRequest(_servicePrincipalName, protection), cancellationToken)
            .ConfigureAwait(false);
        var apReq = _context.ApReq.EncodeApplication().ToArray();

        // The authenticator's subkey, if the library made one, keys every token until the acceptor's own arrives.
        var key = _context.ClientSubSessionKey ?? _context.ServiceTicketSessionKey;
        _session = CreateSession(key, usesAcceptorSubkey: false, initialReceiveSequence: 0,
            initialSendSequence: unchecked((uint)(_context.SequenceNumber ?? 0)));

        // Mutual authentication is requested, so an acceptor always sends an AP-REP back with its own subkey
        // (see ProcessServerApRep, which replaces this session once that arrives) — the context is not actually
        // established until that is processed, but the caller needs no further leg of its own.
        IsComplete = true;
        return apReq;
    }

    private static Rfc4121AesSession CreateSession(KrbEncryptionKey key, bool usesAcceptorSubkey,
        uint initialReceiveSequence, uint initialSendSequence) => key.EType switch
    {
        EncryptionType.AES128_CTS_HMAC_SHA1_96 => new Rfc4121AesSession(KerberosEncryptionType.Aes128CtsHmacSha1,
            key.KeyValue.ToArray(), isInitiator: true, usesAcceptorSubkey, initialReceiveSequence, initialSendSequence),
        EncryptionType.AES256_CTS_HMAC_SHA1_96 => new Rfc4121AesSession(KerberosEncryptionType.Aes256CtsHmacSha1,
            key.KeyValue.ToArray(), isInitiator: true, usesAcceptorSubkey, initialReceiveSequence, initialSendSequence),
        var other => throw new SecurityProviderException($"Encryption type {other} is not supported by this client."),
    };

    /// <summary>
    /// A server normally sends nothing back (see <see cref="GetInitialTokenAsync"/>'s remarks); if it sends an
    /// AP-REP anyway, this verifies it. Either way, nothing more needs sending.
    /// </summary>
    public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default) =>
        new(ProcessServerApRep(token.Span));

    /// <summary>
    /// As <see cref="ProcessServerTokenAsync"/>, but for a bare AP-REP with no GSS TOK_ID — SPNEGO's responseToken
    /// (see <see cref="GetInitialApReqAsync"/>'s remarks). When the AP-REP carries
    /// a subkey, every later Wrap/MIC token must use it instead of the ticket's session key (RFC 4121 section 2;
    /// its Wrap tokens set the AcceptorSubkey flag), so this rebuilds
    /// <see cref="_session"/> on it. The AP-REP's sequence number is the acceptor's initial one (RFC 4120 section 5.5.1);
    /// a Windows acceptor sends a random, nonzero one, so it seeds the receive side.
    /// </summary>
    internal byte[]? ProcessServerApRep(ReadOnlySpan<byte> token)
    {
        if (token.Length == 0)
            return null;

        var apRep = KrbApRep.DecodeApplication(GssKerberosToken.UnwrapApRep(token));
        var part = apRep.EncryptedPart.Decrypt(_context.ServiceTicketSessionKey.AsKey(KeyUsage.EncApRepPart),
            KeyUsage.EncApRepPart, KrbEncApRepPart.DecodeApplication);
        var initialReceiveSequence = unchecked((uint)(part.SequenceNumber ?? 0));
        var sendSequence = unchecked((uint)(_context.SequenceNumber ?? 0));
        _session = part.SubSessionKey is { } subkey
            ? CreateSession(subkey, usesAcceptorSubkey: true, initialReceiveSequence, sendSequence)
            : CreateSession(_context.ClientSubSessionKey ?? _context.ServiceTicketSessionKey, usesAcceptorSubkey: false,
                initialReceiveSequence, sendSequence);
        _logger.LogDebug("Kerberos: AP-REP verified{Subkey}.",
            part.SubSessionKey is null ? string.Empty : " (acceptor subkey)");

        return null;
    }

    public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body,
        ReadOnlySpan<byte> signedAfter, Span<byte> signature) =>
        RequireSession().Protect(encryptBody, signedBefore, body, signedAfter, signature);

    public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body,
        ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature) =>
        RequireSession().Unprotect(decryptBody, signedBefore, body, signedAfter, signature);

    private Rfc4121AesSession RequireSession() =>
        _session ?? throw new InvalidOperationException(
            "No Kerberos message protection was negotiated for this connection.");
}

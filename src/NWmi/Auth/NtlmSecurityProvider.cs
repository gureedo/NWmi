using System.Buffers.Binary;
using System.Security.Cryptography;
using NWmi.Auth.Ntlm;
using NWmi.Rpc;

namespace NWmi.Auth;

/// <summary>
/// NTLMv2 as an RPC security provider (RPC_C_AUTHN_WINNT). Follows MS-NLMP 3.1.5: a NEGOTIATE_MESSAGE, then
/// an AUTHENTICATE_MESSAGE with the NTLMv2 response, a MIC when the server supplies a timestamp, and
/// an exchanged session key when signing or sealing is wanted.
/// </summary>
internal sealed class NtlmSecurityProvider : ISpnegoMechanism
{
    public const RpcAuthType MyAuthType = RpcAuthType.WinNt;
    public const string NtlmMechanismOid = "1.3.6.1.4.1.311.2.2.10";

    private readonly Credential _credential;
    private readonly NtlmSecurityOptions _options;
    private readonly Func<int, byte[]> _random;
    private readonly Func<ulong> _timestamp;
    private byte[]? _negotiateMessage;
    private NtlmNegotiateFlags _requestedFlags;
    private AuthProtection _protection;
    private NtlmSession? _session;

    public RpcAuthType AuthType => MyAuthType;

    public string MechanismOid => NtlmMechanismOid;

    public int SignatureSize => NtlmSession.SignatureSize;

    public bool IsComplete { get; private set; }

    public NtlmSecurityProvider(Credential credential, NtlmSecurityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _options = options ?? new NtlmSecurityOptions();
        _random = _options.Random ?? RandomNumberGenerator.GetBytes;
        _timestamp = _options.Timestamp ?? CurrentFileTime;
    }

    public ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection,
        CancellationToken cancellationToken = default)
    {
        _protection = protection;
        _requestedFlags = NtlmNegotiateFlags.Unicode
                          | NtlmNegotiateFlags.RequestTarget
                          | NtlmNegotiateFlags.Ntlm
                          | NtlmNegotiateFlags.AlwaysSign
                          | NtlmNegotiateFlags.ExtendedSessionSecurity
                          | NtlmNegotiateFlags.TargetInfo
                          | NtlmNegotiateFlags.Version
                          | NtlmNegotiateFlags.Bits128
                          | NtlmNegotiateFlags.Bits56
                          | NtlmNegotiateFlags.KeyExchange;

        if (protection >= AuthProtection.Integrity)
            _requestedFlags |= NtlmNegotiateFlags.Sign;

        if (protection == AuthProtection.Privacy)
            _requestedFlags |= NtlmNegotiateFlags.Seal;

        _negotiateMessage = new NtlmNegotiateMessage
        {
            Flags = _requestedFlags,
            Version = NtlmVersion.Default
        }.Serialize();
        return new ValueTask<byte[]>(_negotiateMessage);
    }

    public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default) =>
        new(ProcessMechToken(token.Span));

    // NTLM tokens are the same bare messages inside and outside SPNEGO.
    public byte[] ProcessMechToken(ReadOnlySpan<byte> token)
    {
        if (_negotiateMessage is null || IsComplete)
            throw new InvalidOperationException(
                "Unexpected token: the NTLM handshake is not waiting for a server token.");

        var challengeMessage = token.ToArray();
        var challenge = NtlmChallengeMessage.Deserialize(challengeMessage);
        var message = BuildAuthenticate(challenge, challengeMessage);
        IsComplete = true;
        return message;
    }

    public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body,
        ReadOnlySpan<byte> signedAfter, Span<byte> signature) =>
        RequireSession().Protect(encryptBody, signedBefore, body, signedAfter, signature);

    public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body,
        ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature) =>
        RequireSession().Unprotect(decryptBody, signedBefore, body, signedAfter, signature);

    public IRpcSecurityProvider CreateForNewConnection() =>
        new NtlmSecurityProvider(_credential, _options);

    ValueTask<byte[]> ISpnegoMechanism.GetInitialMechTokenAsync(AuthProtection protection,
        CancellationToken cancellationToken) =>
        GetInitialTokenAsync(protection, cancellationToken);

    bool ISpnegoMechanism.SupportsMechListMic => _session is not null;

    byte[] ISpnegoMechanism.CreateMechListMic(ReadOnlySpan<byte> mechTypes) => RequireSession().MakeMic(mechTypes);

    void ISpnegoMechanism.VerifyMechListMic(ReadOnlySpan<byte> mechTypes, ReadOnlySpan<byte> mic) =>
        RequireSession().VerifyMic(mechTypes, mic);

    ISpnegoMechanism ISpnegoMechanism.CreateForNewConnection() =>
        new NtlmSecurityProvider(_credential, _options);

    private NtlmSession RequireSession() =>
        _session ?? throw new InvalidOperationException(
            "No NTLM message protection was negotiated for this connection.");

    private byte[] BuildAuthenticate(NtlmChallengeMessage challenge, byte[] challengeMessage)
    {
        var wantsProtection = _protection >= AuthProtection.Integrity;
        if ((challenge.Flags & NtlmNegotiateFlags.Unicode) == 0)
            throw new SecurityProviderException("The server does not support Unicode NTLM strings.");

        if (wantsProtection)
        {
            RequireServerAgreed(challenge, NtlmNegotiateFlags.Sign, "message signing");
            RequireServerAgreed(challenge, NtlmNegotiateFlags.ExtendedSessionSecurity,
                "NTLM extended session security");
            if (_protection == AuthProtection.Privacy)
                RequireServerAgreed(challenge, NtlmNegotiateFlags.Seal, "message sealing");

            if (challenge.Find(AvId.NbComputerName) is null || challenge.Find(AvId.NbDomainName) is null)
                throw new SecurityProviderException(
                    "The server's NTLM target info lacks its computer and domain names.");
        }

        // The flags we confirm: what we asked for, minus security features the server did not grant.
        var flags = _requestedFlags;
        foreach (var feature in new[]
                 {
                     NtlmNegotiateFlags.Sign, NtlmNegotiateFlags.Seal, NtlmNegotiateFlags.Bits128,
                     NtlmNegotiateFlags.Bits56, NtlmNegotiateFlags.KeyExchange
                 })
            if ((challenge.Flags & feature) == 0)
                flags &= ~feature;

        var pairs = challenge.TargetInfo.ToList();
        var serverTimestamp = challenge.Find(AvId.Timestamp);
        var timestamp = serverTimestamp is { } t ? ParseFileTime(t.Value) : _timestamp();
        var withMic = serverTimestamp is not null;
        if (pairs.Count > 0)
            AddClientAttributes(pairs, withMic);

        var responseKey = NtlmV2Response.ResponseKey(_credential.Password, _credential.UserName, _credential.Domain);
        var response = NtlmV2Response.ComputeResponse(
            responseKey, challenge.ServerChallenge, _random(8), timestamp, AvPair.SerializeList(pairs),
            sendLmResponse: !withMic);

        // For NTLMv2 the key exchange key is the session base key.
        byte[] exportedSessionKey;
        byte[] encryptedSessionKey;
        if ((flags & NtlmNegotiateFlags.KeyExchange) != 0 &&
            (flags & (NtlmNegotiateFlags.Sign | NtlmNegotiateFlags.Seal)) != 0)
        {
            exportedSessionKey = _random(16);
            encryptedSessionKey = Rc4.Crypt(response.SessionBaseKey, exportedSessionKey);
        }
        else
        {
            flags &= ~NtlmNegotiateFlags.KeyExchange;
            exportedSessionKey = response.SessionBaseKey;
            encryptedSessionKey = [];
        }

        var workstation = _options.Workstation ?? Environment.MachineName.ToUpperInvariant();
        var authenticate = new NtlmAuthenticateMessage
        {
            Flags = flags,
            Domain = _credential.Domain,
            Username = _credential.UserName,
            Workstation = workstation,
            LmResponse = response.LmResponse,
            NtResponse = response.NtResponse,
            EncryptedSessionKey = encryptedSessionKey,
            Version = NtlmVersion.Default
        }.Serialize(withMic);

        if (withMic)
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, exportedSessionKey);
            hmac.AppendData(_negotiateMessage!);
            hmac.AppendData(challengeMessage);
            hmac.AppendData(authenticate);
            NtlmAuthenticateMessage.PatchMic(authenticate, hmac.GetHashAndReset());
        }

        if (wantsProtection)
            _session = new NtlmSession(exportedSessionKey, flags, isClient: true);

        return authenticate;
    }

    /// <summary>Adds the attributes MS-NLMP 3.1.5.1.2 asks the client to include in the NTLMv2 blob.</summary>
    private void AddClientAttributes(List<AvPair> pairs, bool withMic)
    {
        if (withMic)
        {
            var existing = pairs.FindIndex(p => p.Id == AvId.Flags);
            var flagValue = existing >= 0 ? BitConverter.ToUInt32(pairs[existing].Value) : 0;
            var updated = AvPair.UInt32(AvId.Flags, flagValue | AvId.FlagMicProvided);
            if (existing >= 0)
                pairs[existing] = updated;
            else
                pairs.Add(updated);
        }

        pairs.Add(new AvPair(AvId.ChannelBindings, _options.ChannelBindings?.Invoke() ?? new byte[16]));
        pairs.Add(AvPair.Text(AvId.TargetName, _options.TargetName ?? string.Empty));
    }

    private static void RequireServerAgreed(NtlmChallengeMessage challenge, NtlmNegotiateFlags flag, string what)
    {
        if ((challenge.Flags & flag) == 0)
            throw new SecurityProviderException($"The server did not agree to {what}.");
    }

    private static ulong CurrentFileTime() => (ulong)DateTime.UtcNow.ToFileTimeUtc();

    private static ulong ParseFileTime(ReadOnlySpan<byte> value) =>
        value.Length == sizeof(ulong)
            ? BinaryPrimitives.ReadUInt64LittleEndian(value)
            : throw new SecurityProviderException("The server sent a malformed timestamp.");
}

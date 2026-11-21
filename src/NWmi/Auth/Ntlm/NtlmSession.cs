using System.Buffers.Binary;
using System.Security.Cryptography;

#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms

namespace NWmi.Auth.Ntlm;

/// <summary>
/// Message signing and sealing for an established NTLM context with extended session security (MS-NLMP 3.4).
/// Each direction has its own signing key, RC4 sealing state and sequence number.
/// </summary>
internal sealed class NtlmSession
{
    public const int SignatureSize = 16;

    // The key derivation constants of MS-NLMP 3.4.5.2 (SIGNKEY) and 3.4.5.3 (SEALKEY). The spec appends a null
    // terminator to each before hashing, which is the \0 at the end of each literal.
    private static ReadOnlySpan<byte> ClientToServerSigningMagic =>
        "session key to client-to-server signing key magic constant\0"u8;

    private static ReadOnlySpan<byte> ServerToClientSigningMagic =>
        "session key to server-to-client signing key magic constant\0"u8;

    private static ReadOnlySpan<byte> ClientToServerSealingMagic =>
        "session key to client-to-server sealing key magic constant\0"u8;

    private static ReadOnlySpan<byte> ServerToClientSealingMagic =>
        "session key to server-to-client sealing key magic constant\0"u8;

    private readonly byte[] _signKeySend;
    private readonly byte[] _signKeyReceive;
    private readonly byte[] _sealKeySend;
    private readonly byte[] _sealKeyReceive;
    private readonly Rc4 _sealSend;
    private readonly Rc4 _sealReceive;
    private readonly bool _encryptChecksum;
    private uint _sequenceSend;
    private uint _sequenceReceive;

    /// <param name="exportedSessionKey">The 16-byte session key both sides now share.</param>
    /// <param name="flags">The negotiated flags.</param>
    /// <param name="isClient">Selects which direction uses the client-to-server keys.</param>
    public NtlmSession(ReadOnlySpan<byte> exportedSessionKey, NtlmNegotiateFlags flags, bool isClient)
    {
        if ((flags & NtlmNegotiateFlags.ExtendedSessionSecurity) == 0)
            throw new SecurityProviderException(
                "Message signing needs NTLM extended session security, which the server did not agree to.");

        _signKeySend = SignKey(exportedSessionKey, clientToServer: isClient);
        _signKeyReceive = SignKey(exportedSessionKey, clientToServer: !isClient);
        _sealKeySend = SealKey(exportedSessionKey, flags, clientToServer: isClient);
        _sealKeyReceive = SealKey(exportedSessionKey, flags, clientToServer: !isClient);
        _sealSend = new Rc4(_sealKeySend);
        _sealReceive = new Rc4(_sealKeyReceive);
        _encryptChecksum = (flags & NtlmNegotiateFlags.KeyExchange) != 0;
    }

    /// <summary>SIGNKEY (MS-NLMP 3.4.5.2).</summary>
    public static byte[] SignKey(ReadOnlySpan<byte> exportedSessionKey, bool clientToServer) =>
        MD5.HashData([
            .. exportedSessionKey, .. (clientToServer ? ClientToServerSigningMagic : ServerToClientSigningMagic)
        ]);

    /// <summary>SEALKEY (MS-NLMP 3.4.5.3), for the case where extended session security is negotiated.</summary>
    public static byte[] SealKey(ReadOnlySpan<byte> exportedSessionKey, NtlmNegotiateFlags flags, bool clientToServer)
    {
        var keyMaterial = (flags & NtlmNegotiateFlags.Bits128) != 0 ? exportedSessionKey
            : (flags & NtlmNegotiateFlags.Bits56) != 0 ? exportedSessionKey[..7]
            : exportedSessionKey[..5];

        return MD5.HashData(
            [.. keyMaterial, .. (clientToServer ? ClientToServerSealingMagic : ServerToClientSealingMagic)]
        );
    }

    /// <summary>
    /// Signs <paramref name="body"/> and, when <paramref name="seal"/> is set, encrypts it in place. The signature
    /// covers <paramref name="before"/>, the plaintext body and <paramref name="after"/>, in that order; those
    /// two segments are signed but never encrypted (they are empty unless RPC header signing is in use).
    /// </summary>
    public void Protect(bool seal, ReadOnlySpan<byte> before, Span<byte> body, ReadOnlySpan<byte> after,
        Span<byte> signature)
    {
        var sequence = _sequenceSend++;
        var checksum = Checksum(_signKeySend, sequence, before, body, after);

        // Order matters: the keystream encrypts the message first, then the checksum.
        if (seal)
            _sealSend.Transform(body, body);

        if (_encryptChecksum)
            _sealSend.Transform(checksum, checksum);

        WriteSignature(signature, checksum, sequence);
    }

    public void Unprotect(bool unseal, ReadOnlySpan<byte> before, Span<byte> body, ReadOnlySpan<byte> after,
        ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureSize || BinaryPrimitives.ReadUInt32LittleEndian(signature) != 1)
            throw new SecurityProviderException("The NTLM message signature is malformed.");

        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(signature[12..]);
        if (sequence != _sequenceReceive)
            throw new SecurityProviderException(
                $"NTLM message out of sequence: expected {_sequenceReceive} but received {sequence}.");

        if (unseal)
            _sealReceive.Transform(body, body);

        var expected = Checksum(_signKeyReceive, sequence, before, body, after);
        var received = signature.Slice(4, 8).ToArray();
        if (_encryptChecksum)
            _sealReceive.Transform(received, received);

        if (!CryptographicOperations.FixedTimeEquals(expected, received))
            throw new SecurityProviderException("The NTLM message signature does not match: the message was altered.");

        _sequenceReceive++;
    }

    /// <summary>
    /// The SPNEGO mechListMIC (MS-SPNG 3.3.5.1): a signature over <paramref name="data"/> made as the first message
    /// of the context (sequence 0, fresh RC4 state). The RC4 state is then put back, so the first message the
    /// application signs shares it, but the sequence numbers move on: each side's MIC took its sequence 0, so the
    /// next message in either direction is 1.
    /// </summary>
    public byte[] MakeMic(ReadOnlySpan<byte> data)
    {
        _sequenceSend = 1;
        var checksum = Checksum(_signKeySend, 0, [], data, []);
        if (_encryptChecksum)
            new Rc4(_sealKeySend).Transform(checksum, checksum);

        var signature = new byte[SignatureSize];
        WriteSignature(signature, checksum, 0);
        return signature;
    }

    /// <summary>Checks the peer's mechListMIC over <paramref name="data"/>, made at its sequence 0 (see <see cref="MakeMic"/>).</summary>
    public void VerifyMic(ReadOnlySpan<byte> data, ReadOnlySpan<byte> mic)
    {
        if (mic.Length != SignatureSize || BinaryPrimitives.ReadUInt32LittleEndian(mic) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(mic[12..]) != 0)
            throw new SecurityProviderException("The NTLM mechListMIC is malformed.");

        var expected = Checksum(_signKeyReceive, 0, [], data, []);
        var received = mic.Slice(4, 8).ToArray();
        if (_encryptChecksum)
            new Rc4(_sealKeyReceive).Transform(received, received);

        if (!CryptographicOperations.FixedTimeEquals(expected, received))
            throw new SecurityProviderException("The NTLM mechListMIC does not match: the mechanism list was altered.");

        _sequenceReceive = 1;
    }

    private static byte[] Checksum(ReadOnlySpan<byte> signKey, uint sequence, ReadOnlySpan<byte> before, ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> after)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, signKey);
        Span<byte> sequenceBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sequenceBytes, sequence);
        hmac.AppendData(sequenceBytes);
        hmac.AppendData(before);
        hmac.AppendData(body);
        hmac.AppendData(after);
        return hmac.GetHashAndReset()[..8];
    }

    private static void WriteSignature(Span<byte> signature, ReadOnlySpan<byte> checksum, uint sequence)
    {
        if (signature.Length != SignatureSize)
            throw new ArgumentException($"The signature buffer must be {SignatureSize} bytes.", nameof(signature));

        BinaryPrimitives.WriteUInt32LittleEndian(signature, 1);
        checksum.CopyTo(signature[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(signature[12..], sequence);
    }
}

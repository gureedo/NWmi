using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NWmi.Auth.Kerberos;

/// <summary>
/// Message signing and sealing for an established AES Kerberos GSS context, using RFC 4121's "new" per-message
/// token format (§4.2): a 16-byte clear header (TOK_ID, Flags, Filler, EC, RRC, a 64-bit SND_SEQ sent in the
/// clear — unlike RFC 4757's RC4 format, no sequence-number encryption step exists here), followed by either
/// the AES-CTS-encrypted confounder+body+filler+header-copy and an HMAC-SHA1-96 (Wrap, sealed — <c>RpcAuthLevel.PacketPrivacy</c>),
/// or, at <c>RpcAuthLevel.PacketIntegrity</c>, a MIC token (§4.2.6.1) over the body, which stays in the clear.
/// </summary>
/// <remarks>
/// This is the token format Windows produces for <c>GSS_WrapEx</c> with RPC's header, body and sec_trailer buffers
/// (MS-KILE 3.4.5.4, MS-RPCE 3.3.1.5.2.2), as <c>EncryptMessage</c>/<c>DecryptMessage</c> with
/// <c>SECBUFFER_READONLY_WITH_CHECKSUM</c> buffers make it:
/// <list type="bullet">
/// <item>The sender uses <c>EC=16</c> (one block of zero filler) and <c>RRC=28</c>. The token that goes in the RPC
/// authentication trailer is the clear header, then the rotated material ahead of the body: filler, encrypted
/// header copy, checksum, encrypted confounder (MS-KILE: "clearhdr + padding + enchdr + checksum + confounder"), 76 bytes.
/// The body itself stays in place in the stub, so it is not part of the token.</item>
/// <item>With RPC header signing negotiated, the PDU header and the sec_trailer (<c>before</c> and
/// <c>after</c>) are signed but not encrypted: the checksum covers confounder | header | body | trailer |
/// filler | header copy (MS-KILE: all input buffers in order, including the sign-only ones). Without header signing they
/// are empty and the checksum covers confounder | body | filler | header copy. Both were checked byte for byte
/// against Windows-made tokens and against Windows' own <c>DecryptMessage</c>.</item>
/// <item>The embedded header copy has <c>RRC</c> zeroed (RFC 4121 §4.2.4) and carries the real <c>EC</c>.</item>
/// <item>A receiver must accept whatever EC the peer chose and a declared auth_value longer than the real token (an
/// acceptor declares its per-context maximum, 76 bytes, and zero-pads): only the bytes the EC field accounts for
/// are read.</item>
/// <item>The data a Windows acceptor can verify with header signing must be a multiple of 16 bytes, so the RPC layer
/// pads the stub from its own start, as Windows does (<see cref="NWmi.Rpc.RpcPdu.AuthAlignment"/>).</item>
/// </list>
/// Integrity-only messages use MIC tokens, because the RPC runtime calls <c>GSS_GetMICEx</c>/<c>GSS_VerifyMICEx</c> there
/// rather than a Wrap without encryption (MS-RPCE 3.3.1.5.2.2): 28 bytes, the 16-byte header (TOK_ID 04 04, flags, five
/// 0xFF) and the checksum, made with the *sign* key usage over [PDU header] | body | [sec_trailer] | the token's own header,
/// the bracketed parts present with header signing. Windows' own MIC tokens were decoded this way and its
/// <c>VerifySignature</c> accepts ours.
/// </remarks>
internal sealed class Rfc4121AesSession(
    KerberosEncryptionType type,
    byte[] key,
    bool isInitiator,
    bool usesAcceptorSubkey = false,
    ulong initialReceiveSequence = 0,
    ulong initialSendSequence = 0)
{
    /// <summary>
    /// ClearHeader(16) + Filler ciphertext(16) + HeaderCopy ciphertext(16) + HMAC-SHA1-96(12) + Confounder ciphertext(16);
    /// see this class's remarks.
    /// </summary>
    public const int SignatureSize = 76;

    /// <summary>A MIC token: the clear header and the checksum.</summary>
    public const int UnsealedSignatureSize = 28;

    private const int HeaderSize = 16;
    private const int BlockSize = 16;
    private const int ChecksumSize = 12;

    /// <summary>The extra count a sender uses: one block of filler (MS-KILE 3.4.5.4).</summary>
    private const int ExtraCount = BlockSize;

    private const ushort TokIdWrap = 0x0504;
    private const ushort TokIdMic = 0x0404;

    private const byte FlagSentByAcceptor = 0x01;
    private const byte FlagSealed = 0x02;
    private const byte FlagAcceptorSubkey = 0x04;

    private ulong _sequenceSend = initialSendSequence;
    private ulong _sequenceReceive = initialReceiveSequence;

    // RFC 4120 §5.5.1's seq-number field description says the AP-REP's seq-number is "the initial sequence
    // number ... for messages from the server to the client" — a Windows acceptor always sends a random,
    // nonzero one, so the initiator's receive side must start from it rather
    // than 0. See KerberosSecurityProvider.ProcessServerApRep.

    public void Protect(bool seal, ReadOnlySpan<byte> before, Span<byte> body, ReadOnlySpan<byte> after,
        Span<byte> signature)
    {
        var expectedSize = seal ? SignatureSize : UnsealedSignatureSize;
        if (signature.Length != expectedSize)
            throw new ArgumentException($"The signature buffer must be {expectedSize} bytes.", nameof(signature));

        var sequence = _sequenceSend++;
        if (!seal)
        {
            WriteMicToken(before, body, after, sequence, signature);
            return;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        WriteWrapHeader(header, sequence, sentByAcceptor: !isInitiator, usesAcceptorSubkey);
        header.CopyTo(signature);

        var usage = SealUsage(sending: true);

        // body | filler(EC zero bytes) | header-copy(16). RFC 4121 4.2.4: the RRC field in the TO-BE-ENCRYPTED header
        // copy is zeroed, even though the cleartext wire header (already copied to `signature` above) carries
        // the real RRC value.
        var plaintext = new byte[body.Length + ExtraCount + BlockSize];
        body.CopyTo(plaintext);
        header.CopyTo(plaintext.AsSpan(body.Length + ExtraCount));
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(body.Length + ExtraCount + 6), 0); // RRC

        var (cipherCore, checksum) = Aes3961.EncryptCore(type, key, usage, plaintext, before, after, body.Length);
        // cipherCore = confounderCiphertext(16) | bodyCiphertext | fillerCiphertext(EC) | headerCopyCiphertext(16).
        var confounderCt = cipherCore.AsSpan(0, BlockSize);
        cipherCore.AsSpan(BlockSize, body.Length).CopyTo(body);
        var fillerCt = cipherCore.AsSpan(BlockSize + body.Length, ExtraCount);
        var headerCopyCt = cipherCore.AsSpan(BlockSize + body.Length + ExtraCount, BlockSize);

        // The rotation by RRC + EC leaves: clearhdr | filler | enchdr | checksum | confounder (MS-KILE 3.4.5.4).
        fillerCt.CopyTo(signature[HeaderSize..]);
        headerCopyCt.CopyTo(signature[(HeaderSize + ExtraCount)..]);
        checksum.CopyTo(signature[(HeaderSize + ExtraCount + BlockSize)..]);
        confounderCt.CopyTo(signature[(HeaderSize + ExtraCount + BlockSize + ChecksumSize)..]);
    }

    public void Unprotect(bool unseal, ReadOnlySpan<byte> before, Span<byte> body, ReadOnlySpan<byte> after,
        ReadOnlySpan<byte> signature)
    {
        if (!unseal)
        {
            VerifyMicToken(before, body, after, signature);
            return;
        }

        // The smallest token is the one with no filler (EC = 0), as Windows' HTTP stack makes them.
        if (signature.Length < HeaderSize + BlockSize + ChecksumSize + BlockSize)
            throw new SecurityProviderException("The Kerberos message token is malformed.");

        var header = signature[..HeaderSize];
        var tokId = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (tokId != TokIdWrap)
            throw new SecurityProviderException("The Kerberos message token has an unrecognized TOK_ID.");

        var flags = header[2];
        var sentByAcceptor = (flags & FlagSentByAcceptor) != 0;
        if (sentByAcceptor != isInitiator) // we receive from the acceptor iff we are the initiator, and vice versa
            throw new SecurityProviderException(
                "The Kerberos message token's direction flag does not match the expected sender.");

        if ((flags & FlagSealed) == 0)
            throw new SecurityProviderException(
                "The Kerberos message token's Sealed flag does not match the requested protection level.");

        var sequence = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        if (sequence != _sequenceReceive)
            throw new SecurityProviderException(
                $"Kerberos message out of sequence: expected {_sequenceReceive} but received {sequence}.");

        // EC (RFC 4121 §4.2.3): the peer chooses how much filler goes between the body and the embedded header copy
        // before encrypting. The declared auth_value can be longer than HeaderSize + EC + the rest (an acceptor
        // pads to its per-context maximum with plain zero bytes, not ciphertext), so only that many bytes are read.
        var ec = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (signature.Length < HeaderSize + ec + BlockSize + ChecksumSize + BlockSize)
            throw new SecurityProviderException(
                "The Kerberos message token's EC field does not match its actual length.");

        var usage = SealUsage(sending: false);

        // Token layout after the rotation by RRC + EC: clearhdr | filler | enchdr | checksum | confounder.
        var fillerCt = signature.Slice(HeaderSize, ec);
        var headerCopyCt = signature.Slice(HeaderSize + ec, BlockSize);
        var checksum = signature.Slice(HeaderSize + ec + BlockSize, ChecksumSize);
        var confounderCt = signature.Slice(HeaderSize + ec + BlockSize + ChecksumSize, BlockSize);

        // Rebuild cipherCore in the order it was originally encrypted: confounder | body | filler | header-copy.
        var cipherCore = new byte[BlockSize + body.Length + ec + BlockSize];
        confounderCt.CopyTo(cipherCore);
        body.CopyTo(cipherCore.AsSpan(BlockSize));
        fillerCt.CopyTo(cipherCore.AsSpan(BlockSize + body.Length, ec));
        headerCopyCt.CopyTo(cipherCore.AsSpan(BlockSize + body.Length + ec));

        // DecryptCore already strips the leading confounder, so plaintext here is body | filler | header-copy.
        var plaintext = Aes3961.DecryptCore(type, key, usage, cipherCore, checksum, before, after, body.Length);

        // The embedded header-copy has RRC zeroed (see Protect), so compare against the wire header with
        // its RRC zeroed too, rather than the raw received header.
        var expectedEmbeddedHeader = header.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(expectedEmbeddedHeader.AsSpan(6), 0); // RRC
        if (!CryptographicOperations.FixedTimeEquals(plaintext.AsSpan(body.Length + ec), expectedEmbeddedHeader))
            throw new SecurityProviderException(
                "The Kerberos message's embedded header copy does not match the cleartext header: the header was tampered with.");

        plaintext.AsSpan(0, body.Length).CopyTo(body);
        _sequenceReceive++;
    }

    private static void WriteWrapHeader(Span<byte> header, ulong sequence, bool sentByAcceptor, bool usesAcceptorSubkey)
    {
        BinaryPrimitives.WriteUInt16BigEndian(header, TokIdWrap);
        header[2] = (byte)((sentByAcceptor ? FlagSentByAcceptor : 0) | FlagSealed |
                           (usesAcceptorSubkey ? FlagAcceptorSubkey : 0));
        header[3] = 0xFF; // Filler
        // EC=16 and RRC=28 (= ChecksumSize + one block); see this class's remarks.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], ExtraCount);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], ChecksumSize + BlockSize);
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], sequence);
    }

    /// <summary>
    /// An RFC 4121 §4.2.6.1 MIC token: TOK_ID 04 04, flags, five 0xFF filler bytes, the sequence number and the checksum.
    /// The checksum (sign key usage) covers [PDU header] | body | [sec_trailer] | the token's own 16-byte header.
    /// </summary>
    private void WriteMicToken(ReadOnlySpan<byte> before, ReadOnlySpan<byte> body, ReadOnlySpan<byte> after,
        ulong sequence, Span<byte> signature)
    {
        if (signature.Length != UnsealedSignatureSize)
            throw new ArgumentException($"The signature buffer must be {UnsealedSignatureSize} bytes.",
                nameof(signature));

        var header = signature[..HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(header, TokIdMic);
        header[2] = (byte)((isInitiator ? 0 : FlagSentByAcceptor) | (usesAcceptorSubkey ? FlagAcceptorSubkey : 0));
        header[3..8].Fill(0xFF);
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], sequence);
        MicChecksum(SignUsage(sending: true), before, body, after, header).CopyTo(signature[HeaderSize..]);
    }

    private void VerifyMicToken(ReadOnlySpan<byte> before, ReadOnlySpan<byte> body, ReadOnlySpan<byte> after,
        ReadOnlySpan<byte> signature)
    {
        if (signature.Length < UnsealedSignatureSize)
            throw new SecurityProviderException("The Kerberos message token is malformed.");

        var header = signature[..HeaderSize];
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != TokIdMic)
            throw new SecurityProviderException("The Kerberos message token has an unrecognized TOK_ID.");

        if (((header[2] & FlagSentByAcceptor) != 0) != isInitiator)
            throw new SecurityProviderException(
                "The Kerberos message token's direction flag does not match the expected sender.");

        var sequence = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        if (sequence != _sequenceReceive)
            throw new SecurityProviderException(
                $"Kerberos message out of sequence: expected {_sequenceReceive} but received {sequence}.");

        var expected = MicChecksum(SignUsage(sending: false), before, body, after, header);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature.Slice(HeaderSize, ChecksumSize)))
            throw new SecurityProviderException(
                "The Kerberos message checksum does not match: the message was altered.");

        _sequenceReceive++;
    }

    private byte[] MicChecksum(KerberosKeyUsage usage, ReadOnlySpan<byte> before, ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> after, ReadOnlySpan<byte> header)
    {
        var tail = new byte[after.Length + HeaderSize];
        after.CopyTo(tail);
        header.CopyTo(tail.AsSpan(after.Length));
        return Aes3961.GetMic(key, usage, before, body, tail);
    }

    private KerberosKeyUsage SealUsage(bool sending) =>
        (sending == isInitiator) ? KerberosKeyUsage.InitiatorSeal : KerberosKeyUsage.AcceptorSeal;

    private KerberosKeyUsage SignUsage(bool sending) =>
        (sending == isInitiator) ? KerberosKeyUsage.InitiatorSign : KerberosKeyUsage.AcceptorSign;
}

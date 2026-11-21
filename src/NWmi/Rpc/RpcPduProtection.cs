using System.Buffers.Binary;

namespace NWmi.Rpc;

/// <summary>
/// Applies a security provider to a serialized request or response PDU (MS-RPCE 3.3.1.5.2.2).
/// The PDU is split into its header (the common header plus the fixed fields before the stub), the body
/// (stub data and padding) and the sec_trailer. The body is encrypted at privacy level. With header signing
/// the header and sec_trailer are signed too.
/// </summary>
internal static class RpcPduProtection
{
    private const int SecTrailerSize = 8;

    /// <summary>
    /// Signs (and, at privacy level, encrypts) <paramref name="pdu"/> in place and writes the signature into it.
    /// The PDU must have been serialized with a zero-filled token of the provider's signature size, and
    /// <paramref name="headerLength"/> counts the bytes before the stub: 24 for a response, 24 or 40 for a request.
    /// </summary>
    public static void Protect(Span<byte> pdu, int headerLength, RpcSecurity security, bool headerSigning)
    {
        var layout = Locate(pdu, headerLength);
        var header = headerSigning ? pdu[..headerLength] : default;
        var trailer = headerSigning ? pdu.Slice(layout.TrailerStart, SecTrailerSize) : default;

        security.Provider.Protect(
            encryptBody: security.Level == RpcAuthLevel.PacketPrivacy,
            header,
            pdu[headerLength..layout.TrailerStart],
            trailer,
            pdu[(layout.TrailerStart + SecTrailerSize)..]);
    }

    /// <summary>Verifies and, at privacy level, decrypts a received PDU in place.</summary>
    public static void Unprotect(Span<byte> pdu, int headerLength, RpcSecurity security, bool headerSigning)
    {
        if (pdu.Length < headerLength + SecTrailerSize + security.Provider.SignatureSize)
            throw new RpcProtocolException(
                "The PDU is too short to carry the expected security trailer; unprotected data is not accepted.");

        var authLength = BinaryPrimitives.ReadUInt16LittleEndian(pdu[10..]);
        if (authLength < security.Provider.SignatureSize)
            throw new RpcProtocolException(
                $"The PDU carries {authLength} bytes of authentication data but at least {security.Provider.SignatureSize} bytes were expected; unprotected data is not accepted.");

        var layout = Locate(pdu, headerLength);
        CheckTrailer(pdu.Slice(layout.TrailerStart, SecTrailerSize), security);

        var header = headerSigning ? pdu[..headerLength] : default;
        var trailer = headerSigning ? pdu.Slice(layout.TrailerStart, SecTrailerSize) : default;
        security.Provider.Unprotect(
            decryptBody: security.Level == RpcAuthLevel.PacketPrivacy,
            header,
            pdu[headerLength..layout.TrailerStart],
            trailer,
            pdu[(layout.TrailerStart + SecTrailerSize)..]);
    }

    private static (int TrailerStart, int AuthLength) Locate(Span<byte> pdu, int headerLength)
    {
        var authLength = BinaryPrimitives.ReadUInt16LittleEndian(pdu[10..]);
        var trailerStart = pdu.Length - authLength - SecTrailerSize;
        if (authLength == 0 || trailerStart < headerLength)
            throw new RpcProtocolException("The PDU has no valid security trailer.");

        return (trailerStart, authLength);
    }

    /// <summary>The trailer must name the same provider, level and context that were set up at bind time.</summary>
    private static void CheckTrailer(ReadOnlySpan<byte> trailer, RpcSecurity security)
    {
        if (trailer[0] != (byte)security.Provider.AuthType
            || trailer[1] != (byte)security.Level
            || BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != security.ContextId)
            throw new RpcProtocolException(
                "The security trailer does not match the security context negotiated at bind time.");
    }
}

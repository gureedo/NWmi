using Kerberos.NET.Entities;

namespace NWmi.Auth;

/// <summary>
/// The bare (no outer GSS-API SEQUENCE/OID wrapper) Kerberos context-establishment tokens used inside
/// SPNEGO's mechToken (RFC 4121 §4.1/§4.4): just a 2-byte big-endian TOK_ID followed by the raw Kerberos message.
/// </summary>
internal static class GssKerberosToken
{
    // TOK_ID values of the Kerberos GSS tokens (RFC 4121 §4.1).
    private static readonly byte[] ApReqTokId = [0x01, 0x00];
    private static readonly byte[] ApRepTokId = [0x02, 0x00];
    private const byte KrbErrorTokIdHigh = 0x03;
    private const byte KrbErrorTokIdLow = 0x00;

    // DER tags: class bits (0x40 application) + constructed (0x20) + tag number.
    private const byte GssInitialContextTokenTag = 0x60; // [APPLICATION 0], RFC 2743 §3.1
    private const byte ApRepTag = 0x6F; // [APPLICATION 15], RFC 4120 §5.5.2
    private const byte KrbErrorTag = 0x7E; // [APPLICATION 30], RFC 4120 §5.9.1

    // DER length encoding (X.690 §8.1.3): with the high bit set, the low 7 bits count the length bytes that follow.
    private const int LongLengthFormFlag = 0x80;
    private const int LengthByteCountMask = 0x7F;

    public static byte[] WrapApReq(byte[] apReq) => [.. ApReqTokId, .. apReq];

    /// <summary>
    /// Unwraps an AP-REP token, accepting either the bare TOK_ID form (02 00 + message) used directly as
    /// RPC_C_AUTHN_GSS_KERBEROS's auth_value, or the full GSS-API "InitialContextToken" form — [APPLICATION 0]
    /// SEQUENCE { mechOID, 02 00 + message } (RFC 2743 §3.1) — that a Windows acceptor sends
    /// back as SPNEGO's responseToken (unlike its mechToken for the AP-REQ, which omits even the TOK_ID, the AP-REP
    /// keeps the full wrapper).
    /// </summary>
    public static byte[] UnwrapApRep(ReadOnlySpan<byte> token)
    {
        var bytes = token.ToArray();
        if (bytes.Length > 0 && bytes[0] == GssInitialContextTokenTag)
        {
            // [APPLICATION 0] SEQUENCE { mechOID, TOK_ID + message }: skip past the outer tag/length and the
            // OID TLV by hand (not with AsnReader, which has no way to hand back "everything after the OID"
            // as raw bytes rather than one more parsed value).
            var (_, outerContentStart) = ReadTagAndLength(bytes, 0);
            var (oidLength, oidContentStart) = ReadTagAndLength(bytes, outerContentStart);
            bytes = bytes[(oidContentStart + oidLength)..];
        }

        if (bytes.Length > 0 && bytes[0] == ApRepTag)
            return bytes; // already a bare AP-REP ([APPLICATION 15]), no TOK_ID at all.

        // The acceptor could not use the AP-REQ (a ticket for another host, a clock skew, ...) and says so with a
        // KRB-ERROR (TOK_ID 03 00) while still naming Kerberos: there is nothing left to negotiate.
        if (bytes.Length > 2 && bytes[0] == KrbErrorTokIdHigh && bytes[1] == KrbErrorTokIdLow &&
            bytes[2] == KrbErrorTag)
        {
            var error = KrbError.DecodeApplication(bytes.AsMemory(2));
            throw new SecurityProviderException(
                $"The server rejected the Kerberos ticket: KRB-ERROR {(int)error.ErrorCode} ({error.ErrorCode}){(string.IsNullOrEmpty(error.EText) ? string.Empty : $": {error.EText}")}.");
        }

        if (bytes.Length < 2 || !bytes.AsSpan(0, 2).SequenceEqual(ApRepTokId))
            throw new SecurityProviderException("Expected a GSS AP-REP token (TOK_ID 02 00).");

        return bytes[2..];
    }

    /// <summary>Reads one DER tag+length at <paramref name="offset"/> and returns (content length, content start offset).</summary>
    private static (int Length, int ContentStart) ReadTagAndLength(ReadOnlySpan<byte> data, int offset)
    {
        var lengthByte = data[offset + 1];
        if ((lengthByte & LongLengthFormFlag) == 0)
            return (lengthByte, offset + 2);

        var lengthByteCount = lengthByte & LengthByteCountMask;
        var length = 0;
        for (var i = 0; i < lengthByteCount; i++)
            length = (length << 8) | data[offset + 2 + i];

        return (length, offset + 2 + lengthByteCount);
    }
}

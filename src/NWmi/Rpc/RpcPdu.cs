using NWmi.Ndr;
using NWmi.Options;
using NWmi.Rpc.Pdu;

namespace NWmi.Rpc;

/// <summary>
/// A connection-oriented RPC PDU: the 16-byte common header (C706 12.6.1), a type-specific body and an
/// optional <see cref="RpcAuthTrailer"/>. Only little-endian ASCII/IEEE data (<c>packed_drep</c> 10 00 00 00)
/// and protocol version 5.0 are supported (MS-RPCE 3.3.1.5.1).
/// </summary>
internal abstract class RpcPdu
{
    public const int HeaderSize = 16;
    public const byte MajorVersion = 5;
    public const byte MinorVersion = 0;

    /// <summary>
    /// The padding before the sec_trailer makes the stub data plus padding a multiple of this many bytes (MS-RPCE
    /// 2.2.2.11), counted from the start of the stub for request and response PDUs, as Windows clients and
    /// servers do it (a Kerberos acceptor with header signing depends on it). Other PDUs
    /// count from the start of the PDU.
    /// </summary>
    public const int AuthAlignment = 16;

    private static readonly byte[] LittleEndianDrep = [0x10, 0x00, 0x00, 0x00];

    public abstract RpcPduType Type { get; }

    public uint CallId { get; set; }

    public RpcPfcFlags Flags { get; set; }

    /// <summary>Present when the PDU carries authentication data.</summary>
    public RpcAuthTrailer? Auth { get; set; }

    /// <summary>The flags as sent; subclasses add flags that follow from their content.</summary>
    protected virtual RpcPfcFlags WireFlags => Flags;

    protected abstract void WriteBody(NdrWriter writer);

    /// <summary>
    /// Bytes before the stub data: the common header plus the type's fixed fields. This is the "PDU header" that
    /// header signing covers. Only request and response PDUs, the ones that carry stub data, define it.
    /// </summary>
    internal virtual int StubOffset => throw new InvalidOperationException($"A {Type} PDU has no stub data.");

    /// <summary>Where the <see cref="AuthAlignment"/> count of the padding before the sec_trailer starts.</summary>
    private protected virtual int AuthPaddingOrigin => 0;

    public byte[] Serialize()
    {
        var w = new NdrWriter();
        w.WriteByte(MajorVersion);
        w.WriteByte(MinorVersion);
        w.WriteByte((byte)Type);
        w.WriteByte((byte)WireFlags);
        w.WriteBytes(LittleEndianDrep);
        w.WriteUInt16(0); // frag_length, patched below
        w.WriteUInt16(0); // auth_length, patched below
        w.WriteUInt32(CallId);
        WriteBody(w);

        ushort authLength = 0;
        if (Auth is { } auth)
        {
            if (auth.Value.Length > ushort.MaxValue)
                throw new RpcProtocolException("The authentication token is too large for one PDU.");

            var pad = -(w.Position - AuthPaddingOrigin) & (AuthAlignment - 1);
            w.WriteBytes(new byte[pad]);
            w.WriteByte((byte)auth.AuthType);
            w.WriteByte((byte)auth.AuthLevel);
            w.WriteByte((byte)pad);
            w.WriteByte(0);
            w.WriteUInt32(auth.ContextId);
            w.WriteBytes(auth.Value);
            authLength = (ushort)auth.Value.Length;
        }

        if (w.Position > ushort.MaxValue)
            throw new RpcProtocolException($"PDU of {w.Position} bytes exceeds the 65535-byte fragment limit.");

        w.PatchUInt16(8, (ushort)w.Position);
        w.PatchUInt16(10, authLength);
        return w.ToArray();
    }

    /// <summary>Parses one complete PDU. Any malformation raises <see cref="RpcProtocolException"/>.</summary>
    public static RpcPdu Parse(ReadOnlyMemory<byte> pdu)
    {
        try
        {
            return ParseCore(pdu);
        }
        catch (NdrFormatException ex)
        {
            throw new RpcProtocolException($"Malformed PDU: {ex.Message}", ex);
        }
    }

    /// <summary>Checks the first bytes of a PDU and returns its <c>frag_length</c>.</summary>
    public static int ReadFragmentLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            throw new RpcProtocolException("Truncated PDU header.");

        CheckDataRepresentation(header[4], header[5]);
        var length = header[8] | (header[9] << 8);
        if (length < HeaderSize)
            throw new RpcProtocolException($"PDU frag_length {length} is smaller than the header.");

        return length;
    }

    private static RpcPdu ParseCore(ReadOnlyMemory<byte> pdu)
    {
        var header = new NdrReader(pdu);
        var major = header.ReadByte();
        var minor = header.ReadByte();
        var type = (RpcPduType)header.ReadByte();
        var flags = (RpcPfcFlags)header.ReadByte();
        var drep0 = header.ReadByte();
        var drep1 = header.ReadByte();
        header.ReadUInt16();
        var fragLength = header.ReadUInt16();
        var authLength = header.ReadUInt16();
        var callId = header.ReadUInt32();

        CheckDataRepresentation(drep0, drep1);
        if (type != RpcPduType.BindNak && (major != MajorVersion || minor != MinorVersion))
            throw new RpcProtocolException($"Unsupported RPC protocol version {major}.{minor}.");

        if (fragLength != pdu.Length)
            throw new RpcProtocolException(
                $"PDU frag_length {fragLength} does not match the {pdu.Length} bytes received.");

        var bodyEnd = pdu.Length;
        RpcAuthTrailer? auth = null;
        if (authLength > 0)
        {
            var trailerStart = pdu.Length - authLength - 8;
            if (trailerStart < HeaderSize)
                throw new RpcProtocolException($"auth_length {authLength} does not fit in the PDU.");

            var trailer = new NdrReader(pdu[trailerStart..]);
            var authType = (RpcAuthType)trailer.ReadByte();
            var authLevel = (RpcAuthLevel)trailer.ReadByte();
            var padLength = trailer.ReadByte();
            trailer.ReadByte();
            var contextId = trailer.ReadUInt32();
            auth = new RpcAuthTrailer(authType, authLevel, contextId, trailer.ReadBytes(authLength))
                { PadLength = padLength };

            bodyEnd = trailerStart - padLength;
            if (bodyEnd < HeaderSize)
                throw new RpcProtocolException($"auth_pad_length {padLength} does not fit in the PDU.");
        }

        var body = new NdrReader(pdu[HeaderSize..bodyEnd]);
        RpcPdu result = type switch
        {
            RpcPduType.Request => RpcRequestPdu.ReadBody(body, flags),
            RpcPduType.Response => RpcResponsePdu.ReadBody(body),
            RpcPduType.Fault => RpcFaultPdu.ReadBody(body),
            RpcPduType.Bind or RpcPduType.AlterContext => RpcBindPdu.ReadBody(body, type),
            RpcPduType.BindAck or RpcPduType.AlterContextResponse => RpcBindAckPdu.ReadBody(body, type),
            RpcPduType.BindNak => RpcBindNakPdu.ReadBody(body),
            RpcPduType.Auth3 => new RpcAuth3Pdu(),
            _ => new RpcOtherPdu(type, body.ReadBytes(body.Remaining)),
        };

        result.CallId = callId;
        result.Flags = flags;
        result.Auth = auth;
        return result;
    }

    private static void CheckDataRepresentation(byte drep0, byte drep1)
    {
        if ((drep0 & 0xF0) != 0x10 || (drep0 & 0x0F) != 0 || drep1 != 0)
            throw new RpcProtocolException(
                $"Unsupported data representation {drep0:X2} {drep1:X2}; only little-endian ASCII IEEE is supported.");
    }
}

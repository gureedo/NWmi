using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NWmi.Auth.Ntlm;

/// <summary>NEGOTIATE_MESSAGE (MS-NLMP 2.2.1.1)</summary>
internal sealed class NtlmNegotiateMessage
{
    public required NtlmNegotiateFlags Flags
    {
        get;
        set
        {
            if (value.HasFlag(NtlmNegotiateFlags.OemDomainSupplied))
                throw new NotSupportedException("Sending a domain name in the NEGOTIATE_MESSAGE is not supported.");
            if (value.HasFlag(NtlmNegotiateFlags.OemWorkstationSupplied))
                throw new NotSupportedException(
                    "Sending a workstation name in the NEGOTIATE_MESSAGE is not supported.");
            field = value;
        }
    }

    public NtlmVersion? Version { get; set; }

    public byte[] Serialize()
    {
        var header = new MessageHeader(Flags, Version);
        var message = new byte[Unsafe.SizeOf<MessageHeader>()];
        Unsafe.WriteUnaligned(ref message[0], header);
        return message;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct MessageHeader
    {
        public readonly InlineArray8<byte> Signature;
        public readonly NtlmMessageType MessageType;
        public readonly NtlmNegotiateFlags NegotiateFlags;
        public readonly NtlmSecurityBuffer DomainNameFields;
        public readonly NtlmSecurityBuffer WorkstationFields;
        public readonly NtlmVersion Version;

        public MessageHeader(NtlmNegotiateFlags flags, NtlmVersion? version)
        {
            if (version != null)
                flags |= NtlmNegotiateFlags.Version;
            else
                flags &= ~NtlmNegotiateFlags.Version;

            var headerSize = Unsafe.SizeOf<MessageHeader>();
            NtlmConstants.MessageSignature.CopyTo(Signature);
            MessageType = NtlmMessageType.Negotiate;
            NegotiateFlags = flags;
            DomainNameFields = new NtlmSecurityBuffer(0, headerSize);
            WorkstationFields = new NtlmSecurityBuffer(0, headerSize);
            Version = version ?? default;
        }
    }
}

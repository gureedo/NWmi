using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NWmi.Auth.Ntlm;

/// <summary>AUTHENTICATE_MESSAGE (MS-NLMP 2.2.1.3)</summary>
internal sealed class NtlmAuthenticateMessage
{
    public const int MicOffset = 72;
    public const int MicSize = 16;

    public required NtlmNegotiateFlags Flags { get; set; }
    public required string Domain { get; set; }
    public required string Username { get; set; }
    public required string Workstation { get; set; }
    public required byte[] LmResponse { get; set; }
    public required byte[] NtResponse { get; set; }
    public required byte[] EncryptedSessionKey { get; set; }
    public NtlmVersion? Version { get; set; }

    public byte[] Serialize(bool withMic)
    {
        var domainBytes = Encoding.Unicode.GetBytes(Domain);
        var userBytes = Encoding.Unicode.GetBytes(Username);
        var workstationBytes = Encoding.Unicode.GetBytes(Workstation);

        var headerSize = Unsafe.SizeOf<MessageHeader>();
        if (withMic)
            headerSize += MicSize;
        var payloadLength = domainBytes.Length + userBytes.Length + workstationBytes.Length
                            + LmResponse.Length + NtResponse.Length + EncryptedSessionKey.Length;
        var message = new byte[headerSize + payloadLength];

        // The payload follows the header in the order of the MS-NLMP 4.2.4.3 example.
        var next = headerSize;
        var domainFields = Place(domainBytes);
        var userFields = Place(userBytes);
        var workstationFields = Place(workstationBytes);
        var lmFields = Place(LmResponse);
        var ntFields = Place(NtResponse);
        var keyFields = Place(EncryptedSessionKey);

        var header = new MessageHeader(lmFields, ntFields, domainFields, userFields, workstationFields, keyFields,
            Flags, Version);
        Unsafe.WriteUnaligned(ref message[0], header);
        return message;

        NtlmSecurityBuffer Place(byte[] data)
        {
            var fields = new NtlmSecurityBuffer(checked((ushort)data.Length), next);
            data.CopyTo(message, next);
            next += data.Length;
            return fields;
        }
    }

    public static void PatchMic(Span<byte> authenticateMessage, ReadOnlySpan<byte> mic) =>
        mic.CopyTo(authenticateMessage.Slice(MicOffset, MicSize));

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct MessageHeader
    {
        public readonly InlineArray8<byte> Signature;
        public readonly NtlmMessageType MessageType;
        public readonly NtlmSecurityBuffer LmChallengeResponseFields;
        public readonly NtlmSecurityBuffer NtChallengeResponseFields;
        public readonly NtlmSecurityBuffer DomainNameFields;
        public readonly NtlmSecurityBuffer UserNameFields;
        public readonly NtlmSecurityBuffer WorkstationFields;
        public readonly NtlmSecurityBuffer EncryptedRandomSessionKeyFields;
        public readonly NtlmNegotiateFlags NegotiateFlags;
        public readonly NtlmVersion Version;

        public MessageHeader(
            NtlmSecurityBuffer lmChallengeResponseFields,
            NtlmSecurityBuffer ntChallengeResponseFields,
            NtlmSecurityBuffer domainNameFields,
            NtlmSecurityBuffer userNameFields,
            NtlmSecurityBuffer workstationFields,
            NtlmSecurityBuffer encryptedRandomSessionKeyFields,
            NtlmNegotiateFlags flags,
            NtlmVersion? version)
        {
            if (version != null)
                flags |= NtlmNegotiateFlags.Version;
            else
                flags &= ~NtlmNegotiateFlags.Version;

            NtlmConstants.MessageSignature.CopyTo(Signature);
            MessageType = NtlmMessageType.Authenticate;
            LmChallengeResponseFields = lmChallengeResponseFields;
            NtChallengeResponseFields = ntChallengeResponseFields;
            DomainNameFields = domainNameFields;
            UserNameFields = userNameFields;
            WorkstationFields = workstationFields;
            EncryptedRandomSessionKeyFields = encryptedRandomSessionKeyFields;
            NegotiateFlags = flags;
            Version = version ?? default;
        }
    }
}

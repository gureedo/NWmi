using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NWmi.Auth.Ntlm;

/// <summary>CHALLENGE_MESSAGE (MS-NLMP 2.2.1.2)</summary>
internal sealed class NtlmChallengeMessage
{
    public required NtlmNegotiateFlags Flags { get; set; }

    public required byte[] ServerChallenge { get; set; }

    public required byte[] TargetName { get; set; }

    public required List<AvPair> TargetInfo { get; set; }

    public AvPair? Find(ushort id) => TargetInfo.Where(p => p.Id == id).Select(p => (AvPair?)p).FirstOrDefault();

    public static NtlmChallengeMessage Deserialize(ReadOnlySpan<byte> message)
    {
        if (message.Length < Unsafe.SizeOf<MessageHeader>())
            throw new SecurityProviderException("The server token is not an NTLM message.");

        var header = MemoryMarshal.Read<MessageHeader>(message);
        if (!((ReadOnlySpan<byte>)header.Signature).SequenceEqual(NtlmConstants.MessageSignature))
            throw new SecurityProviderException("The server token is not an NTLM message.");

        if (header.MessageType != NtlmMessageType.Challenge)
            throw new SecurityProviderException(
                $"Expected an NTLM CHALLENGE_MESSAGE but received message type {header.MessageType}.");

        var targetInfoBytes = header.TargetInfoFields.Read(message);
        return new NtlmChallengeMessage
        {
            Flags = header.Flags,
            ServerChallenge = [.. (ReadOnlySpan<byte>)header.ServerChallenge],
            TargetName = [.. header.TargetNameFields.Read(message)],
            TargetInfo = targetInfoBytes.Length == 0 ? [] : AvPair.ParseList(targetInfoBytes),
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct MessageHeader
    {
        public readonly InlineArray8<byte> Signature;
        public readonly NtlmMessageType MessageType;
        public readonly NtlmSecurityBuffer TargetNameFields;
        public readonly NtlmNegotiateFlags Flags;
        public readonly InlineArray8<byte> ServerChallenge;
        public readonly InlineArray8<byte> Reserved;
        public readonly NtlmSecurityBuffer TargetInfoFields;
        public readonly NtlmVersion Version;
    }
}

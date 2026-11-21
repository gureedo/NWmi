using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NWmi.Auth;
using NWmi.Auth.Ntlm;

#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms

namespace NWmi.Tests.Auth;

/// <summary>
/// The server half of NTLMv2 for tests: it issues a challenge, verifies the client's response (and MIC), and
/// then protects messages with the server-side keys. Written from MS-NLMP 3.2.5, independently of the client code
/// path where practical.
/// </summary>
internal sealed class NtlmTestServer
{
    private readonly string _password;
    private byte[]? _negotiate;
    private byte[]? _challengeMessage;
    private byte[] _serverChallenge = [];
    private NtlmNegotiateFlags _flags;

    public NtlmTestServer(string password)
    {
        _password = password;
    }

    /// <summary>Include MsvAvTimestamp in the target info, which makes the client send a MIC.</summary>
    public bool SendTimestamp { get; set; } = true;

    /// <summary>Flags the server refuses to grant even when the client asks.</summary>
    public NtlmNegotiateFlags Withhold { get; set; }

    public NtlmSession? Session { get; private set; }

    public string? AuthenticatedUser { get; private set; }

    public string? AuthenticatedDomain { get; private set; }

    public bool MicWasPresent { get; private set; }

    public byte[] ProcessNegotiate(byte[] negotiate)
    {
        _negotiate = negotiate;
        var requested = (NtlmNegotiateFlags)BinaryPrimitives.ReadUInt32LittleEndian(negotiate.AsSpan(12));
        _flags = (requested & ~Withhold)
                 | NtlmNegotiateFlags.Unicode | NtlmNegotiateFlags.TargetInfo | NtlmNegotiateFlags.TargetTypeServer |
                 NtlmNegotiateFlags.Ntlm;
        _flags &= ~NtlmNegotiateFlags.RequestTarget;
        _serverChallenge = RandomNumberGenerator.GetBytes(8);

        var pairs = new List<AvPair>
        {
            AvPair.Text(AvId.NbDomainName, "DOMAIN"),
            AvPair.Text(AvId.NbComputerName, "SERVER"),
        };
        if (SendTimestamp)
            pairs.Add(new AvPair(AvId.Timestamp, BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc())));

        var targetInfo = AvPair.SerializeList(pairs);
        var targetName = Encoding.Unicode.GetBytes("SERVER");
        var message = new byte[56 + targetName.Length + targetInfo.Length];
        "NTLMSSP\0"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 2);
        Field(message, 12, targetName.Length, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), (uint)_flags);
        _serverChallenge.CopyTo(message, 24);
        Field(message, 40, targetInfo.Length, 56 + targetName.Length);
        targetName.CopyTo(message, 56);
        targetInfo.CopyTo(message, 56 + targetName.Length);

        _challengeMessage = message;
        return message;
    }

    /// <summary>Checks the AUTHENTICATE_MESSAGE; throws <see cref="SecurityProviderException"/> if the client is not who it claims.</summary>
    public void ProcessAuthenticate(byte[] authenticate)
    {
        var flags = (NtlmNegotiateFlags)BinaryPrimitives.ReadUInt32LittleEndian(authenticate.AsSpan(60));
        var ntResponse = ReadField(authenticate, 20);
        var domain = Encoding.Unicode.GetString(ReadField(authenticate, 28));
        var user = Encoding.Unicode.GetString(ReadField(authenticate, 36));
        var encryptedKey = ReadField(authenticate, 52);

        var responseKey = NtlmV2Response.ResponseKey(_password, user, domain);
        var blob = ntResponse[16..];
        byte[] proofInput = [.. _serverChallenge, .. blob];
        var expectedProof = HMACMD5.HashData(responseKey, proofInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedProof, ntResponse.AsSpan(0, 16)))
            throw new SecurityProviderException("Logon failure: the NT response does not match.");

        var sessionBaseKey = HMACMD5.HashData(responseKey, expectedProof);
        var exportedKey = (flags & NtlmNegotiateFlags.KeyExchange) != 0
            ? Rc4.Crypt(sessionBaseKey, encryptedKey)
            : sessionBaseKey;

        // The MsvAvFlags attribute inside the blob says whether a MIC is present.
        var pairs = AvPair.ParseList(blob.AsSpan(28));
        var avFlags = pairs.Where(p => p.Id == AvId.Flags).Select(p => BitConverter.ToUInt32(p.Value)).FirstOrDefault();
        MicWasPresent = (avFlags & AvId.FlagMicProvided) != 0;
        if (MicWasPresent)
        {
            var zeroed = (byte[])authenticate.Clone();
            zeroed.AsSpan(NtlmAuthenticateMessage.MicOffset, NtlmAuthenticateMessage.MicSize).Clear();
            byte[] transcript = [.. _negotiate!, .. _challengeMessage!, .. zeroed];
            var mic = HMACMD5.HashData(exportedKey, transcript);
            if (!CryptographicOperations.FixedTimeEquals(mic,
                    authenticate.AsSpan(NtlmAuthenticateMessage.MicOffset, NtlmAuthenticateMessage.MicSize)))
                throw new SecurityProviderException("Logon failure: the MIC does not match.");
        }

        AuthenticatedUser = user;
        AuthenticatedDomain = domain;
        if ((flags & (NtlmNegotiateFlags.Sign | NtlmNegotiateFlags.Seal)) != 0)
            Session = new NtlmSession(exportedKey, flags, isClient: false);
    }

    private static void Field(byte[] message, int fieldOffset, int length, int dataOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(fieldOffset), (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(fieldOffset + 2), (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(fieldOffset + 4), (uint)dataOffset);
    }

    private static byte[] ReadField(byte[] message, int fieldOffset)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(fieldOffset));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(fieldOffset + 4));
        return [.. message.AsSpan((int)offset, length)];
    }
}

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms

namespace NWmi.Auth.Ntlm;

/// <summary>NTLM v2 challenge response (MS-NLMP 3.3.2).</summary>
internal sealed record NtlmV2Response(byte[] NtResponse, byte[] LmResponse, byte[] SessionBaseKey)
{
    /// <summary>NTOWFv2: HMAC_MD5(MD4(UNICODE(password)), UNICODE(UPPER(user) + domain)).</summary>
    public static byte[] ResponseKey(string password, string userName, string domain) =>
        HMACMD5.HashData(Md4.HashData(Encoding.Unicode.GetBytes(password)),
            Encoding.Unicode.GetBytes(userName.ToUpperInvariant() + domain));

    /// <summary>
    /// Computes the NT and LM responses and the session base key.
    /// The blob contains an 8-byte <c>timestamp</c> (a FILETIME), the client's 8-byte challenge and <c>targetInfo</c>, the AV pairs including MsvAvEOL.
    /// When <c>sendLmResponse</c> is false the LM response is 24 zero bytes.
    /// </summary>
    public static NtlmV2Response ComputeResponse(
        ReadOnlySpan<byte> responseKey,
        ReadOnlySpan<byte> serverChallenge,
        ReadOnlySpan<byte> clientChallenge,
        ulong timestamp,
        ReadOnlySpan<byte> targetInfo,
        bool sendLmResponse)
    {
        if (clientChallenge.Length != Unsafe.SizeOf<InlineArray8<byte>>())
            throw new ArgumentException("The client challenge must be 8 bytes.", nameof(clientChallenge));

        const int proofSize = 16; // HMAC-MD5 output
        var headerSize = Unsafe.SizeOf<ClientChallengeHeader>();

        // NtResponse = NTProofStr(16) || temp
        // temp = Responserversion, HiResponserversion, Z(6), Time, ClientChallenge, Z(4), ServerName, Z(4)
        var writer = new ArrayBufferWriter<byte>(proofSize + headerSize + targetInfo.Length + sizeof(uint));

        // Reserve space for NTProofStr; it is computed over the temp that follows.
        writer.GetSpan(proofSize)[..proofSize].Clear();
        writer.Advance(proofSize);

        var header = new ClientChallengeHeader
        {
            RespType = 1,
            HiRespType = 1,
            TimeStamp = timestamp,
        };
        clientChallenge.CopyTo(header.ChallengeFromClient);
        MemoryMarshal.Write(writer.GetSpan(headerSize), in header);
        writer.Advance(headerSize);

        writer.Write(targetInfo);

        writer.GetSpan(sizeof(uint))[..sizeof(uint)].Clear();
        writer.Advance(sizeof(uint));

        var ntResponse = writer.WrittenSpan.ToArray();

        // NTProofStr = HMAC_MD5(ResponseKey, ServerChallenge || temp)
        var ntProof = ntResponse.AsSpan(0, proofSize);
        using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, responseKey))
        {
            hmac.AppendData(serverChallenge);
            hmac.AppendData(ntResponse.AsSpan(proofSize));
            hmac.GetHashAndReset(ntProof);
        }

        var lmResponse = new byte[24];
        if (sendLmResponse)
        {
            lmResponse.AsSpan().Clear();
            writer.ResetWrittenCount();
            writer.Write(serverChallenge);
            writer.Write(clientChallenge);
            HMACMD5.HashData(responseKey, writer.WrittenSpan, lmResponse);
            clientChallenge.CopyTo(lmResponse.AsSpan(proofSize));
        }

        var sessionBaseKey = HMACMD5.HashData(responseKey, ntProof);
        return new NtlmV2Response(ntResponse, lmResponse, sessionBaseKey);
    }

    /// <summary>NTLMv2_CLIENT_CHALLENGE (MS-NLMP 2.2.2.7)</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ClientChallengeHeader
    {
        public byte RespType;
        public byte HiRespType;
        public InlineArray2<byte> Reserved1;
        public InlineArray4<byte> Reserved2;
        public ulong TimeStamp;
        public InlineArray8<byte> ChallengeFromClient;
        public InlineArray4<byte> Reserved3;
    }
}

using System.Text;
using NWmi.Auth;
using NWmi.Auth.Ntlm;

namespace NWmi.Tests.Auth;

/// <summary>Checks the NTLMv2 code against the worked example in MS-NLMP section 4.2.4.</summary>
public class NtlmSpecTests
{
    private static readonly byte[] ServerChallenge = Convert.FromHexString("0123456789abcdef");
    private static readonly byte[] ClientChallenge = Convert.FromHexString("aaaaaaaaaaaaaaaa");
    private const ulong Time = 0;
    private static readonly byte[] RandomSessionKey = [.. Enumerable.Repeat((byte)0x55, 16)];

    // Challenge flags of the example: 0xe28a8233; the client's flags in the AUTHENTICATE_MESSAGE: 0xe2888235.
    private const NtlmNegotiateFlags AuthenticateFlags = (NtlmNegotiateFlags)0xe2888235;

    private static byte[] Hex(string text) => Convert.FromHexString(text);

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static NtlmChallengeMessage SpecChallenge() =>
        NtlmChallengeMessage.Deserialize(Hex(NtlmSpecVectors.Challenge));

    [Fact]
    public void Response_key_matches_NTOWFv2()
    {
        Assert.Equal("0c868a403bfd7a93a3001ef22ef02e3f", ToHex(NtlmV2Response.ResponseKey("Password", "User", "Domain")));
    }

    [Fact]
    public void Challenge_message_is_parsed()
    {
        var challenge = SpecChallenge();

        Assert.Equal((NtlmNegotiateFlags)0xe28a8233, challenge.Flags);
        Assert.Equal(ServerChallenge, challenge.ServerChallenge);
        Assert.Equal("Server", Encoding.Unicode.GetString(challenge.TargetName));
        Assert.Equal(2, challenge.TargetInfo.Count);
        Assert.Equal("Domain", Encoding.Unicode.GetString(challenge.Find(AvId.NbDomainName)!.Value.Value));
        Assert.Equal("Server", Encoding.Unicode.GetString(challenge.Find(AvId.NbComputerName)!.Value.Value));
        Assert.Null(challenge.Find(AvId.Timestamp));
    }

    [Fact]
    public void Target_info_serializes_back_to_the_same_bytes()
    {
        var challenge = SpecChallenge();
        var targetInfo = AvPair.SerializeList(challenge.TargetInfo);

        // The example's target info is the last 36 bytes of the challenge message.
        Assert.Equal(Hex(NtlmSpecVectors.Challenge)[^36..], targetInfo);
    }

    [Fact]
    public void Responses_and_session_key_match_the_example()
    {
        var responseKey = NtlmV2Response.ResponseKey("Password", "User", "Domain");
        var targetInfo = AvPair.SerializeList(SpecChallenge().TargetInfo);

        var response = NtlmV2Response.ComputeResponse(responseKey, ServerChallenge, ClientChallenge, Time, targetInfo,
            sendLmResponse: true);

        Assert.Equal("8de40ccadbc14a82f15cb0ad0de95ca3", ToHex(response.SessionBaseKey));
        Assert.Equal("86c35097ac9cec102554764a57cccc19aaaaaaaaaaaaaaaa", ToHex(response.LmResponse));
        Assert.Equal("68cd0ab851e51c96aabc927bebef6a1c", ToHex(response.NtResponse[..16]));

        // The rest of the NT response is the client blob (temp) shown in 4.2.4.1.3; the whole response is 84 bytes.
        Assert.Equal(84, response.NtResponse.Length);
        Assert.Equal(Hex(NtlmSpecVectors.Authenticate)[0x84..0xd8], response.NtResponse);
    }

    [Fact]
    public void Random_session_key_is_encrypted_with_the_key_exchange_key()
    {
        var sessionBaseKey =
            Hex("8de40ccadbc14a82f15cb0ad0de95ca3"); // for NTLMv2 the key exchange key is the session base key
        Assert.Equal("c5dad2544fc9799094ce1ce90bc9d03e", ToHex(Rc4.Crypt(sessionBaseKey, RandomSessionKey)));
    }

    [Fact]
    public void Authenticate_message_is_byte_exact()
    {
        var responseKey = NtlmV2Response.ResponseKey("Password", "User", "Domain");
        var targetInfo = AvPair.SerializeList(SpecChallenge().TargetInfo);
        var response = NtlmV2Response.ComputeResponse(responseKey, ServerChallenge, ClientChallenge, Time, targetInfo,
            sendLmResponse: true);

        var message = new NtlmAuthenticateMessage
        {
            Flags = AuthenticateFlags,
            Domain = "Domain",
            Username = "User",
            Workstation = "COMPUTER",
            LmResponse = response.LmResponse,
            NtResponse = response.NtResponse,
            EncryptedSessionKey = Rc4.Crypt(response.SessionBaseKey, RandomSessionKey),
            Version = new NtlmVersion(5, 1, 2600)
        }.Serialize(withMic: false);

        Assert.Equal(NtlmSpecVectors.Authenticate, ToHex(message));
    }

    [Fact]
    public void Authenticate_message_with_a_mic_field_is_16_bytes_longer_and_the_mic_can_be_patched()
    {
        var plain = Build(false);
        var withMic = Build(true);
        Assert.Equal(plain.Length + 16, withMic.Length);

        NtlmAuthenticateMessage.PatchMic(withMic, [.. Enumerable.Repeat((byte)0xAB, 16)]);
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 16).ToArray(), withMic[72..88]);

        // The payload fields moved by the same 16 bytes.
        Assert.Equal(88u, BitConverter.ToUInt32(withMic, 28 + 4));
        Assert.Equal(72u, BitConverter.ToUInt32(plain, 28 + 4));
        return;

        static byte[] Build(bool withMic) => new NtlmAuthenticateMessage
        {
            Flags = AuthenticateFlags,
            Domain = "Domain", Username = "User",
            Workstation = "COMPUTER",
            LmResponse = new byte[24],
            NtResponse = new byte[40],
            EncryptedSessionKey = new byte[16],
            Version = NtlmVersion.Default,
        }.Serialize(withMic);
    }

    [Fact]
    public void Negotiate_message_layout()
    {
        const NtlmNegotiateFlags flags = (NtlmNegotiateFlags)0xe2888235;
        var message = new NtlmNegotiateMessage
        {
            Flags = flags,
            Version = new NtlmVersion(10, 0, 19041)
        }.Serialize();

        Assert.Equal(40, message.Length);
        Assert.Equal("NTLMSSP\0", Encoding.ASCII.GetString(message[..8]));
        Assert.Equal(1u, BitConverter.ToUInt32(message, 8));
        Assert.Equal(0xe2888235u, BitConverter.ToUInt32(message, 12));
        Assert.Equal(40u, BitConverter.ToUInt32(message, 20)); // empty domain: offset just past the header
        Assert.Equal(40u, BitConverter.ToUInt32(message, 28)); // empty workstation
        Assert.Equal("0a00" + "614a" + "000000" + "0f", ToHex(message[32..40]));
    }

    [Fact]
    public void Sealing_matches_the_GSS_WrapEx_example()
    {
        var plaintext = Encoding.Unicode.GetBytes("Plaintext");
        Assert.Equal("59f600973cc4960a25480a7c196e4c58",
            ToHex(NtlmSession.SealKey(RandomSessionKey, AuthenticateFlags, clientToServer: true)));
        Assert.Equal("4788dc861b4782f35d43fd98fe1a2d39",
            ToHex(NtlmSession.SignKey(RandomSessionKey, clientToServer: true)));

        var client = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: true);
        var body = (byte[])plaintext.Clone();
        var signature = new byte[16];
        client.Protect(seal: true, [], body, [], signature);

        Assert.Equal("54e50165bf1936dc996020c1811b0f06fb5f", ToHex(body));
        Assert.Equal("010000007fb38ec5c55d497600000000", ToHex(signature));

        // A server with the same keys reads it back.
        var server = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: false);
        server.Unprotect(unseal: true, [], body, [], signature);
        Assert.Equal(plaintext, body);
    }

    [Fact]
    public void Messages_are_numbered_and_the_keystream_runs_on_in_both_directions()
    {
        var client = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: true);
        var server = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: false);

        for (var i = 0; i < 5; i++)
        {
            var request = Encoding.ASCII.GetBytes($"request {i}");
            var sealedRequest = (byte[])request.Clone();
            var requestSignature = new byte[16];
            client.Protect(true, [], sealedRequest, [], requestSignature);
            Assert.Equal((uint)i, BitConverter.ToUInt32(requestSignature, 12));
            server.Unprotect(true, [], sealedRequest, [], requestSignature);
            Assert.Equal(request, sealedRequest);

            var reply = Encoding.ASCII.GetBytes($"reply {i}");
            var sealedReply = (byte[])reply.Clone();
            var replySignature = new byte[16];
            server.Protect(true, [], sealedReply, [], replySignature);
            client.Unprotect(true, [], sealedReply, [], replySignature);
            Assert.Equal(reply, sealedReply);
        }
    }

    [Fact]
    public void Sign_only_leaves_the_body_readable_and_detects_tampering()
    {
        var client = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: true);
        var server = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: false);

        var body = Encoding.ASCII.GetBytes("visible but protected");
        var original = (byte[])body.Clone();
        var signature = new byte[16];
        client.Protect(false, [], body, [], signature);
        Assert.Equal(original, body);

        body[0] ^= 1;
        Assert.Throws<SecurityProviderException>(() => server.Unprotect(false, [], body, [], signature));
    }

    [Fact]
    public void Header_and_trailer_segments_are_signed_but_not_encrypted()
    {
        var client = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: true);
        var header = new byte[] { 1, 2, 3, 4 };
        var trailer = new byte[] { 9, 8 };
        var body = Encoding.ASCII.GetBytes("secret");
        var signature = new byte[16];
        client.Protect(true, header, body, trailer, signature);

        var tamperedHeader = new byte[] { 1, 2, 3, 5 };
        var server = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: false);
        var copy = (byte[])body.Clone();
        Assert.Throws<SecurityProviderException>(() =>
            server.Unprotect(true, tamperedHeader, copy, trailer, signature));

        var server2 = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: false);
        server2.Unprotect(true, header, body, trailer, signature);
        Assert.Equal("secret", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public void Replayed_or_reordered_messages_are_rejected()
    {
        var client = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: true);
        var server = new NtlmSession(RandomSessionKey, AuthenticateFlags, isClient: false);

        var first = new byte[] { 1 };
        var firstSignature = new byte[16];
        client.Protect(false, [], first, [], firstSignature);
        var second = new byte[] { 2 };
        var secondSignature = new byte[16];
        client.Protect(false, [], second, [], secondSignature);

        Assert.Throws<SecurityProviderException>(() =>
            server.Unprotect(false, [], second, [], secondSignature)); // skipped one
        server.Unprotect(false, [], first, [], firstSignature);
        Assert.Throws<SecurityProviderException>(() =>
            server.Unprotect(false, [], first, [], firstSignature)); // replay
    }

    [Fact]
    public void Signing_needs_extended_session_security()
    {
        Assert.Throws<SecurityProviderException>(() =>
            new NtlmSession(RandomSessionKey, NtlmNegotiateFlags.Sign | NtlmNegotiateFlags.Ntlm, isClient: true));
    }

    [Fact]
    public void Malformed_server_tokens_are_rejected()
    {
        Assert.Throws<SecurityProviderException>(() => NtlmChallengeMessage.Deserialize(new byte[10]));
        Assert.Throws<SecurityProviderException>(() => NtlmChallengeMessage.Deserialize(new byte[60]));

        var truncated = Hex(NtlmSpecVectors.Challenge)[..80]; // target info cut short
        Assert.Throws<SecurityProviderException>(() => NtlmChallengeMessage.Deserialize(truncated));

        var wrongType = Hex(NtlmSpecVectors.Challenge);
        wrongType[8] = 3;
        Assert.Throws<SecurityProviderException>(() => NtlmChallengeMessage.Deserialize(wrongType));
    }

    [Fact]
    public void Corrupt_challenges_only_ever_throw_SecurityProviderException()
    {
        var original = Hex(NtlmSpecVectors.Challenge);
        var random = new Random(4242);
        for (var round = 0; round < 5000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 5); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                NtlmChallengeMessage.Deserialize(mutated);
            }
            catch (SecurityProviderException)
            {
                // The expected outcome for bad input; any other exception type fails the test.
            }
        }
    }
}

using System.Security.Cryptography;
using NWmi.Auth;
using NWmi.Auth.Ntlm;

namespace NWmi.Tests.Auth;

/// <summary>The SPNEGO mechListMIC over an NTLM session (MS-SPNG 3.3.5.1).</summary>
public class NtlmMechListMicTests
{
    private const NtlmNegotiateFlags Flags = NtlmNegotiateFlags.ExtendedSessionSecurity | NtlmNegotiateFlags.Sign |
                                             NtlmNegotiateFlags.Seal | NtlmNegotiateFlags.KeyExchange |
                                             NtlmNegotiateFlags.Bits128;

    private static readonly byte[] MechTypes = Convert.FromHexString("300c060a2b06010401823702020a");

    private static (NtlmSession Client, NtlmSession Server) Pair(NtlmNegotiateFlags flags = Flags)
    {
        var key = RandomNumberGenerator.GetBytes(16);
        return (new NtlmSession(key, flags, isClient: true), new NtlmSession(key, flags, isClient: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_mic_made_by_one_side_verifies_on_the_other(bool keyExchange)
    {
        var flags = keyExchange ? Flags : Flags & ~NtlmNegotiateFlags.KeyExchange;
        var (client, server) = Pair(flags);

        server.VerifyMic(MechTypes, client.MakeMic(MechTypes));
        client.VerifyMic(MechTypes, server.MakeMic(MechTypes));
    }

    [Fact]
    public void A_mic_is_a_signature_for_sequence_zero()
    {
        var (client, _) = Pair();

        var mic = client.MakeMic(MechTypes);

        Assert.Equal(NtlmSession.SignatureSize, mic.Length);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, mic.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[4], mic.AsSpan(12).ToArray());
    }

    [Fact]
    public void A_mic_over_different_data_or_in_the_wrong_direction_is_refused()
    {
        var (client, server) = Pair();
        var mic = client.MakeMic(MechTypes);

        Assert.Throws<SecurityProviderException>(() => server.VerifyMic([.. MechTypes, 0], mic));
        Assert.Throws<SecurityProviderException>(() => client.VerifyMic(MechTypes, mic)); // our own MIC is not the server's
        Assert.Throws<SecurityProviderException>(() => server.VerifyMic(MechTypes, mic.AsSpan(0, 15)));
    }

    [Fact]
    public void Messages_after_the_mics_continue_at_sequence_one_and_still_verify()
    {
        var (client, server) = Pair();
        server.VerifyMic(MechTypes, client.MakeMic(MechTypes));
        client.VerifyMic(MechTypes, server.MakeMic(MechTypes));

        var body = "hello"u8.ToArray();
        var signature = new byte[NtlmSession.SignatureSize];
        client.Protect(true, [], body, [], signature);

        Assert.Equal(1u, BitConverter.ToUInt32(signature, 12));
        server.Unprotect(true, [], body, [], signature);
        Assert.Equal("hello"u8.ToArray(), body);
    }
}

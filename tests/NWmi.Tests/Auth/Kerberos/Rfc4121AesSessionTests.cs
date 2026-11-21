using NWmi.Auth;
using NWmi.Auth.Kerberos;

namespace NWmi.Tests.Auth.Kerberos;

public class Rfc4121AesSessionTests
{
    private static byte[] SampleKey(KerberosEncryptionType type) => Aes3961.StringToKey(type, "P@ssw0rd!", "EXAMPLE.COMalice");

    public static IEnumerable<object[]> Types() =>
    [
        [(int)KerberosEncryptionType.Aes128CtsHmacSha1],
        [(int)KerberosEncryptionType.Aes256CtsHmacSha1],
    ];

    [Theory]
    [MemberData(nameof(Types))]
    public void Integrity_only_round_trips_without_changing_the_body(int etype)
    {
        var type = (KerberosEncryptionType)etype;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        var body = "hello, world"u8.ToArray();
        var original = body.ToArray();
        var signature = new byte[Rfc4121AesSession.UnsealedSignatureSize];
        client.Protect(seal: false, [], body, [], signature);

        Assert.Equal(original, body); // integrity-only: the body is untouched
        server.Unprotect(unseal: false, [], body, [], signature);
        Assert.Equal(original, body);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Privacy_round_trips_and_the_wire_bytes_are_not_the_plaintext(int etype)
    {
        var type = (KerberosEncryptionType)etype;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        var original = "a secret request"u8.ToArray();
        var body = original.ToArray();
        var signature = new byte[Rfc4121AesSession.SignatureSize];
        client.Protect(seal: true, [], body, [], signature);

        Assert.NotEqual(original, body);
        server.Unprotect(unseal: true, [], body, [], signature);
        Assert.Equal(original, body);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Privacy_round_trips_for_lengths_near_the_block_boundary(int etype)
    {
        var type = (KerberosEncryptionType)etype;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        for (var length = 0; length <= 40; length++)
        {
            var original = new byte[length];
            System.Security.Cryptography.RandomNumberGenerator.Fill(original);
            var body = original.ToArray();
            var signature = new byte[Rfc4121AesSession.SignatureSize];
            client.Protect(seal: true, [], body, [], signature);
            server.Unprotect(unseal: true, [], body, [], signature);
            Assert.Equal(original, body);
        }
    }

    [Fact]
    public void Each_direction_tracks_its_own_sequence_number()
    {
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        for (var i = 0; i < 3; i++)
        {
            var body = new byte[] { (byte)i };
            var signature = new byte[Rfc4121AesSession.SignatureSize];
            client.Protect(seal: true, [], body, [], signature);
            server.Unprotect(unseal: true, [], body, [], signature);
            Assert.Equal(i, body[0]);
        }

        for (var i = 0; i < 3; i++)
        {
            var body = new byte[] { (byte)(100 + i) };
            var signature = new byte[Rfc4121AesSession.SignatureSize];
            server.Protect(seal: true, [], body, [], signature);
            client.Unprotect(unseal: true, [], body, [], signature);
            Assert.Equal(100 + i, body[0]);
        }
    }

    [Fact]
    public void An_altered_body_is_rejected()
    {
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        var body = "integrity matters"u8.ToArray();
        var signature = new byte[Rfc4121AesSession.UnsealedSignatureSize];
        client.Protect(seal: false, [], body, [], signature);
        body[0] ^= 0xFF;

        Assert.Throws<SecurityProviderException>(() => server.Unprotect(unseal: false, [], body, [], signature));
    }

    [Fact]
    public void Before_and_after_segments_are_covered_by_the_signature_but_not_encrypted()
    {
        // RPC header signing: the PDU header and the sec_trailer are signed, not encrypted (MS-RPCE 3.3.1.5.2.2).
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        var before = "HDR"u8.ToArray();
        var after = "TRAILER"u8.ToArray();

        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var body = "body"u8.ToArray();
        var signature = new byte[Rfc4121AesSession.SignatureSize];
        client.Protect(seal: true, before, body, after, signature);
        Assert.Equal("HDR"u8.ToArray(), before);
        Assert.NotEqual("body"u8.ToArray(), body);

        var server = new Rfc4121AesSession(type, key, isInitiator: false);
        server.Unprotect(unseal: true, before, body, after, signature);
        Assert.Equal("body"u8.ToArray(), body);

        foreach (var (tamperedBefore, tamperedAfter) in new[] { ("XXX"u8.ToArray(), after), (before, "XXXXXXX"u8.ToArray()), ([], after), (before, (byte[])[]) })
        {
            var again = new Rfc4121AesSession(type, key, isInitiator: true);
            var sealedBody = "body"u8.ToArray();
            var token = new byte[Rfc4121AesSession.SignatureSize];
            again.Protect(seal: true, before, sealedBody, after, token);
            var peer = new Rfc4121AesSession(type, key, isInitiator: false);
            Assert.Throws<SecurityProviderException>(() => peer.Unprotect(unseal: true, tamperedBefore, sealedBody, tamperedAfter, token));
        }
    }

    // Tokens made by Windows' own SSPI (EncryptMessage on a loopback Kerberos context with an AES256 session key, on a
    // Server 2025 domain controller), for the buffers a header-signing RPC client passes: PDU header, stub, sec_trailer, token.
    private static readonly byte[] WindowsKey = Convert.FromHexString("1E8F8A6376670947D001E52D75400C500FF583D16E3A2A9AD7BC2F2AB1C52AA1");
    private static readonly byte[] WindowsPduHeader = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7");
    private static readonly byte[] WindowsSecTrailer = Convert.FromHexString("0906000000000000");
    private static readonly byte[] WindowsPlaintext = "0123456789ABCDEF0123456789ABCDEF0123"u8.ToArray();

    [Fact]
    public void A_windows_token_without_header_signing_is_accepted()
    {
        // Header buffers READONLY (not covered): EC=0, so the token is 60 bytes; an acceptor declares and zero-pads to 76.
        var data = Convert.FromHexString("706588D517B04CE7FF5FBA74C169E471EF016C390B4843AFAC3F0C17F93FB6CBC90CA37A");
        var token = Convert.FromHexString(
            "050406FF0000001C0000000028FF9C02537089E212D0939C9B31A380552759AEA91F79A636BD3A82E5045F85BD81B5DEE8F8DE89693AA5AD3427567B");
        Assert.Equal(60, token.Length);

        var session = new Rfc4121AesSession(KerberosEncryptionType.Aes256CtsHmacSha1, WindowsKey, isInitiator: false,
            usesAcceptorSubkey: true, initialReceiveSequence: 0x28FF9C02);
        session.Unprotect(unseal: true, [], data, [], [.. token, .. new byte[Rfc4121AesSession.SignatureSize - token.Length]]);

        Assert.Equal(WindowsPlaintext, data);
    }

    [Fact]
    public void A_windows_token_with_header_signing_is_accepted_only_with_the_header_and_trailer_it_covered()
    {
        // Header buffers READONLY_WITH_CHECKSUM: EC=16, RRC=28, 76 bytes: clearhdr | filler | enchdr | checksum | confounder.
        var encrypted = Convert.FromHexString("206625305A4F0055D77631DA777189F1D589523C6D016E9ADED15782BBA712E84E2FEA43");
        var token = Convert.FromHexString(
            "050406FF0010001C0000000028FF9C03812F4F7CD2C490B10226B0200CE4EEAFD4F7D7985026730E2F47FF40682AE22D420011064B9B24AFA869378B27807813574BD94383B673657969B42A");
        Assert.Equal(Rfc4121AesSession.SignatureSize, token.Length);

        Rfc4121AesSession Session() => new(KerberosEncryptionType.Aes256CtsHmacSha1, WindowsKey, isInitiator: false,
            usesAcceptorSubkey: true, initialReceiveSequence: 0x28FF9C03);

        Assert.Throws<SecurityProviderException>(() => Session().Unprotect(true, [], (byte[])encrypted.Clone(), [], token));
        Assert.Throws<SecurityProviderException>(() => Session().Unprotect(true, WindowsPduHeader, (byte[])encrypted.Clone(), [], token));

        var data = (byte[])encrypted.Clone();
        Session().Unprotect(unseal: true, WindowsPduHeader, data, WindowsSecTrailer, token);
        Assert.Equal(WindowsPlaintext, data);
    }

    // MIC tokens made by Windows' own SSPI (MakeSignature, the same kind of loopback context with an AES256 session key),
    // which is what the RPC runtime uses at integrity level; buffers: PDU header, 32-byte stub, sec_trailer, token.
    private static readonly byte[] WindowsMicKey = Convert.FromHexString("5F7EA1A4DA3382B0F222472AB33154518BAAB758B10879608088F9921A810725");
    private static readonly byte[] WindowsStub = "0123456789ABCDEF0123456789ABCDEF"u8.ToArray();

    [Fact]
    public void Windows_mic_tokens_are_accepted_with_and_without_header_signing()
    {
        var session = new Rfc4121AesSession(KerberosEncryptionType.Aes256CtsHmacSha1, WindowsMicKey, isInitiator: false,
            usesAcceptorSubkey: true, initialReceiveSequence: 0x02FC469D);

        session.Unprotect(unseal: false, [], (byte[])WindowsStub.Clone(), [],
            Convert.FromHexString("040404FFFFFFFFFF0000000002FC469D664F5BEB22C0723505F98ECE"));
        session.Unprotect(unseal: false, WindowsPduHeader, (byte[])WindowsStub.Clone(), WindowsSecTrailer,
            Convert.FromHexString("040404FFFFFFFFFF0000000002FC469E6FF086F21D678AD691890AE3"));
    }

    [Fact]
    public void A_windows_mic_token_made_with_header_signing_does_not_verify_without_the_header_and_trailer()
    {
        var session = new Rfc4121AesSession(KerberosEncryptionType.Aes256CtsHmacSha1, WindowsMicKey, isInitiator: false,
            usesAcceptorSubkey: true, initialReceiveSequence: 0x02FC469E);

        Assert.Throws<SecurityProviderException>(() => session.Unprotect(unseal: false, [], (byte[])WindowsStub.Clone(), [],
            Convert.FromHexString("040404FFFFFFFFFF0000000002FC469E6FF086F21D678AD691890AE3")));
    }

    [Fact]
    public void Our_mic_token_has_the_windows_layout()
    {
        var session = new Rfc4121AesSession(KerberosEncryptionType.Aes256CtsHmacSha1, WindowsMicKey, isInitiator: true, usesAcceptorSubkey: true);
        var token = new byte[Rfc4121AesSession.UnsealedSignatureSize];

        session.Protect(seal: false, WindowsPduHeader, (byte[])WindowsStub.Clone(), WindowsSecTrailer, token);

        Assert.Equal("040404FFFFFFFFFF0000000000000000", Convert.ToHexString(token.AsSpan(0, 16)));
    }

    [Fact]
    public void Sealed_tokens_use_ec_16_and_rrc_28_as_windows_does()
    {
        var session = new Rfc4121AesSession(KerberosEncryptionType.Aes256CtsHmacSha1, WindowsKey, isInitiator: true, usesAcceptorSubkey: true);
        var token = new byte[Rfc4121AesSession.SignatureSize];

        session.Protect(seal: true, WindowsPduHeader, new byte[32], WindowsSecTrailer, token);

        Assert.Equal("050406FF0010001C0000000000000000", Convert.ToHexString(token.AsSpan(0, 16)));
        Assert.Equal(76, token.Length);
    }

    [Fact]
    public void A_tampered_cleartext_header_is_rejected_when_sealed()
    {
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        var body = "a secret request"u8.ToArray();
        var signature = new byte[Rfc4121AesSession.SignatureSize];
        client.Protect(seal: true, [], body, [], signature);
        signature[3] ^= 0xFF; // Filler octet, part of the 16-byte cleartext header

        Assert.Throws<SecurityProviderException>(() => server.Unprotect(unseal: true, [], body, [], signature));
    }

    [Fact]
    public void A_declared_signature_longer_than_the_real_token_is_accepted_when_the_excess_is_zero()
    {
        // A Kerberos acceptor can declare a fixed, per-context auth_length
        // (e.g. 76 bytes) in the RPC common header that is longer than the real token (SignatureSize + EC,
        // here 60 + 0), padding the excess with plain zero bytes rather than ciphertext. Unprotect must accept
        // this and only read the real SignatureSize + EC bytes.
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        var original = "a secret request"u8.ToArray();
        var body = original.ToArray();
        var signature = new byte[Rfc4121AesSession.SignatureSize];
        client.Protect(seal: true, [], body, [], signature);

        var oversizedSignature = new byte[Rfc4121AesSession.SignatureSize + 16]; // extra bytes stay zero
        signature.CopyTo(oversizedSignature, 0);

        server.Unprotect(unseal: true, [], body, [], oversizedSignature);
        Assert.Equal(original, body);
    }

    [Fact]
    public void The_receive_side_starts_from_the_initial_receive_sequence()
    {
        // RFC 4120 §5.5.1: the AP-REP's seq-number is "the initial sequence number ... for messages from the
        // server to the client" -- a real Windows acceptor always sends a random, nonzero one, so the
        // initiator's receive side must start from it rather than 0.
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        const ulong initialReceiveSequence = 5;
        var client = new Rfc4121AesSession(type, key, isInitiator: true, initialReceiveSequence: initialReceiveSequence);
        var server = new Rfc4121AesSession(type, key, isInitiator: false);

        // Advance the server's own send sequence (independent counter) to the same starting point so its
        // first outgoing message's wire sequence matches what the client now expects to receive.
        for (var i = 0UL; i < initialReceiveSequence; i++)
            server.Protect(seal: true, [], new byte[1], [], new byte[Rfc4121AesSession.SignatureSize]);

        var body = "hello"u8.ToArray();
        var signature = new byte[Rfc4121AesSession.SignatureSize];
        server.Protect(seal: true, [], body, [], signature);
        client.Unprotect(unseal: true, [], body, [], signature);
        Assert.Equal("hello"u8.ToArray(), body);
    }

    [Fact]
    public void A_message_cannot_be_replayed_back_to_its_own_sender()
    {
        var type = KerberosEncryptionType.Aes128CtsHmacSha1;
        var key = SampleKey(type);
        var client = new Rfc4121AesSession(type, key, isInitiator: true);

        var body = "hello"u8.ToArray();
        var signature = new byte[Rfc4121AesSession.SignatureSize];
        client.Protect(seal: true, [], body, [], signature);

        // The direction flag records that the initiator sent this; the same initiator must refuse to accept
        // it back as if it had been received from the acceptor.
        Assert.Throws<SecurityProviderException>(() => client.Unprotect(unseal: true, [], body, [], signature));
    }
}

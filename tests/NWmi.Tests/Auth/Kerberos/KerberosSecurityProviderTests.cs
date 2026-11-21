using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;
using NWmi.Auth;
using NWmi.Auth.Kerberos;
using NWmi.Rpc;

namespace NWmi.Tests.Auth.Kerberos;

/// <summary>The provider against an in-process KDC and acceptor built from Kerberos.NET's own server side.</summary>
public class KerberosSecurityProviderTests
{
    private static readonly KerberosCredential Alice = new(FakeRealm.UserName, FakeRealm.Password, FakeRealm.RealmName);

    private static Task<KerberosSecurityProvider> NegotiateAsync(FakeRealm realm) =>
        KerberosSecurityProvider.NegotiateAsync(
            Alice, [("fake", 88)], FakeRealm.ServiceName, null, realm.Transport, TestContext.Current.CancellationToken);

    private static DecryptedKrbApReq Accept(FakeRealm realm, byte[] apReq)
    {
        var request = KrbApReq.DecodeApplication(apReq);
        var decrypted = new DecryptedKrbApReq(request);
        decrypted.Decrypt(realm.ServiceKey);
        return decrypted;
    }

    /// <summary>
    /// What a Windows acceptor sends back: an AP-REP encrypted with the ticket's session key (RFC 4120 section 5.5.2),
    /// carrying its own subkey and a sequence number.
    /// </summary>
    private static byte[] AcceptorReply(DecryptedKrbApReq accepted, out KrbEncryptionKey subkey)
    {
        subkey = KrbEncryptionKey.Generate(EncryptionType.AES256_CTS_HMAC_SHA1_96);
        var part = new KrbEncApRepPart
        {
            CTime = accepted.Authenticator.CTime,
            CuSec = accepted.Authenticator.CuSec,
            SubSessionKey = subkey,
            SequenceNumber = 12345,
        };
        var sessionKey = new KerberosKey(accepted.Ticket.Key);
        var rep = new KrbApRep
        {
            EncryptedPart = KrbEncryptedData.Encrypt(part.EncodeApplication(), sessionKey, KeyUsage.EncApRepPart),
        };
        return rep.EncodeApplication().ToArray();
    }

    [Fact]
    public async Task Negotiate_logs_in_with_the_password_and_gets_a_service_ticket()
    {
        var realm = new FakeRealm();

        var provider = await NegotiateAsync(realm);

        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_AP_REQ_is_accepted_by_the_service_and_carries_the_GSS_flags_and_a_sequence_number(bool privacy)
    {
        var protection = privacy ? AuthProtection.Privacy : AuthProtection.Integrity;
        var realm = new FakeRealm();
        var provider = await NegotiateAsync(realm);

        var apReq = await provider.GetInitialApReqAsync(protection, TestContext.Current.CancellationToken);
        var accepted = Accept(realm, apReq);

        Assert.StartsWith(FakeRealm.UserName, accepted.Authenticator.CName.FullyQualifiedName, StringComparison.Ordinal);
        Assert.True(accepted.Options.HasFlag(ApOptions.MutualRequired));
        Assert.NotNull(accepted.Authenticator.SequenceNumber);
        Assert.NotNull(accepted.Authenticator.Checksum); // the GSS checksum a Windows acceptor insists on
        var flags = (GssContextEstablishmentFlag)BitConverter.ToInt32(accepted.Authenticator.Checksum.Checksum.Span[20..24]);
        Assert.True(flags.HasFlag(GssContextEstablishmentFlag.GSS_C_MUTUAL_FLAG));
        Assert.True(flags.HasFlag(GssContextEstablishmentFlag.GSS_C_SEQUENCE_FLAG));
        Assert.True(flags.HasFlag(GssContextEstablishmentFlag.GSS_C_INTEG_FLAG));
        Assert.Equal(protection == AuthProtection.Privacy, flags.HasFlag(GssContextEstablishmentFlag.GSS_C_CONF_FLAG));
    }

    [Fact]
    public async Task GetInitialToken_prefixes_the_AP_REQ_with_the_GSS_token_id()
    {
        var realm = new FakeRealm();
        var provider = await NegotiateAsync(realm);

        var token = await provider.GetInitialTokenAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x01, 0x00 }, token[..2]);
        Assert.Equal(0x6E, token[2]); // [APPLICATION 14], the AP-REQ
    }

    [Fact]
    public async Task Each_connection_gets_a_fresh_authenticator_from_the_same_service_ticket()
    {
        var realm = new FakeRealm();
        var provider = await NegotiateAsync(realm);

        var first = await provider.GetInitialApReqAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);
        var second = await ((KerberosSecurityProvider)provider.CreateForNewConnection()).GetInitialApReqAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        var a = KrbApReq.DecodeApplication(first);
        var b = KrbApReq.DecodeApplication(second);
        Assert.Equal(a.Ticket.EncryptedPart.Cipher.ToArray(), b.Ticket.EncryptedPart.Cipher.ToArray());
    }

    [Fact]
    public async Task An_AP_REP_from_the_acceptor_is_verified_and_replaces_the_session_keys()
    {
        var realm = new FakeRealm();
        var provider = await NegotiateAsync(realm);
        var accepted = Accept(realm, await provider.GetInitialApReqAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken));

        var reply = AcceptorReply(accepted, out var acceptorSubkey);

        Assert.Null(await provider.ProcessServerTokenAsync(reply, TestContext.Current.CancellationToken));
        Assert.True(provider.IsComplete);
        Assert.NotNull(acceptorSubkey);
    }

    [Fact]
    public async Task An_empty_server_token_needs_no_reply()
    {
        var provider = await NegotiateAsync(new FakeRealm());
        await provider.GetInitialApReqAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        Assert.Null(await provider.ProcessServerTokenAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_token_with_the_wrong_id_is_refused()
    {
        var provider = await NegotiateAsync(new FakeRealm());
        await provider.GetInitialApReqAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecurityProviderException>(async () => await provider.ProcessServerTokenAsync(new byte[] { 0x09, 0x09, 0x30, 0x00 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_a_login_failure()
    {
        var realm = new FakeRealm();

        var ex = await Assert.ThrowsAsync<SecurityProviderException>(() => KerberosSecurityProvider.NegotiateAsync(
            new KerberosCredential(FakeRealm.UserName, "wrong", FakeRealm.RealmName), [("fake", 88)], FakeRealm.ServiceName,
            null, realm.Transport, TestContext.Current.CancellationToken));

        Assert.Contains("Kerberos login failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_service_is_reported_as_a_login_failure()
    {
        var realm = new FakeRealm();

        await Assert.ThrowsAsync<SecurityProviderException>(() => KerberosSecurityProvider.NegotiateAsync(
            Alice, [("fake", 88)], "host/nobody.example.com", null, realm.Transport, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Protect_is_unavailable_before_GetInitialToken_establishes_a_session()
    {
        var provider = await NegotiateAsync(new FakeRealm());

        Assert.Throws<InvalidOperationException>(() => provider.Protect(true, [], new byte[16], [], new byte[76]));
    }

    [Fact]
    public async Task The_signature_size_follows_the_protection_level()
    {
        var provider = await NegotiateAsync(new FakeRealm());

        await provider.GetInitialApReqAsync(AuthProtection.Privacy, TestContext.Current.CancellationToken);
        Assert.Equal(Rfc4121AesSession.SignatureSize, provider.SignatureSize);
        await provider.GetInitialApReqAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken);
        Assert.Equal(Rfc4121AesSession.UnsealedSignatureSize, provider.SignatureSize);
        Assert.Equal(RpcAuthType.GssKerberos, provider.AuthType);
    }
}

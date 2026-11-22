using System.Text;
using NWmi.Auth;
using NWmi.Auth.Ntlm;
using NWmi.Options;

namespace NWmi.Tests.Auth;

public class NtlmProviderTests
{
    private const string Password = "Passw0rd!";

    private static async Task<(NtlmSecurityProvider Client, NtlmTestServer Server)> Handshake(
        AuthProtection protection,
        bool timestamp = true,
        Credential? credential = null,
        string serverPassword = Password,
        NtlmNegotiateFlags withhold = NtlmNegotiateFlags.None,
        NtlmSecurityOptions? options = null)
    {
        var client = new NtlmSecurityProvider(credential ?? new Credential("alice", Password, "CORP"), options);
        var server = new NtlmTestServer(serverPassword) { SendTimestamp = timestamp, Withhold = withhold };

        var challenge = server.ProcessNegotiate(await client.GetInitialTokenAsync(protection, TestContext.Current.CancellationToken));
        var authenticate = await client.ProcessServerTokenAsync(challenge, TestContext.Current.CancellationToken);
        Assert.NotNull(authenticate);
        Assert.True(client.IsComplete);
        server.ProcessAuthenticate(authenticate);
        return (client, server);
    }

    private static void ExchangeBothWays(NtlmSecurityProvider client, NtlmTestServer server, bool seal)
    {
        for (var i = 0; i < 3; i++)
        {
            var request = Encoding.ASCII.GetBytes($"call {i} with some payload");
            var onWire = (byte[])request.Clone();
            var signature = new byte[client.SignatureSize];
            client.Protect(seal, [], onWire, [], signature);
            Assert.Equal(!seal, onWire.SequenceEqual(request));
            server.Session!.Unprotect(seal, [], onWire, [], signature);
            Assert.Equal(request, onWire);

            var reply = Encoding.ASCII.GetBytes($"reply {i}");
            var replyOnWire = (byte[])reply.Clone();
            var replySignature = new byte[NtlmSession.SignatureSize];
            server.Session.Protect(seal, [], replyOnWire, [], replySignature);
            client.Unprotect(seal, [], replyOnWire, [], replySignature);
            Assert.Equal(reply, replyOnWire);
        }
    }

    [Theory]
    [InlineData(nameof(AuthProtection.Integrity), false)]
    [InlineData(nameof(AuthProtection.Privacy), true)]
    public async Task Handshake_succeeds_and_messages_are_protected_in_both_directions(string protectionName, bool seal)
    {
        var (client, server) = await Handshake(Enum.Parse<AuthProtection>(protectionName));
        Assert.Equal("alice", server.AuthenticatedUser);
        Assert.Equal("CORP", server.AuthenticatedDomain);
        ExchangeBothWays(client, server, seal);
    }

    [Fact]
    public async Task A_server_timestamp_makes_the_client_send_a_valid_mic()
    {
        var (_, withTimestamp) = await Handshake(AuthProtection.Privacy, timestamp: true);
        Assert.True(withTimestamp.MicWasPresent);

        var (client, withoutTimestamp) = await Handshake(AuthProtection.Privacy, timestamp: false);
        Assert.False(withoutTimestamp.MicWasPresent);
        ExchangeBothWays(client, withoutTimestamp, seal: true);
    }

    [Fact]
    public async Task Connect_level_authenticates_without_message_protection()
    {
        var (client, server) = await Handshake(AuthProtection.Connect);
        Assert.Equal("alice", server.AuthenticatedUser);
        Assert.Null(server.Session);
        Assert.Throws<InvalidOperationException>(() => client.Protect(false, [], new byte[4], [], new byte[16]));
    }

    [Fact]
    public async Task Wrong_password_is_rejected_by_the_server()
    {
        var client = new NtlmSecurityProvider(new Credential("alice", "wrong", "CORP"));
        var server = new NtlmTestServer(Password);
        var challenge = server.ProcessNegotiate(await client.GetInitialTokenAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken));
        var authenticate = (await client.ProcessServerTokenAsync(challenge, TestContext.Current.CancellationToken))!;
        Assert.Throws<SecurityProviderException>(() => server.ProcessAuthenticate(authenticate));
    }

    [Fact]
    public async Task A_tampered_authenticate_message_fails_the_mic_check()
    {
        var client = new NtlmSecurityProvider(new Credential("alice", Password, "CORP"));
        var server = new NtlmTestServer(Password);
        var challenge = server.ProcessNegotiate(await client.GetInitialTokenAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken));
        var authenticate = (await client.ProcessServerTokenAsync(challenge, TestContext.Current.CancellationToken))!;

        authenticate[^1] ^= 1; // flip a bit in the encrypted session key
        Assert.Throws<SecurityProviderException>(() => server.ProcessAuthenticate(authenticate));
    }

    [Fact]
    public async Task Client_fails_if_the_server_refuses_signing_or_sealing()
    {
        await Assert.ThrowsAsync<SecurityProviderException>(() =>
            Handshake(AuthProtection.Integrity, withhold: NtlmNegotiateFlags.Sign));
        await Assert.ThrowsAsync<SecurityProviderException>(() =>
            Handshake(AuthProtection.Privacy, withhold: NtlmNegotiateFlags.Seal));
        await Assert.ThrowsAsync<SecurityProviderException>(() =>
            Handshake(AuthProtection.Integrity, withhold: NtlmNegotiateFlags.ExtendedSessionSecurity));
    }

    [Fact]
    public async Task Without_key_exchange_the_session_key_is_used_directly()
    {
        var (client, server) = await Handshake(AuthProtection.Privacy, withhold: NtlmNegotiateFlags.KeyExchange);
        ExchangeBothWays(client, server, seal: true);
    }

    [Fact]
    public async Task Tokens_out_of_order_are_usage_errors()
    {
        var client = new NtlmSecurityProvider(new Credential("alice", Password));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ProcessServerTokenAsync(new byte[60], TestContext.Current.CancellationToken));

        var server = new NtlmTestServer(Password);
        var challenge = server.ProcessNegotiate(await client.GetInitialTokenAsync(AuthProtection.Connect, TestContext.Current.CancellationToken));
        await client.ProcessServerTokenAsync(challenge, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ProcessServerTokenAsync(challenge, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Garbage_from_the_server_is_an_authentication_error()
    {
        var client = new NtlmSecurityProvider(new Credential("alice", Password));
        await client.GetInitialTokenAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SecurityProviderException>(async () => await client.ProcessServerTokenAsync(new byte[100], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_target_name_and_workstation_are_taken_from_the_options()
    {
        var options = new NtlmSecurityOptions { Workstation = "LABPC", TargetName = "RPCSS/server" };
        var client = new NtlmSecurityProvider(new Credential("alice", Password, "CORP"), options);
        var server = new NtlmTestServer(Password);
        var authenticate = (await client.ProcessServerTokenAsync(server.ProcessNegotiate(await client.GetInitialTokenAsync(AuthProtection.Integrity, TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken))!;

        var text = Encoding.Unicode.GetString(authenticate);
        Assert.Contains("LABPC", text, StringComparison.Ordinal);
        Assert.Contains("RPCSS/server", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Credential_parsing_splits_domain_and_user()
    {
        Assert.Equal(("CORP", "alice"), (Credential.Parse(@"CORP\alice", "x").Domain, Credential.Parse(@"CORP\alice", "x").UserName));
        Assert.Equal(("corp.example", "alice"), (Credential.Parse("alice@corp.example", "x").Domain, Credential.Parse("alice@corp.example", "x").UserName));
        Assert.Equal(("", "alice"), (Credential.Parse("alice", "x").Domain, Credential.Parse("alice", "x").UserName));
    }
}

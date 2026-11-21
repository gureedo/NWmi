using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Auth;
using NWmi.Rpc;
using NWmi.WinRm;

namespace NWmi.Tests.WinRm;

public class WsmanTransportSealingTests
{
    /// <summary>Flips every bit of the body, so a message that skipped sealing or unsealing shows up in the result.</summary>
    private sealed class InvertingProvider : IRpcSecurityProvider
    {
        public RpcAuthType AuthType => RpcAuthType.WinNt;

        public string? MechanismOid => null;

        public int SignatureSize => 16;

        public bool IsComplete => true;

        public ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default) => new([]);

        public ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token, CancellationToken cancellationToken = default) => new((byte[]?)null);

        public void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, Span<byte> signature) =>
            Invert(body);

        public void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter, ReadOnlySpan<byte> signature) =>
            Invert(body);

        public IRpcSecurityProvider CreateForNewConnection() => new InvertingProvider();

        private static void Invert(Span<byte> body)
        {
            for (var i = 0; i < body.Length; i++)
                body[i] = (byte)~body[i];
        }
    }

    private static int CountOf(byte[] haystack, string needle)
    {
        var pattern = Encoding.ASCII.GetBytes(needle);
        var count = 0;
        for (var position = 0; ;)
        {
            var found = haystack.AsSpan(position).IndexOf(pattern);
            if (found < 0)
                return count;

            count++;
            position += found + pattern.Length;
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(16384)]
    [InlineData(40000)]
    public void A_message_of_any_size_is_sealed_as_one_part_and_comes_back_intact(int size)
    {
        var message = Enumerable.Range(0, size).Select(i => (byte)('a' + i % 26)).ToArray();
        var original = message.ToArray();
        var provider = new InvertingProvider();

        var body = WsmanTransport.Seal(provider, message);

        Assert.Equal(1, CountOf(body, "Content-Type: application/octet-stream"));
        Assert.Equal(1, CountOf(body, $"Length={size}\r\n"));
        Assert.Equal(original, message); // the caller's buffer is not sealed in place
        Assert.Equal(original, WsmanTransport.Unseal(provider, body));
    }

    [Theory]
    [InlineData("multipart/encrypted", true)]
    [InlineData("multipart/x-multi-encrypted", true)]
    [InlineData("MULTIPART/ENCRYPTED", true)]
    [InlineData("application/soap+xml", false)]
    [InlineData(null, false)]
    public void Both_sealed_media_types_are_recognised(string? mediaType, bool expected) =>
        Assert.Equal(expected, WsmanTransport.IsEncryptedMediaType(mediaType));

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.NameResolutionError, true)]
    [InlineData(HttpRequestError.ResponseEnded, false)]
    [InlineData(HttpRequestError.Unknown, false)]
    public void Only_a_failure_to_connect_proves_the_request_was_not_sent(HttpRequestError error, bool expected) =>
        Assert.Equal(expected, WsmanTransport.NeverReachedServer(new HttpRequestException(error, "failed")));

    [Theory]
    [InlineData(Wsman.ActionGet, true)]
    [InlineData(Wsman.ActionEnumerate, true)]
    [InlineData(Wsman.ActionPut, false)]
    [InlineData(Wsman.ActionCreate, false)]
    [InlineData(Wsman.ActionDelete, false)]
    [InlineData(Wsman.ActionPull, false)]
    [InlineData("http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/Win32_Process/Create", false)]
    public void Only_reads_may_be_sent_twice(string action, bool expected) =>
        Assert.Equal(expected, WsmanClient.IsIdempotent(action));

    [Fact]
    public void An_encrypted_reply_without_an_encrypted_part_is_rejected() =>
        Assert.Throws<WsmanException>(() =>
            WsmanTransport.Unseal(new InvertingProvider(), "--Encrypted Boundary--\r\n"u8.ToArray()));

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task An_unencrypted_reply_on_a_sealed_session_is_rejected(int status)
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/wsman/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            // The login: accepted at once, since the fake provider needs no round trip.
            var login = await listener.GetContextAsync();
            login.Response.StatusCode = 200;
            login.Response.Close();

            // The request: answered in the clear, as someone on the path could.
            var request = await listener.GetContextAsync();
            request.Response.StatusCode = status;
            request.Response.ContentType = "application/soap+xml;charset=UTF-8";
            var body = Encoding.UTF8.GetBytes("<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body/></s:Envelope>");
            await request.Response.OutputStream.WriteAsync(body);
            request.Response.Close();
        }, ct);

        using var transport = new WsmanTransport(new Uri($"http://localhost:{port}/wsman"),
            (_, _) => Task.FromResult<IRpcSecurityProvider>(new InvertingProvider()), TimeSpan.FromSeconds(10),
            NullLogger.Instance);
        await transport.ConnectAsync(ct);

        var ex = await Assert.ThrowsAsync<WsmanException>(() => transport.SendAsync("<request/>", idempotent: false, ct));
        Assert.Contains("encrypted", ex.Message, StringComparison.Ordinal);
        await serving;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using NWmi.Auth;

#pragma warning disable CA5351

namespace NWmi.WinRm;

/// <summary>
/// HTTP transport for WS-Management (MS-WSMV 2.2.9): one keep-alive connection that is authenticated with NTLM
/// (the <c>Negotiate</c> scheme carrying bare NTLM messages), after which every SOAP message travels sealed in a
/// <c>multipart/encrypted</c> body with protocol <c>application/HTTP-SPNEGO-session-encrypted</c>. A Windows
/// listener refuses unencrypted traffic over plain HTTP by default, and HTTP.sys ties the authenticated identity to
/// the TCP connection, so this holds exactly one connection and logs in again if it drops.
/// </summary>
internal sealed class WsmanTransport : IDisposable
{
    private const string EncryptionProtocol = "application/HTTP-SPNEGO-session-encrypted";
    private const string Boundary = "Encrypted Boundary";

    private readonly Func<Func<byte[]?>, CancellationToken, Task<IRpcSecurityProvider>> _providerFactory;
    private byte[]? _channelBinding;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRpcSecurityProvider? _session;

    public WsmanTransport(Uri endpoint,
        Func<Func<byte[]?>, CancellationToken, Task<IRpcSecurityProvider>> providerFactory, TimeSpan timeout,
        ILogger logger, bool ignoreCertificateErrors = false)
    {
        Endpoint = endpoint;
        _providerFactory = providerFactory;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                // The listener's certificate also feeds NTLM's channel binding (RFC 5929 tls-server-end-point).
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    _channelBinding = certificate is null ? null : ComputeChannelBinding(certificate);
                    return ignoreCertificateErrors || errors == System.Net.Security.SslPolicyErrors.None;
                },
            },
        })
        {
            Timeout = timeout,
        };
    }

    public Uri Endpoint { get; }

    private bool SealMessages => Endpoint.Scheme != Uri.UriSchemeHttps;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
                await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SendAsync(string envelope, bool idempotent, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0;; attempt++)
            {
                if (_session is null)
                    await AuthenticateAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    return await SendEncryptedAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    // The connection (and with it the authenticated session) was dropped: log in again, once.
                    _session = null;
                    if (attempt > 0 || !(idempotent || NeverReachedServer(ex)))
                        throw;
                }
                catch (WsmanAuthenticationRequiredException) when (attempt == 0)
                {
                    _session = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool NeverReachedServer(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError;

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var provider = await _providerFactory(() => _channelBinding, cancellationToken).ConfigureAwait(false);
        var token = await provider.GetInitialTokenAsync(AuthProtection.Privacy, cancellationToken)
            .ConfigureAwait(false);
        for (var leg = 0; leg < 6; leg++)
        {
            using var request = EmptyPost(token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var challenge = ReadNegotiateChallenge(response);
            var accepted = response.IsSuccessStatusCode;
            if (!accepted && response.StatusCode != HttpStatusCode.Unauthorized)
                throw new WsmanException(
                    $"The WinRM listener answered the authentication request with {(int)response.StatusCode}.");

            if (challenge is null)
            {
                if (!accepted || !provider.IsComplete)
                    throw new WsmanException(accepted
                        ? "The WinRM listener ended authentication before the security context was complete."
                        : leg == 0
                            ? "The WinRM listener does not offer Negotiate authentication (enable it on the service, or use HTTPS)."
                            : "The WinRM listener rejected the credentials (HTTP 401).");

                break;
            }

            var next = await provider.ProcessServerTokenAsync(challenge, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                if (!provider.IsComplete)
                    throw new WsmanException(
                        "The WinRM listener's authentication token did not complete the security context.");

                break;
            }

            token = next;
        }

        if (!provider.IsComplete)
            throw new WsmanException("WinRM authentication did not finish.");

        _logger.LogDebug("WinRM: authenticated to {Endpoint}.", Endpoint);
        _session = provider;
    }

    /// <summary>
    /// The MsvAvChannelBindings value for the server's certificate: the MD5 of a GSS channel-bindings structure whose
    /// application data is <c>tls-server-end-point:</c> and the certificate's hash (SHA-256, or the hash its signature uses
    /// when that is stronger).
    /// </summary>
    internal static byte[] ComputeChannelBinding(X509Certificate certificate)
    {
        using var cert = new X509Certificate2(certificate);
        var signature = cert.SignatureAlgorithm.FriendlyName ?? string.Empty;
        var hash = signature.Contains("384", StringComparison.Ordinal)
            ? SHA384.HashData(cert.RawData)
            : signature.Contains("512", StringComparison.Ordinal)
                ? SHA512.HashData(cert.RawData)
                : SHA256.HashData(cert.RawData);
        var data = "tls-server-end-point:"u8.ToArray().Concat(hash).ToArray();

        // initiator type and length, acceptor type and length (all zero), then the application data's length and bytes.
        var structure = new byte[16 + 4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(structure.AsSpan(16), data.Length);
        data.CopyTo(structure, 20);
        return MD5.HashData(structure);
    }

    private HttpRequestMessage EmptyPost(byte[] token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent([]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Negotiate", Convert.ToBase64String(token));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "UTF-8" };
        return request;
    }

    private static byte[]? ReadNegotiateChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
            if (header.Scheme.Equals("Negotiate", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(header.Parameter))
                return Convert.FromBase64String(header.Parameter);

        return null;
    }

    private async Task<string> SendEncryptedAsync(string envelope, CancellationToken cancellationToken)
    {
        var session = _session!;
        var plain = Encoding.UTF8.GetBytes(envelope);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Content = new ByteArrayContent(SealMessages ? Seal(session, plain) : plain);
        request.Content.Headers.ContentType = SealMessages
            ? MediaTypeHeaderValue.Parse(
                $"multipart/encrypted;protocol=\"{EncryptionProtocol}\";boundary=\"{Boundary}\"")
            : new MediaTypeHeaderValue("application/soap+xml") { CharSet = "UTF-8" };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new WsmanAuthenticationRequiredException();

        var text = IsEncryptedMediaType(response.Content.Headers.ContentType?.MediaType)
            ? Encoding.UTF8.GetString(Unseal(session, payload))
            : Encoding.UTF8.GetString(payload);

        if (!response.IsSuccessStatusCode)
            // A SOAP fault travels with status 500 (or 400); the caller turns the fault into an exception.
            if (!text.Contains("Envelope", StringComparison.Ordinal))
                throw new WsmanException($"WinRM returned HTTP {(int)response.StatusCode}: {text}");

        return text;
    }

    /// <summary>Whether a reply body is sealed. Only CredSSP splits a message (<c>multipart/x-multi-encrypted</c>), but reading that form costs nothing.</summary>
    internal static bool IsEncryptedMediaType(string? mediaType) =>
        string.Equals(mediaType, "multipart/encrypted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "multipart/x-multi-encrypted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps the message as the multipart/encrypted body of MS-WSMV 2.2.9.1.1: one sealed part whatever its size, since
    /// splitting into 16 KB parts belongs to CredSSP and its <c>multipart/x-multi-encrypted</c>, which this does not use.
    /// </summary>
    internal static byte[] Seal(IRpcSecurityProvider session, byte[] message)
    {
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        Span<byte> digits = stackalloc byte[11];

        var sealedMessage = message.ToArray();
        var signature = new byte[session.SignatureSize];
        session.Protect(true, default, sealedMessage, default, signature);

        output.Write("--Encrypted Boundary\r\n"u8);
        output.Write("Content-Type: application/HTTP-SPNEGO-session-encrypted\r\n"u8);
        output.Write("OriginalContent: type=application/soap+xml;charset=UTF-8;Length="u8);
        message.Length.TryFormat(digits, out var digitCount, default, CultureInfo.InvariantCulture);
        output.Write(digits[..digitCount]);
        output.Write("\r\n--Encrypted Boundary\r\n"u8);
        output.Write("Content-Type: application/octet-stream\r\n"u8);
        BinaryPrimitives.WriteInt32LittleEndian(length, signature.Length);
        output.Write(length);
        output.Write(signature);
        output.Write(sealedMessage);
        output.Write("--Encrypted Boundary--\r\n"u8);
        return output.ToArray();
    }

    internal static byte[] Unseal(IRpcSecurityProvider session, byte[] payload)
    {
        var marker = "Content-Type: application/octet-stream\r\n"u8;
        var boundary = "--Encrypted Boundary"u8;
        using var output = new MemoryStream();
        var span = payload.AsSpan();
        var position = 0;
        while (true)
        {
            var found = span[position..].IndexOf(marker);
            if (found < 0)
                break;

            var start = position + found + marker.Length;
            var end = span[start..].IndexOf(boundary);
            var partEnd = end < 0 ? span.Length : start + end;
            var part = span[start..partEnd];
            if (part.Length < 4)
                throw new WsmanException("An encrypted WinRM part is shorter than its length prefix.");

            var signatureLength = BinaryPrimitives.ReadInt32LittleEndian(part);
            if (signatureLength < 0 || signatureLength > part.Length - 4)
                throw new WsmanException("An encrypted WinRM part has an invalid signature length.");

            var signature = part.Slice(4, signatureLength);
            var data = part[(4 + signatureLength)..].ToArray();
            session.Unprotect(true, default, data, default, signature);
            output.Write(data);
            position = partEnd;
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private sealed class WsmanAuthenticationRequiredException : Exception;
}

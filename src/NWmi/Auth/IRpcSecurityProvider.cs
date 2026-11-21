using NWmi.Rpc;

namespace NWmi.Auth;

internal enum AuthProtection
{
    /// <summary>Authenticate when the connection is set up; later messages are not protected.</summary>
    Connect,

    /// <summary>Every message carries a signature.</summary>
    Integrity,

    /// <summary>Every message is signed and its body is encrypted.</summary>
    Privacy,
}

internal interface IRpcSecurityProvider
{
    RpcAuthType AuthType { get; }

    string? MechanismOid { get; }

    int SignatureSize { get; }

    bool IsComplete { get; }

    /// <summary>Starts the handshake and returns the first token for the server.</summary>
    ValueTask<byte[]> GetInitialTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a token from the server. Returns the next token to send, or null when there is nothing more to send.
    /// A token returned while <see cref="IsComplete"/> is false is sent in an alter_context PDU and the server's answer
    /// comes back here; one returned once the provider is complete is the last, sent in an auth3 PDU.
    /// </summary>
    ValueTask<byte[]?> ProcessServerTokenAsync(ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs a message and, if <paramref name="encryptBody"/> is set, encrypts <paramref name="body"/> in place.
    /// <paramref name="signedBefore"/> and <paramref name="signedAfter"/> are covered by the signature but are not
    /// encrypted; they are empty unless the RPC header is being signed.
    /// </summary>
    void Protect(bool encryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter,
        Span<byte> signature);

    /// <summary>Checks the signature of a received message and decrypts <paramref name="body"/> in place if asked.</summary>
    void Unprotect(bool decryptBody, ReadOnlySpan<byte> signedBefore, Span<byte> body, ReadOnlySpan<byte> signedAfter,
        ReadOnlySpan<byte> signature);

    /// <summary>
    /// A provider with the same credentials and options but no handshake state, for another connection: a security
    /// context belongs to one connection, and DCOM opens several (endpoint mapper, then each object exporter).
    /// </summary>
    IRpcSecurityProvider CreateForNewConnection();
}

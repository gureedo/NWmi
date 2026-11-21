namespace NWmi.Auth;

internal interface ISpnegoMechanism : IRpcSecurityProvider
{
    /// <summary>The first mechanism token, with no GSS wrapper beyond what SPNEGO itself expects.</summary>
    ValueTask<byte[]> GetInitialMechTokenAsync(AuthProtection protection, CancellationToken cancellationToken = default);

    /// <summary>Handles the server's mechanism token and returns the next one, or null if there is none to send.</summary>
    byte[]? ProcessMechToken(ReadOnlySpan<byte> token);

    /// <summary>Whether the context can sign the mechanism list (the SPNEGO mechListMIC).</summary>
    bool SupportsMechListMic { get; }

    byte[] CreateMechListMic(ReadOnlySpan<byte> mechTypes);

    void VerifyMechListMic(ReadOnlySpan<byte> mechTypes, ReadOnlySpan<byte> mic);

    /// <summary>The same mechanism with the same credentials and no handshake state.</summary>
    new ISpnegoMechanism CreateForNewConnection();
}

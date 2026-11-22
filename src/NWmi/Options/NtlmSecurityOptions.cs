namespace NWmi.Options;

/// <summary>Optional NTLM settings.</summary>
public sealed class NtlmSecurityOptions
{
    /// <summary>Workstation name reported to the server. Defaults to this machine's name.</summary>
    public string? Workstation { get; set; }

    /// <summary>Service principal name of the target (for example "RPCSS/server01"). Sent as MsvAvTargetName; empty if unset.</summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// The channel-binding hash (MsvAvChannelBindings) to put in the AUTHENTICATE message, for NTLM over TLS: asked for when
    /// the message is built, so the TLS handshake can come first. Null, or a null result, sends the "none" value.
    /// </summary>
    internal Func<byte[]?>? ChannelBindings { get; set; }

    /// <summary>Source of random bytes (client challenge, exported session key). Null uses the system CSPRNG. For tests.</summary>
    internal Func<int, byte[]>? Random { get; set; }

    /// <summary>Source of the client FILETIME, used when the server sends no timestamp. Null uses the current time. For tests.</summary>
    internal Func<ulong>? Timestamp { get; set; }

    internal NtlmSecurityOptions Copy() => (NtlmSecurityOptions)MemberwiseClone();
}

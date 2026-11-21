namespace NWmi.Auth.Ntlm;

internal static class NtlmConstants
{
    public static readonly byte[] MessageSignature = [.. "NTLMSSP\0"u8];
}

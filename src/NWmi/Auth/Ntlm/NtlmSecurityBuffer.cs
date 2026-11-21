using System.Runtime.InteropServices;

namespace NWmi.Auth.Ntlm;

/// <summary>A security buffer descriptor (MS-NLMP 2.2.2.1)</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct NtlmSecurityBuffer(ushort length, int bufferOffset)
{
    public readonly ushort Length = length;
    public readonly ushort MaximumLength = length;
    public readonly uint BufferOffset = checked((uint)bufferOffset);

    public ReadOnlySpan<byte> Read(ReadOnlySpan<byte> message)
    {
        if (Length == 0)
            return [];

        if (BufferOffset > message.Length || Length > message.Length - BufferOffset)
            throw new SecurityProviderException("An NTLM message field points outside the message.");

        return message.Slice((int)BufferOffset, Length);
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NWmi.Auth.Ntlm;

/// <summary>VERSION (MS-NLMP 2.2.2.10)</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct NtlmVersion(byte major, byte minor, ushort build)
{
    private const byte NtlmSspRevisionW2K3 = 0x0F;

    /// <summary>
    /// Windows 10 version 2004 (10.0.19041), a common real-world client build. The server ignores the value.
    /// </summary>
    public static NtlmVersion Default => new(10, 0, 19041);

    public readonly byte Major = major;
    public readonly byte Minor = minor;
    public readonly ushort Build = build;
    private readonly InlineArray3<byte> _reserved;
    public readonly byte Revision = NtlmSspRevisionW2K3;
}

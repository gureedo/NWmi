namespace NWmi.Auth.Ntlm;

/// <summary>NegotiateFlags (MS-NLMP 2.2.2.5).</summary>
[Flags]
internal enum NtlmNegotiateFlags : uint
{
    None = 0,
    Unicode = 0x00000001,
    Oem = 0x00000002,
    RequestTarget = 0x00000004,
    Sign = 0x00000010,
    Seal = 0x00000020,
    Datagram = 0x00000040,
    LmKey = 0x00000080,
    Ntlm = 0x00000200,
    Anonymous = 0x00000800,
    OemDomainSupplied = 0x00001000,
    OemWorkstationSupplied = 0x00002000,
    AlwaysSign = 0x00008000,
    TargetTypeDomain = 0x00010000,
    TargetTypeServer = 0x00020000,
    ExtendedSessionSecurity = 0x00080000,
    Identify = 0x00100000,
    RequestNonNtSessionKey = 0x00400000,
    TargetInfo = 0x00800000,
    Version = 0x02000000,
    Bits128 = 0x20000000,
    KeyExchange = 0x40000000,
    Bits56 = 0x80000000,
}

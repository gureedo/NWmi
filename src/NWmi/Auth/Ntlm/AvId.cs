namespace NWmi.Auth.Ntlm;

/// <summary>AV_PAIR IDs (MS-NLMP 2.2.2.1).</summary>
internal static class AvId
{
    public const ushort EndOfList = 0;
    public const ushort NbComputerName = 1;
    public const ushort NbDomainName = 2;
    public const ushort DnsComputerName = 3;
    public const ushort DnsDomainName = 4;
    public const ushort DnsTreeName = 5;
    public const ushort Flags = 6;
    public const ushort Timestamp = 7;
    public const ushort SingleHost = 8;
    public const ushort TargetName = 9;
    public const ushort ChannelBindings = 10;

    /// <summary>Bit in <see cref="Flags"/>: the AUTHENTICATE_MESSAGE carries a MIC.</summary>
    public const uint FlagMicProvided = 0x2;
}

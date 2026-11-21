namespace NWmi.Auth.Ntlm;

/// <summary>MessageType (MS-NLMP 2.2.1)</summary>
internal enum NtlmMessageType : uint
{
    Negotiate = 1,
    Challenge = 2,
    Authenticate = 3,
}

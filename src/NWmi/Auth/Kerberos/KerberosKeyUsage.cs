// ReSharper disable once UnusedMember.Global

namespace NWmi.Auth.Kerberos;

/// <summary>
/// Key usage numbers (RFC 4120 section 7.5.1) and the GSS-API per-message token usages of the Kerberos V5 mechanism's
/// "new" token format (RFC 4121 section 2). The number is mixed into the derived keys (RFC 3961 section 5.3).
/// </summary>
internal enum KerberosKeyUsage
{
    /// <summary>AS-REQ PA-ENC-TIMESTAMP padata, encrypted with the client key.</summary>
    AsReqPaEncTimestamp = 1,

    /// <summary>AS-REP and TGS-REP ticket, encrypted with the service key.</summary>
    Ticket = 2,

    /// <summary>AS-REP encrypted part, encrypted with the client key.</summary>
    AsRepEncryptedPart = 3,

    /// <summary>TGS-REQ KDC-REQ-BODY authorization data, encrypted with the TGS session key.</summary>
    TgsReqAuthDataSessionKey = 4,

    /// <summary>TGS-REQ KDC-REQ-BODY authorization data, encrypted with the TGS authenticator subkey.</summary>
    TgsReqAuthDataSubkey = 5,

    /// <summary>TGS-REQ PA-TGS-REQ AP-REQ authenticator checksum, keyed with the TGS session key.</summary>
    TgsReqPaTgsReqChecksum = 6,

    /// <summary>TGS-REQ PA-TGS-REQ AP-REQ authenticator, encrypted with the TGS session key.</summary>
    TgsReqPaTgsReqAuthenticator = 7,

    /// <summary>TGS-REP encrypted part, encrypted with the TGS session key.</summary>
    TgsRepEncryptedPartSessionKey = 8,

    /// <summary>TGS-REP encrypted part, encrypted with the TGS authenticator subkey.</summary>
    TgsRepEncryptedPartSubkey = 9,

    /// <summary>AP-REQ authenticator checksum, keyed with the application session key.</summary>
    ApReqAuthenticatorChecksum = 10,

    /// <summary>AP-REQ authenticator, encrypted with the application session key.</summary>
    ApReqAuthenticator = 11,

    /// <summary>AP-REP encrypted part, encrypted with the application session key.</summary>
    ApRepEncryptedPart = 12,

    /// <summary>KRB-PRIV encrypted part, encrypted with a key chosen by the application.</summary>
    KrbPrivEncryptedPart = 13,

    /// <summary>KRB-CRED encrypted part, encrypted with a key chosen by the application.</summary>
    KrbCredEncryptedPart = 14,

    /// <summary>KRB-SAFE checksum, keyed with a key chosen by the application.</summary>
    KrbSafeChecksum = 15,

    /// <summary>AD-KDC-ISSUED checksum.</summary>
    AdKdcIssuedChecksum = 19,

    /// <summary>GSS-API token sealed by the acceptor (RFC 4121).</summary>
    AcceptorSeal = 22,

    /// <summary>GSS-API token signed by the acceptor (RFC 4121).</summary>
    AcceptorSign = 23,

    /// <summary>GSS-API token sealed by the initiator (RFC 4121).</summary>
    InitiatorSeal = 24,

    /// <summary>GSS-API token signed by the initiator (RFC 4121).</summary>
    InitiatorSign = 25,
}

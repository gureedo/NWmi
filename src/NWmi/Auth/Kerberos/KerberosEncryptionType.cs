// ReSharper disable UnusedMember.Global

namespace NWmi.Auth.Kerberos;

/// <summary>Kerberos encryption type numbers (RFC 3961 section 8 and the IANA Kerberos Encryption Type Numbers registry).</summary>
internal enum KerberosEncryptionType
{
    DesCbcCrc = 1,
    DesCbcMd4 = 2,
    DesCbcMd5 = 3,
    Des3CbcMd5 = 5,
    Des3CbcSha1 = 7,
    DsaWithSha1CmsOid = 9,
    Md5WithRsaEncryptionCmsOid = 10,
    Sha1WithRsaEncryptionCmsOid = 11,
    Rc2CbcEnvOid = 12,
    RsaEncryptionEnvOid = 13,
    RsaEsOaepEnvOid = 14,
    DesEde3CbcEnvOid = 15,
    Des3CbcSha1Kd = 16,
    Aes128CtsHmacSha1 = 17,
    Aes256CtsHmacSha1 = 18,
    Aes128CtsHmacSha256 = 19,
    Aes256CtsHmacSha384 = 20,
    Rc4Hmac = 23,
    Rc4HmacExp = 24,
    Camellia128CtsCmac = 25,
    Camellia256CtsCmac = 26,
}

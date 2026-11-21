using System.Buffers.Binary;
using NWmi.Auth.Kerberos;

namespace NWmi.Tests.Auth.Kerberos;

/// <summary>
/// SCRATCH: verifies this client's Wrap-token decryption logic against a real token captured from Windows'
/// own SSPI (EncryptMessage, Kerberos package, no mutual auth) on the lab DC, with the real session key
/// extracted via QueryContextAttributes(SECPKG_ATTR_SESSION_KEY). Not a permanent test; delete once the
/// live RPC_S_SEC_PKG_ERROR investigation concludes.
/// </summary>
public class SspiGroundTruthScratchTests
{
    [Fact]
    public void DecryptsRealSspiTokenNoMutualAuth()
    {
        var key = Convert.FromHexString("170BDC089DC9180284066662F685FA6C9625B1EEB794F7A7347F57D56FD6F9DA");
        var token = Convert.FromHexString("050402FF0000001C0000000061691A9C43A98897BA938F8994120BA3D00EA775D887C3EED99B5C618330B6E9F8CDD2425D9207C72B11E0B76775E3E7");
        var data = Convert.FromHexString("7D735EFD57D91B06062831D04A4477F8957D69B0EA364FEFE74F16085F4057A6ACB859D0");
        var expectedPlain = Convert.FromHexString("4E574D492D535350492D50524F42452D504C41494E544558542D30313233343536373839");

        Assert.Equal(60, token.Length);
        var header = token.AsSpan(0, 16).ToArray();
        var headerCopyCt = token.AsSpan(16, 16).ToArray();
        var checksum = token.AsSpan(32, 12).ToArray();
        var confounderCt = token.AsSpan(44, 16).ToArray();

        // EC=0 here (no filler) - cipherCore order as originally encrypted: confounder | body | header-copy.
        var cipherCore = new byte[16 + data.Length + 16];
        confounderCt.CopyTo(cipherCore, 0);
        data.CopyTo(cipherCore, 16);
        headerCopyCt.CopyTo(cipherCore, 16 + data.Length);

        var plaintext = Aes3961.DecryptCore(KerberosEncryptionType.Aes256CtsHmacSha1, key, KerberosKeyUsage.InitiatorSeal, cipherCore, checksum);

        Assert.Equal(expectedPlain, plaintext.AsSpan(0, data.Length).ToArray());

        var embeddedHeader = plaintext.AsSpan(data.Length).ToArray();
        var expectedEmbeddedWithRrcZeroed = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(expectedEmbeddedWithRrcZeroed.AsSpan(6), 0);
        Assert.Equal(expectedEmbeddedWithRrcZeroed, embeddedHeader);
    }
}

using Org.BouncyCastle.Crypto.Digests;

namespace NWmi.Auth.Ntlm;

internal static class Md4
{
    public const int HashSize = 16;

    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        var digest = new MD4Digest();
        digest.BlockUpdate(data);
        var hash = new byte[HashSize];
        digest.DoFinal(hash);
        return hash;
    }
}

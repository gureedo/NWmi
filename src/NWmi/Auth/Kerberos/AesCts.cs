using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace NWmi.Auth.Kerberos;

/// <summary>
/// AES in CBC mode with ciphertext stealing (RFC 3962 §5), always with an all-zero initial vector (Kerberos never
/// chains state across separate encrypt/decrypt calls — each EncryptedData is self-contained). More than one block
/// goes through BouncyCastle's <see cref="CtsBlockCipher"/>, whose output is the same CBC-CS3 form (the last two
/// blocks swapped, the final one truncated); exactly one block is a single raw AES block, which BouncyCastle's
/// does not produce (RFC 3962 leaves it unswapped, and it happens when a Kerberos message has an empty body).
/// Less than one block is not defined and is rejected.
/// </summary>
internal static class AesCts
{
    private const int BlockSize = 16;

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext) => Transform(key, plaintext, encrypt: true);

    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ciphertext) => Transform(key, ciphertext, encrypt: false);

    private static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, bool encrypt)
    {
        if (input.Length < BlockSize)
            throw new ArgumentException("CBC-CTS needs at least one block (the confounder is one).", nameof(input));

        if (input.Length == BlockSize)
        {
            using var aes = Aes.Create();
            aes.SetKey(key);
            return encrypt
                ? aes.EncryptEcb(input, PaddingMode.None)
                : aes.DecryptEcb(input, PaddingMode.None);
        }

        var cipher = new CtsBlockCipher(new CbcBlockCipher(new AesEngine()));
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), new byte[BlockSize]));
        var result = new byte[input.Length];
        var length = cipher.ProcessBytes(input, result);
        length += cipher.DoFinal(result.AsSpan(length));
        return result.AsSpan(0, length).ToArray();
    }
}

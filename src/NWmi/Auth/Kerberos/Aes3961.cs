using System.Buffers.Binary;
using System.Security.Cryptography;
using Kerberos.NET.Crypto;

namespace NWmi.Auth.Kerberos;

/// <summary>
/// The AES128-CTS-HMAC-SHA1-96 and AES256-CTS-HMAC-SHA1-96 Kerberos encryption types (etypes 17/18, RFC 3962),
/// built on RFC 3961's "simplified profile": key derivation (n-fold, DK/DR), PBKDF2 string-to-key, and the
/// confidentiality/integrity construction in RFC 3961 §5.3 (encrypt = AES-CBC-CTS of confounder+plaintext,
/// trailed by an HMAC-SHA1-96 of the same span, keyed separately).
/// </summary>
internal static class Aes3961
{
    private const int BlockSize = 16;

    /// <summary>HMAC-SHA1-96 (RFC 3962 §6, "HMAC output size, h")</summary>
    private const int ChecksumSize = 12;

    public static int KeySize(KerberosEncryptionType type) => type switch
    {
        KerberosEncryptionType.Aes128CtsHmacSha1 => 16,
        KerberosEncryptionType.Aes256CtsHmacSha1 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an AES encryption type."),
    };

    /// <summary>
    /// String-to-key (RFC 3962 §4): PBKDF2-HMAC-SHA1(password, salt, iterations, keysize), then DK(., "kerberos").
    /// <paramref name="iterations"/> defaults to 4,096 (RFC 3962's default string-to-key parameter, "00 00 10 00"),
    /// used when the KDC's ETYPE-INFO2 supplied no s2kparams.
    /// </summary>
    public static byte[] StringToKey(KerberosEncryptionType type, string password, string salt, int iterations = 4096)
    {
        KeySize(type); // rejects anything but the two AES types
        var etype = (EncryptionType)(int)type;
        var parameters = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(parameters, iterations);
        var key = new KerberosKey(password, null, null, salt, null, etype, SaltType.Rfc4120, parameters);
        return key.GetKey(CryptoService.CreateTransform(etype)).ToArray();
    }

    /// <summary>Kc/Ke/Ki (RFC 3961 §5.3): DK(baseKey, usage as 4 big-endian octets, followed by the selector octet).</summary>
    public static byte[] DeriveUsageKey(ReadOnlySpan<byte> baseKey, KerberosKeyUsage usage, byte selector)
    {
        Span<byte> constant = stackalloc byte[5];
        BinaryPrimitives.WriteInt32BigEndian(constant, (int)usage);
        constant[4] = selector;
        return DeriveKey(baseKey, baseKey.Length, constant);
    }

    /// <summary>DK(key, constant) = random-to-key(DR(key, constant)); random-to-key is the identity for AES (RFC 3962 §6).</summary>
    private static byte[] DeriveKey(ReadOnlySpan<byte> key, int outputSize, ReadOnlySpan<byte> constant)
    {
        using var aes = Aes.Create();
        aes.SetKey(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        // DR(Key, Constant) = k-truncate(K1 | K2 | ...), Ki = E(Key, n-fold(Constant)) then E(Key, K(i-1)): a
        // single-block AES-ECB feedback chain (RFC 3961 §5.1), not real CBC — each step starts fresh.
        var block = NFold(constant, BlockSize);
        var output = new byte[((outputSize + BlockSize - 1) / BlockSize) * BlockSize];
        var offset = 0;
        while (offset < output.Length)
        {
            block = EncryptBlock(encryptor, block);
            block.CopyTo(output, offset);
            offset += BlockSize;
        }

        return output[..outputSize];
    }

    private static byte[] EncryptBlock(ICryptoTransform encryptor, byte[] block)
    {
        var output = new byte[BlockSize];
        encryptor.TransformBlock(block, 0, BlockSize, output, 0);
        return output;
    }

    /// <summary>
    /// n-fold (RFC 3961 §5.1/Appendix A): stretches or shrinks <paramref name="input"/> to exactly
    /// <paramref name="outputBytes"/>, replicating it to the LCM of its length and the output length (rotating
    /// right by 13 bits before each repetition), then summing the resulting <c>outputBytes</c>-sized chunks
    /// with 1's-complement (end-around-carry) addition.
    /// </summary>
    internal static byte[] NFold(ReadOnlySpan<byte> input, int outputBytes)
    {
        if (input.Length == 0)
            throw new ArgumentException("n-fold needs a non-empty input.", nameof(input));

        var inputBits = input.Length * 8;
        var outputBits = outputBytes * 8;
        var lcm = Lcm(inputBits, outputBits);

        // Build the replicated, rotated bit stream (lcm bits long) a whole copy of `input` at a time; each
        // successive copy is the previous one rotated right by 13 bits over the FULL inputBits-bit value.
        var totalBytes = lcm / 8;
        var stream = new byte[totalBytes];
        var copies = lcm / inputBits;
        var rotationBits = 0;
        for (var c = 0; c < copies; c++)
        {
            CopyRotated(input, rotationBits, stream.AsSpan(c * input.Length, input.Length));
            rotationBits += 13;
        }

        // Sum the `outputBytes`-sized chunks with 1's-complement addition (end-around carry).
        var result = new byte[outputBytes];
        for (var chunkStart = 0; chunkStart < totalBytes; chunkStart += outputBytes)
        {
            var carry = 0;
            for (var i = outputBytes - 1; i >= 0; i--)
            {
                var sum = result[i] + stream[chunkStart + i] + carry;
                result[i] = (byte)sum;
                carry = sum >> 8;
            }

            while (carry != 0)
                for (var i = outputBytes - 1; i >= 0 && carry != 0; i--)
                {
                    var sum = result[i] + carry;
                    result[i] = (byte)sum;
                    carry = sum >> 8;
                }
        }

        return result;
    }

    /// <summary>Writes <paramref name="input"/>, treated as a big-endian bit string, rotated right by <paramref name="rotationBits"/>, into <paramref name="destination"/> (same length as <paramref name="input"/>).</summary>
    private static void CopyRotated(ReadOnlySpan<byte> input, int rotationBits, Span<byte> destination)
    {
        var totalBits = input.Length * 8;
        rotationBits %= totalBits;
        for (var bit = 0; bit < totalBits; bit++)
        {
            var sourceBit = (bit - rotationBits % totalBits + totalBits) % totalBits;
            var sourceByte = sourceBit / 8;
            var sourceBitInByte = 7 - (sourceBit % 8);
            var value = (input[sourceByte] >> sourceBitInByte) & 1;

            var destByte = bit / 8;
            var destBitInByte = 7 - (bit % 8);
            if (value != 0)
                destination[destByte] |= (byte)(1 << destBitInByte);
            else
                destination[destByte] &= (byte)~(1 << destBitInByte);
        }
    }

    private static int Lcm(int a, int b) => a / Gcd(a, b) * b;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a;
    }

    /// <summary>
    /// The RFC 3961 §5.3 simplified-profile "encryption function": a random confounder, then AES-CBC-CTS of
    /// confounder+plaintext (no padding: the AES profile's message block size is 1 octet), trailed by an HMAC-SHA1-96
    /// keyed with Ki.
    /// </summary>
    /// <remarks>
    /// The checksum covers the confounder and the plaintext, and optionally buffers that are signed but never encrypted
    /// (RPC header signing, MS-KILE 3.4.5.4: the checksum's "plaintext-data" is all the input buffers in order, the
    /// encrypted one's only the encrypted buffers): confounder | <paramref name="signedBefore"/> | plaintext[..<paramref name="afterOffset"/>] |
    /// <paramref name="signedAfter"/> | plaintext[<paramref name="afterOffset"/>..]. Both are empty for every Kerberos message's own
    /// enc-part (AS-REQ/TGS-REQ/AP-REQ/...).
    /// </remarks>
    public static (byte[] CipherCore, byte[] Checksum) EncryptCore(
        KerberosEncryptionType type, ReadOnlySpan<byte> baseKey, KerberosKeyUsage usage, ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> signedBefore = default, ReadOnlySpan<byte> signedAfter = default, int afterOffset = 0)
    {
        var ke = DeriveUsageKey(baseKey, usage, 0xAA);
        var ki = DeriveUsageKey(baseKey, usage, 0x55);

        var confAndPlaintext = new byte[BlockSize + plaintext.Length];
        RandomNumberGenerator.Fill(confAndPlaintext.AsSpan(0, BlockSize));
        plaintext.CopyTo(confAndPlaintext.AsSpan(BlockSize));

        var cipherCore = AesCts.Encrypt(ke, confAndPlaintext);
        return (cipherCore, Checksum(ki, confAndPlaintext, signedBefore, signedAfter, afterOffset));
    }

    /// <summary>The reverse of <see cref="EncryptCore"/>: decrypts <paramref name="cipherCore"/> and checks <paramref name="checksum"/>.</summary>
    /// <exception cref="SecurityProviderException">The checksum does not match.</exception>
    public static byte[] DecryptCore(
        KerberosEncryptionType type, ReadOnlySpan<byte> baseKey, KerberosKeyUsage usage, ReadOnlySpan<byte> cipherCore, ReadOnlySpan<byte> checksum,
        ReadOnlySpan<byte> signedBefore = default, ReadOnlySpan<byte> signedAfter = default, int afterOffset = 0)
    {
        var ke = DeriveUsageKey(baseKey, usage, 0xAA);
        var ki = DeriveUsageKey(baseKey, usage, 0x55);

        var confAndPlaintext = AesCts.Decrypt(ke, cipherCore);
        var expected = Checksum(ki, confAndPlaintext, signedBefore, signedAfter, afterOffset);
        if (!CryptographicOperations.FixedTimeEquals(expected, checksum))
            throw new SecurityProviderException(
                "An AES-HMAC-SHA1-96 checksum did not match: the data was altered, or the key is wrong.");

        return confAndPlaintext[BlockSize..];
    }

    private static byte[] Checksum(ReadOnlySpan<byte> ki, ReadOnlySpan<byte> confAndPlaintext, ReadOnlySpan<byte> signedBefore,
        ReadOnlySpan<byte> signedAfter, int afterOffset)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, ki);
        hmac.AppendData(confAndPlaintext[..BlockSize]);
        hmac.AppendData(signedBefore);
        hmac.AppendData(confAndPlaintext.Slice(BlockSize, afterOffset));
        hmac.AppendData(signedAfter);
        hmac.AppendData(confAndPlaintext[(BlockSize + afterOffset)..]);
        return hmac.GetHashAndReset()[..ChecksumSize];
    }

    /// <summary>
    /// The simple, concatenated wire format Kerberos messages use for an EncryptedData's cipher bytes
    /// (RFC 3961 §5.3: "ciphertext = C1 | H1[1..h]"), the format of the ciphertext in a Kerberos message.
    /// </summary>
    public static byte[] Encrypt(KerberosEncryptionType type, ReadOnlySpan<byte> baseKey, KerberosKeyUsage usage, ReadOnlySpan<byte> plaintext)
    {
        var (core, checksum) = EncryptCore(type, baseKey, usage, plaintext);
        var result = new byte[core.Length + checksum.Length];
        core.CopyTo(result, 0);
        checksum.CopyTo(result, core.Length);
        return result;
    }

    public static byte[] Decrypt(KerberosEncryptionType type, ReadOnlySpan<byte> baseKey, KerberosKeyUsage usage, ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockSize + ChecksumSize)
            throw new SecurityProviderException(
                "AES-HMAC-SHA1-96 ciphertext is too short to hold its confounder and checksum.");

        var core = data[..^ChecksumSize];
        var checksum = data[^ChecksumSize..];
        return DecryptCore(type, baseKey, usage, core, checksum);
    }

    /// <summary>get_mic (RFC 3961 §5.4): HMAC-SHA1-96 keyed with Kc, used for integrity-only (unsealed) GSS tokens.</summary>
    public static byte[] GetMic(ReadOnlySpan<byte> baseKey, KerberosKeyUsage usage, ReadOnlySpan<byte> before, ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> after)
    {
        var kc = DeriveUsageKey(baseKey, usage, 0x99);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, kc);
        hmac.AppendData(before);
        hmac.AppendData(body);
        hmac.AppendData(after);
        return hmac.GetHashAndReset()[..ChecksumSize];
    }
}

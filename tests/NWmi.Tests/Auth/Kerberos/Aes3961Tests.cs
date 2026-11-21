using NWmi.Auth.Kerberos;

namespace NWmi.Tests.Auth.Kerberos;

/// <summary>
/// RFC 3961 Appendix A (n-fold) and RFC 3962 Appendix B (PBKDF2 string-to-key, AES-CBC-CTS) test vectors,
/// checked byte-exact.
/// </summary>
public class Aes3961Tests
{
    [Theory]
    [InlineData("012345", 8, "be072631276b1955")]
    [InlineData("password", 7, "78a07b6caf85fa")]
    [InlineData("Rough Consensus, and Running Code", 8, "bb6ed30870b7f0e0")]
    [InlineData("password", 21, "59e4a8ca7c0385c3c37b3f6d2000247cb6e6bd5b3e")]
    [InlineData("MASSACHVSETTS INSTITVTE OF TECHNOLOGY", 24, "db3b0d8f0b061e603282b308a50841229ad798fab9540c1b")]
    [InlineData("Q", 21, "518a54a215a8452a518a54a215a8452a518a54a215")]
    [InlineData("ba", 21, "fb25d531ae8974499f52fd92ea9857c4ba24cf297e")]
    [InlineData("kerberos", 8, "6b657262 65726f73")]
    [InlineData("kerberos", 16, "6b657262 65726f73 7b9b5b2b 93132b93")]
    [InlineData("kerberos", 21, "8372c236 344e5f15 50cd0747 e15d62ca 7a5a3bce a4")]
    [InlineData("kerberos", 32, "6b657262 65726f73 7b9b5b2b 93132b93 5c9bdcda d95c9899 c4cae4de e6d6cae4")]
    public void NFold_matches_rfc_3961_appendix_a(string input, int outputBytes, string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex.Replace(" ", ""));
        var actual = Aes3961.NFold(System.Text.Encoding.ASCII.GetBytes(input), outputBytes);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, "password", "ATHENA.MIT.EDUraeburn", 17, "4226 3c6e 89f4 fc28 b8df 68ee 0979 9f15")]
    [InlineData(1, "password", "ATHENA.MIT.EDUraeburn", 18, "fe69 7b52 bc0d 3ce1 4432 ba03 6a92 e65b bb52 2809 90a2 fa27 8839 98d7 2af3 0161")]
    [InlineData(2, "password", "ATHENA.MIT.EDUraeburn", 17, "c651 bf29 e230 0ac2 7fa4 69d6 93bd da13")]
    [InlineData(1200, "password", "ATHENA.MIT.EDUraeburn", 18, "55a6 ac74 0ad1 7b48 4694 1051 e1e8 b0a7 548d 93b0 ab30 a8bc 3ff1 6280 382b 8c2a")]
    [InlineData(50, "𝄞", "EXAMPLE.COMpianist", 17, "f149 c1f2 e154 a734 52d4 3e7f e62a 56e5")]
    public void StringToKey_matches_rfc_3962_appendix_b(int iterations, string password, string salt, int etype, string expectedHex)
    {
        // The g-clef test vector's pass phrase is U+1D11E (surrogate pair 𝄞 in UTF-16); RFC 3962
        // feeds its raw UTF-8 bytes (f0 9d 84 9e) into PBKDF2, which .NET's UTF8 encoder reproduces.
        var expected = Convert.FromHexString(expectedHex.Replace(" ", ""));
        var actual = Aes3961.StringToKey((KerberosEncryptionType)etype, password, salt, iterations);
        Assert.Equal(expected, actual);
    }

    [Theory]
    // AES-128 key "chicken teriyaki", zero IV, increasing message lengths (RFC 3962 Appendix B).
    [InlineData(
        "636869636b656e207465726979616b69",
        "4920776f756c64206c696b652074686520",
        "c6353568f2bf8cb4d8a580362da7ff7f97")]
    [InlineData(
        "636869636b656e207465726979616b69",
        "4920776f756c64206c696b65207468652047656e6572616c20476175277320",
        "fc00783e0efdb2c1d445d4c8eff7ed2297687268d6ecccc0c07b25e25ecfe5")]
    [InlineData(
        "636869636b656e207465726979616b69",
        "4920776f756c64206c696b65207468652047656e6572616c2047617527732043",
        "39312523a78662d5be7fcbcc98ebf5a897687268d6ecccc0c07b25e25ecfe584")]
    public void AesCts_matches_rfc_3962_appendix_b(string keyHex, string plaintextHex, string expectedHex)
    {
        var key = Convert.FromHexString(keyHex.Replace(" ", ""));
        var plaintext = Convert.FromHexString(plaintextHex.Replace(" ", ""));
        var expected = Convert.FromHexString(expectedHex.Replace(" ", ""));

        var ciphertext = AesCts.Encrypt(key, plaintext);
        Assert.Equal(expected, ciphertext);

        var decrypted = AesCts.Decrypt(key, ciphertext);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesCts_of_exactly_one_block_is_the_plain_aes_block_and_a_shorter_input_is_rejected()
    {
        // RFC 3962 §5: with one block there is nothing to swap. It happens when a Kerberos message has an empty body
        // (the 16-byte confounder alone).
        var key = Convert.FromHexString("636869636b656e207465726979616b69");
        var block = Convert.FromHexString("4920776f756c64206c696b6520746865");
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        var expected = aes.EncryptEcb(block, System.Security.Cryptography.PaddingMode.None);

        var ciphertext = AesCts.Encrypt(key, block);
        Assert.Equal(expected, ciphertext);
        Assert.Equal(block, AesCts.Decrypt(key, ciphertext));

        Assert.Throws<ArgumentException>(() => AesCts.Encrypt(key, block.AsSpan(0, 9)));
        Assert.Throws<ArgumentException>(() => AesCts.Decrypt(key, block.AsSpan(0, 9)));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    public void Encrypt_then_decrypt_round_trips_for_every_length_near_block_boundaries(int etype)
    {
        var type = (KerberosEncryptionType)etype;
        var key = new byte[Aes3961.KeySize(type)];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        for (var length = 0; length <= 40; length++)
        {
            var plaintext = new byte[length];
            System.Security.Cryptography.RandomNumberGenerator.Fill(plaintext);

            var ciphertext = Aes3961.Encrypt(type, key, usage: KerberosKeyUsage.AsRepEncryptedPart, plaintext);
            var decrypted = Aes3961.Decrypt(type, key, usage: KerberosKeyUsage.AsRepEncryptedPart, ciphertext);
            Assert.Equal(plaintext, decrypted);
        }
    }

    [Fact]
    public void Decrypt_rejects_a_tampered_ciphertext()
    {
        var key = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        var ciphertext = Aes3961.Encrypt(KerberosEncryptionType.Aes128CtsHmacSha1, key, usage: KerberosKeyUsage.AsRepEncryptedPart, "hello, kdc"u8);
        ciphertext[^1] ^= 0xFF;

        Assert.Throws<NWmi.Auth.SecurityProviderException>(() => Aes3961.Decrypt(KerberosEncryptionType.Aes128CtsHmacSha1, key, usage: KerberosKeyUsage.AsRepEncryptedPart, ciphertext));
    }
}

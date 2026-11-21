using System.Text;
using NWmi.Auth.Ntlm;

namespace NWmi.Tests.Auth;

public class CryptoPrimitiveTests
{
    // RFC 1320 appendix A.5 test suite.
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Md4_matches_rfc_1320(string input, string expectedHex)
    {
        Assert.Equal(expectedHex, Convert.ToHexString(Md4.HashData(Encoding.ASCII.GetBytes(input))).ToLowerInvariant());
    }

    [Theory]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(119)]
    [InlineData(120)]
    public void Md4_handles_the_padding_boundaries(int length)
    {
        // Only the lengths around the 55/56/64 byte boundaries change how padding spills into a new block.
        var hash = Md4.HashData(new byte[length]);
        Assert.Equal(Md4.HashSize, hash.Length);
        Assert.NotEqual(hash, Md4.HashData(new byte[length + 1]));
    }

    [Fact]
    public void Md4_of_the_utf16_password_is_the_ntlm_hash()
    {
        // MS-NLMP 4.2.2.1.2: NTOWFv1("Password") = a4f49c406510bdcab6824ee7c30fd852
        var hash = Md4.HashData(Encoding.Unicode.GetBytes("Password"));
        Assert.Equal("a4f49c406510bdcab6824ee7c30fd852", Convert.ToHexString(hash).ToLowerInvariant());
    }

    // Classic RC4 vectors.
    [Theory]
    [InlineData("Key", "Plaintext", "bbf316e8d940af0ad3")]
    [InlineData("Wiki", "pedia", "1021bf0420")]
    [InlineData("Secret", "Attack at dawn", "45a01f645fc35b383552544b9bf5")]
    public void Rc4_matches_known_vectors(string key, string plaintext, string expectedHex)
    {
        var cipher = Rc4.Crypt(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(plaintext));
        Assert.Equal(expectedHex, Convert.ToHexString(cipher).ToLowerInvariant());
    }

    [Fact]
    public void Rc4_keystream_continues_across_calls()
    {
        var key = Encoding.ASCII.GetBytes("Key");
        var whole = Rc4.Crypt(key, Encoding.ASCII.GetBytes("Plaintext"));

        var rc4 = new Rc4(key);
        var first = rc4.Transform(Encoding.ASCII.GetBytes("Plain"));
        var second = rc4.Transform(Encoding.ASCII.GetBytes("text"));

        Assert.Equal(whole, first.Concat(second).ToArray());
    }

    [Fact]
    public void Rc4_can_work_in_place_and_decrypts_by_repeating()
    {
        var key = new byte[] { 1, 2, 3, 4, 5 };
        var data = Encoding.ASCII.GetBytes("round trip");
        var buffer = (byte[])data.Clone();

        new Rc4(key).Transform(buffer, buffer);
        Assert.NotEqual(data, buffer);
        new Rc4(key).Transform(buffer, buffer);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void Rc4_rejects_bad_keys_and_short_outputs()
    {
        Assert.Throws<ArgumentException>(() => new Rc4([]));
        Assert.Throws<ArgumentException>(() => new Rc4(new byte[257]));
        Assert.Throws<ArgumentException>(() => new Rc4([1]).Transform(new byte[4], new byte[2]));
    }
}

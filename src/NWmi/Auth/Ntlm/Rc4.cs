using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace NWmi.Auth.Ntlm;

internal sealed class Rc4
{
    private readonly RC4Engine _engine = new();

    public Rc4(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 1 or > 256)
            throw new ArgumentException("An RC4 key is 1 to 256 bytes.", nameof(key));

        _engine.Init(true, new KeyParameter(key));
    }

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output is shorter than the input.", nameof(output));

        _engine.ProcessBytes(input, output[..input.Length]);
    }

    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        Transform(input, output);
        return output;
    }

    public static byte[] Crypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => new Rc4(key).Transform(data);
}

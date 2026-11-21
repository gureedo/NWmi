namespace NWmi.Tests.Ndr;

internal static class Hex
{
    public static byte[] Parse(string text) =>
        Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal).Replace("|", "", StringComparison.Ordinal));
}

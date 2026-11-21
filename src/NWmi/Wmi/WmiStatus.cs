namespace NWmi.Wmi;

internal static class WmiStatus
{
    public static bool IsError(int hresult) => hresult < 0;

    public static string Describe(int hresult)
    {
        var value = unchecked((uint)hresult);
        return Enum.IsDefined(typeof(WbemStatus), value) ? ((WbemStatus)value).ToString() : $"0x{value:X8}";
    }

    public static void ThrowIfError(int hresult, string message)
    {
        if (IsError(hresult))
            throw new WmiException(hresult, message);
    }
}

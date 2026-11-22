using NWmi.Wmi;

namespace NWmi;

/// <summary>A WMI method failed; <see cref="Exception.HResult"/> and <see cref="StatusName"/> identify how.</summary>
public sealed class WmiException : Exception
{
    /// <summary>Creates the exception for a WMI status code.</summary>
    public WmiException(int hresult, string message)
        : base($"{message} ({WmiStatus.Describe(hresult)}, 0x{unchecked((uint)hresult):X8})")
    {
        HResult = hresult;
    }

    /// <summary>The symbolic <see cref="WbemStatus"/> name, or the hex value when it is not a known one.</summary>
    public string StatusName => WmiStatus.Describe(HResult);
}

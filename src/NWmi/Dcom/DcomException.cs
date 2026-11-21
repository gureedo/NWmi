namespace NWmi.Dcom;

/// <summary>Thrown when a DCOM call fails; <see cref="Exception.HResult"/> carries the status where there is one.</summary>
public sealed class DcomException : Exception
{
    /// <summary>Creates the exception for the Win32 error or HRESULT a method returned.</summary>
    public DcomException(int hresult, string message)
        : base($"{message} (status 0x{unchecked((uint)hresult):X8})")
    {
        HResult = hresult;
    }

    /// <summary>Creates the exception with a message.</summary>
    public DcomException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public DcomException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

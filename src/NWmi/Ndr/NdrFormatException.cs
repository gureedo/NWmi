namespace NWmi.Ndr;

/// <summary>Thrown when NDR data is malformed or does not fit the expected structure.</summary>
public sealed class NdrFormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public NdrFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public NdrFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

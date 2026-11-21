namespace NWmi.Wmio;

/// <summary>Thrown when WMIO data is malformed. Decoders throw this and nothing else for bad input.</summary>
public sealed class WmioFormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public WmioFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public WmioFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

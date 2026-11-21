namespace NWmi.WinRm;

/// <summary>A failure talking WS-Management: transport, authentication or a SOAP fault.</summary>
public class WsmanException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    public WsmanException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the given message and the failure that caused it.</summary>
    public WsmanException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

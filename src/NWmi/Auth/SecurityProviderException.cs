namespace NWmi.Auth;

/// <summary>Thrown when authentication fails or a security token is invalid.</summary>
public sealed class SecurityProviderException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public SecurityProviderException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public SecurityProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

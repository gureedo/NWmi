namespace NWmi.Rpc;

/// <summary>Base class for errors raised by the RPC layer itself (not by I/O).</summary>
public class RpcException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public RpcException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public RpcException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The peer sent something that is not a valid PDU, or a valid PDU at the wrong time.</summary>
public sealed class RpcProtocolException : RpcException
{
    /// <summary>Creates the exception with a message.</summary>
    public RpcProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public RpcProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The server answered a call with a rpc_fault PDU.</summary>
public sealed class RpcFaultException : RpcException
{
    /// <summary>
    /// Creates the exception from a fault PDU: its status, whether the call did not execute, and any
    /// extended error information.
    /// </summary>
    public RpcFaultException(uint status, bool didNotExecute, byte[]? extendedErrorInfo)
        : base($"RPC fault 0x{status:X8}: {RpcStatus.Describe(status)}")
    {
        Status = status;
        Win32Status = RpcStatus.ToWin32(status);
        DidNotExecute = didNotExecute;
        ExtendedErrorInfo = extendedErrorInfo;
    }

    /// <summary>The raw status from the PDU: usually a Win32 error code, sometimes a DCE <c>nca_s_*</c> code.</summary>
    public uint Status { get; }

    /// <summary>The status as a Win32 error code (MS-RPCE 3.1.1.5.5); equal to <see cref="Status"/> when not mapped.</summary>
    public uint Win32Status { get; }

    /// <summary>True when the server says the call was not executed, so it is safe to retry.</summary>
    public bool DidNotExecute { get; }

    /// <summary>Undecoded RPC extended error information (MS-EERR), if the server sent any.</summary>
    public byte[]? ExtendedErrorInfo { get; }
}

/// <summary>The server refused a bind or alter_context.</summary>
public sealed class RpcBindRejectedException : RpcException
{
    /// <summary>Creates the exception with a message.</summary>
    public RpcBindRejectedException(string message) : base(message)
    {
    }
}

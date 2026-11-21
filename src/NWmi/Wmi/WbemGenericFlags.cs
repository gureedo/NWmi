namespace NWmi.Wmi;

/// <summary>WBEM_GENERIC_FLAG_TYPE (MS-WMI Appendix A): the <c>lFlags</c> bits most <c>IWbemServices</c> methods accept.</summary>
[Flags]
public enum WbemGenericFlags : uint
{
    /// <summary>Return only when the operation is complete (WBEM_FLAG_RETURN_WBEM_COMPLETE).</summary>
    ReturnWbemComplete = 0x0,

    /// <summary>Return at once and let the results arrive later (WBEM_FLAG_RETURN_IMMEDIATELY).</summary>
    ReturnImmediately = 0x10,

    /// <summary>The enumerator can only move forward (WBEM_FLAG_FORWARD_ONLY).</summary>
    ForwardOnly = 0x20,

    /// <summary>Do not return an extended error object (WBEM_FLAG_NO_ERROR_OBJECT).</summary>
    NoErrorObject = 0x40,

    /// <summary>Send status updates to the sink (WBEM_FLAG_SEND_STATUS).</summary>
    SendStatus = 0x80,

    /// <summary>Include the system properties that locate the object (WBEM_FLAG_ENSURE_LOCATABLE).</summary>
    EnsureLocatable = 0x100,

    /// <summary>Read the object from the provider's own class, not a derived one (WBEM_FLAG_DIRECT_READ).</summary>
    DirectRead = 0x200,

    /// <summary>Return localized (amended) qualifiers (WBEM_FLAG_USE_AMENDED_QUALIFIERS).</summary>
    UseAmendedQualifiers = 0x20000,

    /// <summary>Validate the query strictly (WBEM_FLAG_STRONG_VALIDATION).</summary>
    StrongValidation = 0x100000,
}

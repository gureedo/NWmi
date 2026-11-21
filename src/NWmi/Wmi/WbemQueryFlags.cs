namespace NWmi.Wmi;

/// <summary>WBEM_QUERY_FLAG_TYPE: how deep <c>CreateClassEnum</c>/<c>CreateInstanceEnum</c> walk the class hierarchy.</summary>
[Flags]
public enum WbemQueryFlags : uint
{
    /// <summary>Include subclasses (WBEM_FLAG_DEEP).</summary>
    Deep = 0x00,

    /// <summary>Only the class itself, not its subclasses (WBEM_FLAG_SHALLOW).</summary>
    Shallow = 0x01,

    /// <summary>Return the prototype of the result, not the results (WBEM_FLAG_PROTOTYPE).</summary>
    Prototype = 0x02,
}

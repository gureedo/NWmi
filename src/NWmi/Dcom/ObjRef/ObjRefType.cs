namespace NWmi.Dcom;

/// <summary>The form of a marshaled interface pointer: the flags field of an OBJREF (MS-DCOM 2.2.18).</summary>
internal enum ObjRefType : uint
{
    /// <summary>OBJREF_STANDARD: the object's identity plus the bindings of its object resolver.</summary>
    Standard = 1,

    /// <summary>OBJREF_HANDLER: like the standard form, plus the class of a client-side handler.</summary>
    Handler = 2,

    /// <summary>OBJREF_CUSTOM: opaque data for a custom marshaler.</summary>
    Custom = 4,

    /// <summary>OBJREF_EXTENDED: the standard form followed by extension data. Not supported.</summary>
    Extended = 8,
}

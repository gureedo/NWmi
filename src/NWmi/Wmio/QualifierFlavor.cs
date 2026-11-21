namespace NWmi.Wmio;

/// <summary>WBEM_FLAVOR_* bits (MS-WMIO 2.2.62).</summary>
[Flags]
internal enum QualifierFlavor : byte
{
    None = 0,
    PropagateToInstance = 0x01,
    PropagateToDerivedClass = 0x02,
    NotOverridable = 0x10,
    OriginPropagated = 0x20,
    OriginSystem = 0x40,
    Amended = 0x80,
}

namespace NWmi.Wmio;

[Flags]
internal enum MethodFlags : byte
{
    None = 0,

    /// <summary>The method is inherited from a parent class rather than defined by this class.</summary>
    OriginPropagated = 0x20,
}

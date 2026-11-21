namespace NWmi.Dcom.ActivationProperty;

/// <summary>A property this library has no model for, kept as the bytes it came with (servers SHOULD be ignored on those).</summary>
internal sealed class UnknownActivationProperty(Guid clsid, byte[] data) : IActivationProperty
{
    public Guid Clsid { get; } = clsid;

    /// <summary>The property's NDR data as received (including any padding the framing added).</summary>
    public byte[] Data { get; } = data;

    public byte[] Encode() => Data;
}

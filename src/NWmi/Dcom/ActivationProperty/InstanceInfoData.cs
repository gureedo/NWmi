namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// InstanceInfoData (MS-DCOM 2.2.22.2.3).
/// </summary>
internal sealed class InstanceInfoData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001ad-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    public byte[] Encode() => throw new NotImplementedException();
}

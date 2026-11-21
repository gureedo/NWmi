namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// One property of an activation properties BLOB (MS-DCOM 2.2.22.2): the CLSID that names it in the CustomHeader and
/// its NDR encoding without the type serialization framing. Every property type implements this; the types also offer
/// the CLSID as a static <c>PropertyClsid</c> for looking one up.
/// </summary>
internal interface IActivationProperty
{
    /// <summary>The CLSID that identifies this property in <c>CustomHeader.pclsid</c>.</summary>
    Guid Clsid { get; }

    /// <summary>The NDR encoding, without the type serialization framing.</summary>
    byte[] Encode();
}

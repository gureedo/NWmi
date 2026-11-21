using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>
/// Wraps and unwraps the custom marshaling an <c>IWbemClassObject*</c> parameter travels in (MS-WMI 2.2.4):
/// an OBJREF_CUSTOM whose <see cref="CustomObjRef.ObjectData"/> is exactly the EncodingUnit
/// <see cref="WmioObjectBlock.DecodeEncodingUnit"/> expects (MS-WMIO 2.2.1), inside the MInterfacePointer
/// every interface-pointer parameter uses (MS-DCOM 2.2.14).
/// </summary>
internal static class WmiClassObjectCodec
{
    /// <summary>Writes an <c>[in] IWbemClassObject*</c> or <c>[in,out,unique] IWbemClassObject**</c> parameter.</summary>
    public static void WriteParameter(NdrWriter writer, WmioObjectBlock? value) =>
        writer.WriteMInterfacePointer(value is null ? null : ToObjRefBytes(value));

    /// <summary>
    /// Reads an <c>IWbemClassObject*</c> parameter at the top level of a reply (where <see cref="NWmi.Ndr.NdrReader.ReadUniquePointer{T}"/>
    /// resolves immediately), returning null for a null interface pointer.
    /// </summary>
    public static WmioObjectBlock? ReadParameter(NdrReader reader)
    {
        var pointer = reader.ReadMInterfacePointer();
        return pointer.IsNull ? null : Decode(pointer.Value);
    }

    public static byte[] ToObjRefBytes(WmioObjectBlock value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new CustomObjRef(WmiIds.IidWbemClassObject, WmiIds.ClsidWbemClassObject, value.EncodeEncodingUnit())
            .ToBytes();
    }

    public static WmioObjectBlock Decode(byte[]? objRefBytes)
    {
        ArgumentNullException.ThrowIfNull(objRefBytes);
        if (ObjRef.Parse(objRefBytes) is not CustomObjRef custom || custom.Clsid != WmiIds.ClsidWbemClassObject)
            throw new FormatException("The interface pointer is not a custom-marshaled IWbemClassObject.");

        return WmioObjectBlock.DecodeEncodingUnit(custom.ObjectData);
    }
}

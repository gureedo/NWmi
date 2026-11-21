using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class RemoteCreateInstanceRequestTests
{
    private static readonly Guid Cid = Guid.Parse("12345678-9abc-def0-1122-334455667788");
    private static readonly Guid ClassId = Guid.Parse("8bc3f05e-d86b-11d0-a075-00c04fb68820"); // WbemLevel1Login
    private static readonly Guid Login = Guid.Parse("f309ad18-d86a-11d0-a075-00c04fb68820");

    private static byte[] Request() =>
        RemoteScmActivator.BuildRemoteCreateInstanceRequest(Cid, ClassId, [Login], [DualStringArray.TcpTowerId]);

    [Fact]
    public void The_stub_starts_with_ORPCTHIS_then_a_null_outer_pointer_then_the_properties()
    {
        var stub = Request();

        Assert.Equal(
            Hex.Parse("0500 0700 | 00000000 | 00000000 | 78563412 bc9a f0de 1122334455667788 | 00000000"),
            stub[..32]);

        var reader = new NdrReader(stub.AsMemory()[32..]);
        Assert.Equal(0u, reader.ReadUInt32()); // pUnkOuter
        Assert.Equal(NdrWriter.FirstReferentId, BitConverter.ToUInt32(stub, 36)); // the first pointer ever allocated
    }

    [Fact]
    public void The_properties_are_an_ActivationPropertiesIn_object_reference()
    {
        var reader = new NdrReader(Request().AsMemory()[32..]);
        reader.ReadUInt32(); // pUnkOuter
        var objRefBytes = reader.ReadMInterfacePointer().Value!;

        var objRef = Assert.IsType<CustomObjRef>(ObjRef.Parse(objRefBytes));
        Assert.Equal(DcomIds.IActivationPropertiesIn, objRef.Iid);
        Assert.Equal(DcomIds.ActivationPropertiesIn, objRef.Clsid);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void The_blob_holds_the_properties_a_real_windows_client_sends_in_order()
    {
        // A real Windows endpoint mapper rejected activation (STG_E_READFAULT) when SpecialPropertiesData was
        // missing, even though MS-DCOM's own table marks it Optional; footnote <77> says Windows clients always
        // send it (and InstantiationInfoData/ScmRequestInfoData/LocationInfoData), which is what this asserts.
        var reader = new NdrReader(Request().AsMemory()[32..]);
        reader.ReadUInt32();
        var custom = (CustomObjRef)ObjRef.Parse(reader.ReadMInterfacePointer().Value!);

        var properties = ActivationPropertiesBlob.Parse(custom.ObjectData);

        Assert.Equal(
            [
                SpecialPropertiesData.PropertyClsid, InstantiationInfoData.PropertyClsid, ActivationContextInfoData.PropertyClsid,
                SecurityInfoData.PropertyClsid, LocationInfoData.PropertyClsid, ScmRequestInfoData.PropertyClsid
            ],
            properties.Select(p => p.Clsid));

        var instantiation = Assert.IsType<InstantiationInfoData>(properties[1]);
        Assert.Equal(ClassId, instantiation.ClassId);
        Assert.Equal([Login], instantiation.RequestedInterfaces);
        Assert.Equal(
            [DualStringArray.TcpTowerId],
            Assert.IsType<ScmRequestInfoData>(properties[5]).RequestedProtocolSequences);
    }
}

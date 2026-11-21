using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class PropsOutInfoLayoutTests
{
    private const int NoInterface = unchecked((int)0x80004002); // E_NOINTERFACE

    [Fact]
    public void One_successful_interface_has_the_documented_layout()
    {
        var info = new PropsOutInfo([new InterfaceResult(DcomIds.IUnknown, 0, [1, 2, 3, 4])]);

        var expected = Hex.Parse(
            "01000000 | 00000200 | 04000200 | 08000200 | " + // cIfs and the three array pointers
            "01000000 | 00000000 0000 0000 c000 000000000046 | " + // piid: max_count, the IID
            "01000000 | 00000000 | " + // phresults: max_count, S_OK
            "01000000 | 0c000200 | " + // ppIntfData: max_count, pointer to the first object reference
            "04000000 | 04000000 | 01020304"); // that MInterfacePointer: max_count, size, bytes

        Assert.Equal(expected, info.Encode());
    }

    [Fact]
    public void A_mix_of_success_and_failure_round_trips()
    {
        var unsupported = Guid.Parse("f309ad18-d86a-11d0-a075-00c04fb68820");
        var info = new PropsOutInfo(
        [
            new InterfaceResult(DcomIds.IUnknown, 0, [9, 8, 7]),
            new InterfaceResult(unsupported, NoInterface, null),
            new InterfaceResult(DcomIds.IActivationPropertiesOut, 0, [1]),
        ]);

        var decoded = PropsOutInfo.Decode(info.Encode());

        Assert.Equal(3, decoded.Results.Count);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.Results[0].ObjRef);
        Assert.True(decoded.Results[0].Succeeded);
        Assert.Equal(unsupported, decoded.Results[1].Iid);
        Assert.Equal(NoInterface, decoded.Results[1].HResult);
        Assert.Null(decoded.Results[1].ObjRef);
        Assert.False(decoded.Results[1].Succeeded);
        Assert.Equal(new byte[] { 1 }, decoded.Results[2].ObjRef);
    }

    [Fact]
    public void Results_must_number_between_one_and_the_maximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropsOutInfo([]));
    }
}

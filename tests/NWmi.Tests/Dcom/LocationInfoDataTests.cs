using NWmi.Dcom.ActivationProperty;

namespace NWmi.Tests.Dcom;

public class LocationInfoDataTests
{
    [Fact]
    public void The_encoding_is_a_null_name_and_three_zero_fields()
    {
        Assert.Equal(new byte[16], new LocationInfoData().Encode());
    }
}

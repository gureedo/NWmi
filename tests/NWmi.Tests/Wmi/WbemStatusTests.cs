using NWmi.Wmi;

namespace NWmi.Tests.Wmi;

public class WbemStatusTests
{
    [Theory]
    [InlineData(0, false)] // WBEM_S_NO_ERROR
    [InlineData(1, false)] // WBEM_S_FALSE: a success despite being nonzero
    [InlineData(0x40004, false)] // WBEM_S_TIMEDOUT
    [InlineData(unchecked((int)0x8004100CU), true)] // WBEM_E_NOT_SUPPORTED
    [InlineData(unchecked((int)0x80041010U), true)] // WBEM_E_INVALID_CLASS
    public void IsError_follows_the_severity_bit_not_a_zero_comparison(int hresult, bool expectedError)
    {
        Assert.Equal(expectedError, WmiStatus.IsError(hresult));
    }

    [Fact]
    public void Describe_names_a_known_status_and_hex_dumps_an_unknown_one()
    {
        Assert.Equal("InvalidClass", WmiStatus.Describe(unchecked((int)0x80041010U)));
        Assert.Equal("0xDEADBEEF", WmiStatus.Describe(unchecked((int)0xDEADBEEFU)));
    }

    [Fact]
    public void ThrowIfError_throws_only_for_an_error_status()
    {
        WmiStatus.ThrowIfError((int)WbemStatus.False, "should not throw");

        var ex = Assert.Throws<WmiException>(() => WmiStatus.ThrowIfError(unchecked((int)WbemStatus.InvalidNamespace), "GetObject failed"));
        Assert.Equal(unchecked((int)WbemStatus.InvalidNamespace), ex.HResult);
        Assert.Equal("InvalidNamespace", ex.StatusName);
    }
}

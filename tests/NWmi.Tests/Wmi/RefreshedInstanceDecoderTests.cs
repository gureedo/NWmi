using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmi;

public class RefreshedInstanceDecoderTests
{
    private static readonly WmioClass Template = new("T", [], [], [], []) { ValueTableLength = 0 };

    [Fact]
    public void An_enumeration_with_the_wrong_version_is_rejected()
    {
        byte[] blob = [2, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<WmioFormatException>(() => RefreshedInstanceDecoder.DecodeEnum(Template, blob));
    }

    [Fact]
    public void An_enumeration_cut_off_inside_an_object_is_rejected()
    {
        byte[] blob = [1, 0, 0, 0, 1, 0, 0, 0, 0xFF, 0, 0, 0, 1];
        Assert.Throws<WmioFormatException>(() => RefreshedInstanceDecoder.DecodeEnum(Template, blob));
    }

    [Fact]
    public void An_empty_enumeration_has_no_instances()
    {
        byte[] blob = [1, 0, 0, 0, 0, 0, 0, 0];
        Assert.Empty(RefreshedInstanceDecoder.DecodeEnum(Template, blob));
    }
}

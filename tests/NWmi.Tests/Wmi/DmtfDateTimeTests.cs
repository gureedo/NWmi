using NWmi.Wmi;

namespace NWmi.Tests.Wmi;

public class DmtfDateTimeTests
{
    [Fact]
    public void A_moment_is_formatted_with_its_offset_in_minutes()
    {
        var moment = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(-330)).AddTicks(5_000_000);
        Assert.Equal("20260102030405.500000-330", DmtfDateTime.Format(moment));
    }

    [Fact]
    public void An_interval_is_formatted_with_days_first()
    {
        var span = new TimeSpan(1, 2, 3, 4, 500);
        Assert.Equal("00000001020304.500000:000", DmtfDateTime.Format(span));
    }

    [Fact]
    public void A_negative_interval_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DmtfDateTime.Format(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void A_formatted_moment_parses_back()
    {
        var moment = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(120)).AddTicks(1230);
        Assert.Equal(moment, DmtfDateTime.Parse(DmtfDateTime.Format(moment)));
    }

    [Fact]
    public void A_formatted_interval_parses_back()
    {
        var span = new TimeSpan(12, 3, 4, 5) + TimeSpan.FromTicks(1230);
        Assert.Equal(span, DmtfDateTime.Parse(DmtfDateTime.Format(span)));
    }

    [Theory]
    [InlineData("2026****030405.000000+000")] // wildcards have no DateTimeOffset form
    [InlineData("not a date")]
    [InlineData("")]
    public void Anything_else_comes_back_unchanged(string text) =>
        Assert.Same(text, DmtfDateTime.Parse(text));
}

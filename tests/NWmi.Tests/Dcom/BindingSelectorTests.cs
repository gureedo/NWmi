using NWmi.Dcom;

namespace NWmi.Tests.Dcom;

public class BindingSelectorTests
{
    [Fact]
    public void The_first_tcp_binding_with_an_endpoint_is_chosen()
    {
        var bindings = new DualStringArray(
            [
                new StringBinding(15, "SERVER[\\pipe\\x]"), // not TCP: skipped
                new StringBinding(DualStringArray.TcpTowerId, "SERVER"), // TCP but no endpoint: skipped
                new StringBinding(DualStringArray.TcpTowerId, "10.0.0.5[49668]"),
                new StringBinding(DualStringArray.TcpTowerId, "fe80::1[49668]"),
            ],
            []);

        var target = BindingSelector.SelectExporterTarget(bindings);

        Assert.Equal("10.0.0.5", target.Host);
        Assert.Equal(49668, target.Port);
    }

    [Fact]
    public void No_usable_binding_is_an_error()
    {
        var bindings = new DualStringArray([new StringBinding(DualStringArray.TcpTowerId, "SERVER")], []);
        Assert.Throws<DcomException>(() => BindingSelector.SelectExporterTarget(bindings));
    }

    [Fact]
    public void An_empty_binding_list_is_an_error()
    {
        Assert.Throws<DcomException>(() => BindingSelector.SelectExporterTarget(new DualStringArray([], [])));
    }
}

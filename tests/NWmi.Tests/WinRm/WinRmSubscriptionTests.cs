using System.Xml.Linq;
using NWmi.WinRm;

namespace NWmi.Tests.WinRm;

public class WinRmSubscriptionTests
{
    [Fact]
    public void Unsubscribe_carries_the_subscription_managers_reference_properties_except_the_resource_uri()
    {
        // As a Windows listener sends it: WS-Addressing 2004/08 ReferenceProperties.
        var response = XElement.Parse("""
            <e:SubscribeResponse xmlns:e="http://schemas.xmlsoap.org/ws/2004/08/eventing"
                                 xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                                 xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"
                                 xmlns:n="http://schemas.xmlsoap.org/ws/2004/09/enumeration">
              <e:SubscriptionManager>
                <a:Address>http://server01:5985/wsman</a:Address>
                <a:ReferenceProperties>
                  <w:ResourceURI>http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/*</w:ResourceURI>
                  <e:Identifier>uuid:5E1D2C3B-0000-0000-0000-000000000001</e:Identifier>
                </a:ReferenceProperties>
              </e:SubscriptionManager>
              <n:EnumerationContext>uuid:5E1D2C3B-0000-0000-0000-000000000002</n:EnumerationContext>
            </e:SubscribeResponse>
            """);

        var header = Assert.Single(WinRmBackend.SubscriptionHeaders(response));

        Assert.Equal(Wsman.Eventing + "Identifier", header.Name);
        Assert.Equal("uuid:5E1D2C3B-0000-0000-0000-000000000001", header.Value);
    }

    [Fact]
    public void A_reply_without_a_subscription_manager_gives_no_headers() =>
        Assert.Empty(WinRmBackend.SubscriptionHeaders(new XElement(Wsman.Eventing + "SubscribeResponse")));

    [Fact]
    public void ReferenceParameters_are_sent_back_too()
    {
        var response = new XElement(Wsman.Eventing + "SubscribeResponse",
            new XElement(Wsman.Eventing + "SubscriptionManager",
                new XElement(Wsman.Addressing + "ReferenceParameters",
                    new XElement(Wsman.Eventing + "Identifier", "uuid:1"))));

        Assert.Equal("uuid:1", Assert.Single(WinRmBackend.SubscriptionHeaders(response)).Value);
    }
}

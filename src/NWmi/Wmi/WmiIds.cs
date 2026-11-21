using NWmi.Rpc;

namespace NWmi.Wmi;

/// <summary>
/// Well-known GUIDs of the Windows Management Instrumentation Remote Protocol: the CLSIDs and IIDs of MS-WMI 1.9 (Standards
/// Assignments), the other interface UUIDs of the Appendix A IDL, and the unmarshaler CLSIDs of MS-WMI 2.2.4 and 2.2.13.
/// </summary>
internal static class WmiIds
{
    /// <summary>
    /// CLSID_WbemLevel1Login (MS-WMI 1.9; the <c>WbemLevel1Login</c> coclass of Appendix A), activated to obtain the first
    /// <see cref="IidWbemLevel1Login"/>.
    /// </summary>
    public static readonly Guid ClsidWbemLevel1Login = Guid.Parse("8BC3F05E-D86B-11D0-A075-00C04FB68820");

    /// <summary>IID_IWbemLevel1Login (MS-WMI 1.9, 3.1.4.1).</summary>
    public static readonly Guid IidWbemLevel1Login = Guid.Parse("F309AD18-D86A-11D0-A075-00C04FB68820");

    /// <summary>IID_IWbemServices (MS-WMI 1.9, 3.1.4.3).</summary>
    public static readonly Guid IidWbemServices = Guid.Parse("9556DC99-828C-11CF-A37E-00AA003240C7");

    /// <summary>The uuid of IEnumWbemClassObject (MS-WMI Appendix A, 3.1.4.4).</summary>
    public static readonly Guid IidEnumWbemClassObject = Guid.Parse("027947E1-D731-11CE-A357-000000000001");

    /// <summary>The uuid of IWbemCallResult (MS-WMI Appendix A, 3.1.4.5).</summary>
    public static readonly Guid IidWbemCallResult = Guid.Parse("44ACA675-E8FC-11D0-A07C-00C04FB68820");

    /// <summary>IID_IWbemContext (MS-WMI 1.9, 2.2.13).</summary>
    public static readonly Guid IidWbemContext = Guid.Parse("44ACA674-E8FC-11D0-A07C-00C04FB68820");

    /// <summary> Unmarshaler CLSID for the custom-marshaled IWbemContext buffer (MS-WMI 2.2.13; the <c>WbemContext</c> coclass of Appendix A). </summary>
    public static readonly Guid ClsidWbemContext = Guid.Parse("674B6698-EE92-11D0-AD71-00C04FD8FDFF");

    /// <summary>IID_IWbemClassObject (MS-WMI 1.9, 2.2.4).</summary>
    public static readonly Guid IidWbemClassObject = Guid.Parse("DC12A681-737F-11CF-884D-00AA004B2E24");

    /// <summary> Unmarshaler CLSID for the custom-marshaled IWbemClassObject (an EncodingUnit, MS-WMI 2.2.4). It is not the CLSID of Appendix A's <c>WbemClassObject</c> coclass, which is a different GUID. </summary>
    public static readonly Guid ClsidWbemClassObject = Guid.Parse("4590F812-1D3A-11D0-891F-00AA004B2E24");

    /// <summary>The uuid of IWbemFetchSmartEnum (MS-WMI Appendix A, 3.1.4.6).</summary>
    public static readonly Guid IidWbemFetchSmartEnum = Guid.Parse("1C1C45EE-4395-11D2-B60B-00104B703EFD");

    /// <summary>The uuid of IWbemWCOSmartEnum (MS-WMI Appendix A, 3.1.4.7).</summary>
    public static readonly Guid IidWbemWCOSmartEnum = Guid.Parse("423EC01E-2E35-11D2-B604-00104B703EFD");

    /// <summary>The uuid of IWbemRefreshingServices (MS-WMI Appendix A, 3.1.4.12).</summary>
    public static readonly Guid IidWbemRefreshingServices = Guid.Parse("2C9273E0-1DC3-11D3-B364-00105A1F8177");

    /// <summary>The uuid of IWbemRemoteRefresher (MS-WMI Appendix A, 3.1.4.13).</summary>
    public static readonly Guid IidWbemRemoteRefresher = Guid.Parse("F1E9C5B2-F59B-11D2-B362-00105A1F8177");

    /// <summary>An IID as a plain RPC abstract syntax (version 0.0, the convention DCOM interfaces use; see <c>DcomIds</c>).</summary>
    public static RpcSyntaxId AsSyntax(Guid iid) => new(iid, 0, 0);
}

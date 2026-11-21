using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>
/// Well-known GUIDs of the DCOM Remote Protocol (MS-DCOM 1.9).
/// </summary>
internal static class DcomIds
{
    // RPC interfaces.
    public static readonly RpcSyntaxId RemoteScmActivator =
        new(Guid.Parse("000001a0-0000-0000-c000-000000000046"), 0, 0);

    public static readonly RpcSyntaxId RemUnknown = new(Guid.Parse("00000131-0000-0000-c000-000000000046"), 0, 0);
    public static readonly RpcSyntaxId RemUnknown2 = new(Guid.Parse("00000143-0000-0000-c000-000000000046"), 0, 0);

    // Interface ids placed in the OBJREF header of the activation property blobs.
    public static readonly Guid IActivationPropertiesIn = Guid.Parse("000001a2-0000-0000-c000-000000000046");
    public static readonly Guid IActivationPropertiesOut = Guid.Parse("000001a3-0000-0000-c000-000000000046");

    // OBJREF_CUSTOM unmarshalers for the activation property blobs.
    public static readonly Guid ActivationPropertiesIn = Guid.Parse("00000338-0000-0000-c000-000000000046");
    public static readonly Guid ActivationPropertiesOut = Guid.Parse("00000339-0000-0000-c000-000000000046");

    /// <summary>IUnknown, the interface that activation asks for first when the caller wants only a reference.</summary>
    public static readonly Guid IUnknown = Guid.Parse("00000000-0000-0000-c000-000000000046");

    /// <summary>The iid field of a marshaled Context's OBJREF (MS-DCOM 2.2.20).</summary>
    public static readonly Guid IContext = Guid.Parse("000001c0-0000-0000-c000-000000000046");

    /// <summary>The clsid field of a marshaled Context's OBJREF_CUSTOM (MS-DCOM 2.2.20).</summary>
    public static readonly Guid ContextMarshaler = Guid.Parse("0000033b-0000-0000-c000-000000000046");
}

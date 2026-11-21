namespace NWmi.Rpc;

/// <summary>An interface or transfer syntax: a UUID and a version (<c>p_syntax_id_t</c>).</summary>
internal readonly record struct RpcSyntaxId(Guid Uuid, ushort VersionMajor, ushort VersionMinor)
{
    /// <summary>NDR 2.0, the only transfer syntax this library offers.</summary>
    public static readonly RpcSyntaxId Ndr20 = new(Guid.Parse("8a885d04-1ceb-11c9-9fe8-08002b104860"), 2, 0);

    /// <summary>
    /// The pseudo transfer syntax used for bind time feature negotiation (MS-RPCE 3.3.1.5.3). The 8 bytes of the UUID
    /// after the fixed prefix are the (empty) feature bitmask.
    /// </summary>
    public static readonly RpcSyntaxId FeatureNegotiation =
        new(Guid.Parse("6cb71c2c-9812-4540-0000-000000000000"), 1, 0);

    public static RpcSyntaxId Parse(string uuid, ushort major = 0, ushort minor = 0) =>
        new(Guid.Parse(uuid), major, minor);

    public override string ToString() => $"{Uuid} v{VersionMajor}.{VersionMinor}";
}

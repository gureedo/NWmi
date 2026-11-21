using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

internal static class RpcSyntaxIdNdrExtensions
{
    public static void Write(this NdrWriter w, RpcSyntaxId syntax)
    {
        w.WriteGuid(syntax.Uuid);
        w.WriteUInt32(syntax.VersionMajor | ((uint)syntax.VersionMinor << 16));
    }

    public static RpcSyntaxId ReadSyntaxId(this NdrReader r)
    {
        var uuid = r.ReadGuid();
        var version = r.ReadUInt32();
        return new RpcSyntaxId(uuid, (ushort)(version & 0xFFFF), (ushort)(version >> 16));
    }
}

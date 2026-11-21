using NWmi.Ndr;

namespace NWmi.Dcom;

/// <summary>A request to add or release references on one interface (REMINTERFACEREF, MS-DCOM 2.2.23).</summary>
/// <param name="Ipid">The interface whose reference count changes.</param>
/// <param name="PublicRefs">Public references to add or release.</param>
/// <param name="PrivateRefs">Private references to add or release.</param>
internal readonly record struct RemInterfaceRef(Guid Ipid, uint PublicRefs, uint PrivateRefs)
{
    internal void WriteNdr(NdrWriter writer)
    {
        writer.WriteGuid(Ipid);
        writer.WriteUInt32(PublicRefs);
        writer.WriteUInt32(PrivateRefs);
    }
}

using NWmi.Ndr;

namespace NWmi.Dcom;

/// <summary>The answer to one interface in a RemQueryInterface call (REMQIRESULT, MS-DCOM 2.2.24).</summary>
/// <param name="HResult">0 on success, negative on failure.</param>
/// <param name="Std">The reference to the interface; meaningful only when <paramref name="HResult"/> is 0.</param>
internal readonly record struct RemQiResult(int HResult, StdObjRef Std)
{
    public bool Succeeded => HResult >= 0;

    internal static RemQiResult ReadNdr(NdrReader reader)
    {
        var hresult = reader.ReadInt32();
        return new RemQiResult(hresult, StdObjRef.ReadNdr(reader));
    }

    internal void WriteNdr(NdrWriter writer)
    {
        writer.WriteInt32(HResult);
        Std.WriteNdr(writer);
    }
}

namespace NWmi.Dcom;

/// <summary>OBJREF_STANDARD: the object's identity plus the bindings of its object resolver.</summary>
internal sealed class StandardObjRef(Guid iid, StdObjRef std, DualStringArray resolverAddress)
    : ObjRef(iid)
{
    public StdObjRef Std { get; } = std;

    public DualStringArray ResolverAddress { get; } = resolverAddress;

    protected override ObjRefType Type => ObjRefType.Standard;

    protected override void WriteBody(Stream stream)
    {
        WriteStd(stream, Std);
        stream.Write(ResolverAddress.ToPacked());
    }

    internal static StandardObjRef ReadBody(Guid iid, ReadOnlySpan<byte> body)
    {
        var std = ReadStd(body);
        return new StandardObjRef(iid, std, DualStringArray.ParsePacked(body[StdObjRef.Size..], out _));
    }
}

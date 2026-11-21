namespace NWmi.Dcom;

/// <summary>OBJREF_HANDLER: like the standard form, plus the class of a client-side handler.</summary>
internal sealed class HandlerObjRef(Guid iid, StdObjRef std, Guid handlerClsid, DualStringArray resolverAddress)
    : ObjRef(iid)
{
    public StdObjRef Std { get; } = std;

    public Guid HandlerClsid { get; } = handlerClsid;

    public DualStringArray ResolverAddress { get; } = resolverAddress;

    protected override ObjRefType Type => ObjRefType.Handler;

    protected override void WriteBody(Stream stream)
    {
        WriteStd(stream, Std);
        stream.Write(HandlerClsid.ToByteArray());
        stream.Write(ResolverAddress.ToPacked());
    }

    internal static HandlerObjRef ReadBody(Guid iid, ReadOnlySpan<byte> body)
    {
        var std = ReadStd(body);
        if (body.Length < StdObjRef.Size + 16)
            throw new FormatException("An OBJREF_HANDLER is too short to hold its class id.");

        var clsid = new Guid(body.Slice(StdObjRef.Size, 16));
        return new HandlerObjRef(iid, std, clsid, DualStringArray.ParsePacked(body[(StdObjRef.Size + 16)..], out _));
    }
}

using NWmi.Ndr;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// InstantiationInfoData (MS-DCOM 2.2.22.2.1).
/// Which class to activate and which interfaces the client wants back.
/// </summary>
internal sealed class InstantiationInfoData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001ab-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    /// <summary>CLSCTX_REMOTE_SERVER. The server ignores this field; it is sent for compatibility.</summary>
    public const uint ClassContextRemoteServer = 0x10;

    /// <summary>Upper bound on the number of requested interfaces (MAX_REQUESTED_INTERFACES, MS-DCOM 2.2.28.1).</summary>
    public const int MaxRequestedInterfaces = 0x8000;

    /// <summary>The class of the object to create.</summary>
    public Guid ClassId { get; }

    /// <summary>The interfaces the client asks for; the reply holds one result per entry, in this order.</summary>
    public IReadOnlyList<Guid> RequestedInterfaces { get; }

    /// <summary>ACTVFLAGS_* bits; 0 for none.</summary>
    public uint ActivationFlags { get; }

    /// <summary>The DCOM version the client announces.</summary>
    public Version ClientVersion { get; init; } = DcomConstants.ClientVersion;

    public InstantiationInfoData(Guid classId, IReadOnlyList<Guid> requestedInterfaces, uint activationFlags = 0)
    {
        ArgumentNullException.ThrowIfNull(requestedInterfaces);
        if (requestedInterfaces.Count is < 1 or > MaxRequestedInterfaces)
            throw new ArgumentOutOfRangeException(
                nameof(requestedInterfaces), requestedInterfaces.Count,
                $"Between 1 and {MaxRequestedInterfaces} interfaces can be requested.");

        ClassId = classId;
        RequestedInterfaces = requestedInterfaces;
        ActivationFlags = activationFlags;
    }

    /// <summary>
    /// The NDR encoding, without the type serialization framing. <c>thisSize</c> is the size the structure has
    /// once framed (headers and padding included); it does not change the length of the encoding.
    /// </summary>
    public byte[] Encode()
    {
        var length = EncodeCore(thisSize: 0).Length;
        var framedSize = TypeSerialization1.Wrap(new byte[length]).Length;
        return EncodeCore((uint)framedSize);
    }

    private byte[] EncodeCore(uint thisSize)
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteGuid(ClassId);
            w.WriteUInt32(ClassContextRemoteServer);
            w.WriteUInt32(ActivationFlags);
            w.WriteInt32(0); // fIsSurrogate: must be FALSE
            w.WriteUInt32((uint)RequestedInterfaces.Count);
            w.WriteUInt32(0); // instFlag
            w.WriteUniquePointer(true, pw =>
            {
                pw.WriteMaxCount(RequestedInterfaces.Count);
                foreach (var iid in RequestedInterfaces)
                    pw.WriteGuid(iid);
            });
            w.WriteUInt32(thisSize);
            w.WriteUInt16((ushort)ClientVersion.Major);
            w.WriteUInt16((ushort)ClientVersion.Minor);
        });

        return writer.ToArray();
    }

    public static InstantiationInfoData Decode(ReadOnlySpan<byte> data)
    {
        var reader = new NdrReader(data.ToArray());
        var classId = Guid.Empty;
        uint flags = 0;
        var version = new Version(0, 0);
        NdrDeferred<List<Guid>>? interfaces = null;
        reader.Struct(r =>
        {
            classId = r.ReadGuid();
            r.ReadUInt32(); // classCtx: ignored
            flags = r.ReadUInt32();
            r.ReadInt32(); // fIsSurrogate: ignored
            var count = r.ReadUInt32();
            if (count is < 1 or > MaxRequestedInterfaces)
                throw new NdrFormatException($"InstantiationInfoData requests {count} interfaces.");

            r.ReadUInt32(); // instFlag
            interfaces = r.ReadUniquePointer(pr => ReadInterfaceIds(pr, count));
            r.ReadUInt32(); // thisSize: ignored
            version = new Version(r.ReadUInt16(), r.ReadUInt16());
            return 0;
        });

        if (interfaces is null || interfaces.IsNull)
            throw new NdrFormatException("InstantiationInfoData has no interface list.");

        return new InstantiationInfoData(classId, interfaces.Value, flags) { ClientVersion = version };
    }

    private static List<Guid> ReadInterfaceIds(NdrReader reader, uint count)
    {
        var actual = reader.ReadMaxCount();
        if (actual != count)
            throw new NdrFormatException($"The interface list has {actual} entries but cIID says {count}.");

        reader.EnsureCount(count, 16);
        var ids = new List<Guid>((int)count);
        for (var i = 0; i < count; i++)
            ids.Add(reader.ReadGuid());

        return ids;
    }
}

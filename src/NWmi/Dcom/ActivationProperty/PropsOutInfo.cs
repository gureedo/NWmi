using NWmi.Ndr;
using NWmi.Ndr.Ole;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>The outcome for one interface the client asked for.</summary>
/// <param name="Iid">The interface.</param>
/// <param name="HResult">0 or positive on success, negative on failure.</param>
/// <param name="ObjRef">The marshaled interface pointer when the call succeeded, otherwise null.</param>
internal sealed record InterfaceResult(Guid Iid, int HResult, byte[]? ObjRef)
{
    public bool Succeeded => HResult >= 0;
}

/// <summary>
/// PropsOutInfo (MS-DCOM 2.2.22.2.9). The interfaces the new object implements, with a status for each one requested.
/// </summary>
internal sealed class PropsOutInfo : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("00000339-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    public PropsOutInfo(IReadOnlyList<InterfaceResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count is < 1 or > InstantiationInfoData.MaxRequestedInterfaces)
            throw new ArgumentOutOfRangeException(nameof(results), results.Count,
                "There must be between 1 and 0x8000 results.");

        Results = results;
    }

    /// <summary>One entry per requested interface, in the order they were requested.</summary>
    public IReadOnlyList<InterfaceResult> Results { get; }

    /// <summary>The NDR encoding without framing. Servers produce this; it is here for tests and fake servers.</summary>
    public byte[] Encode()
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUInt32((uint)Results.Count);
            w.WriteUniquePointer(true, pw =>
            {
                pw.WriteMaxCount(Results.Count);
                foreach (var result in Results)
                    pw.WriteGuid(result.Iid);
            });
            w.WriteUniquePointer(true, pw =>
            {
                pw.WriteMaxCount(Results.Count);
                foreach (var result in Results)
                    pw.WriteInt32(result.HResult);
            });
            w.WriteUniquePointer(true, pw =>
            {
                pw.WriteMaxCount(Results.Count);
                foreach (var result in Results)
                    pw.WriteMInterfacePointer(result.ObjRef);
            });
        });

        return writer.ToArray();
    }

    internal static PropsOutInfo Decode(ReadOnlySpan<byte> data)
    {
        var reader = new NdrReader(data.ToArray());
        NdrDeferred<List<Guid>>? iids = null;
        NdrDeferred<List<int>>? statuses = null;
        NdrDeferred<List<NdrDeferred<byte[]?>>>? objects = null;
        uint count = 0;
        reader.Struct(r =>
        {
            count = r.ReadUInt32();
            if (count is < 1 or > InstantiationInfoData.MaxRequestedInterfaces)
                throw new NdrFormatException($"PropsOutInfo announces {count} interfaces.");

            var n = count;
            iids = r.ReadUniquePointer(pr => ReadArray(pr, n, 16, x => x.ReadGuid()));
            statuses = r.ReadUniquePointer(pr => ReadArray(pr, n, 4, x => x.ReadInt32()));
            objects = r.ReadUniquePointer(pr => ReadArray(pr, n, 4, x => x.ReadMInterfacePointer()));
            return 0;
        });

        if (iids is null || statuses is null || objects is null || iids.IsNull || statuses.IsNull || objects.IsNull)
            throw new NdrFormatException("PropsOutInfo is missing one of its arrays.");

        var results = new List<InterfaceResult>((int)count);
        for (var i = 0; i < count; i++)
        {
            var objRef = objects.Value[i].Value;
            if (statuses.Value[i] >= 0 && objRef is null)
                throw new NdrFormatException(
                    $"Interface {iids.Value[i]} succeeded but no object reference was returned.");

            results.Add(new InterfaceResult(iids.Value[i], statuses.Value[i], objRef));
        }

        return new PropsOutInfo(results);
    }

    private static List<T> ReadArray<T>(NdrReader reader, uint count, int minElementSize,
        Func<NdrReader, T> readElement)
    {
        var actual = reader.ReadMaxCount();
        if (actual != count)
            throw new NdrFormatException($"An array has {actual} entries but cIfs says {count}.");

        reader.EnsureCount(count, minElementSize);
        var list = new List<T>((int)count);
        for (var i = 0; i < count; i++)
            list.Add(readElement(reader));

        return list;
    }
}

using System.Buffers.Binary;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>What activation returned: where the object lives and the interface pointers obtained from it.</summary>
internal sealed class ActivationResult
{
    public ActivationResult(ScmReplyInfoData reply, PropsOutInfo interfaces)
    {
        Reply = reply;
        Interfaces = interfaces;
    }

    /// <summary>The object exporter: OXID, bindings, and the IPID of its IRemUnknown.</summary>
    public ScmReplyInfoData Reply { get; }

    /// <summary>One result per interface requested, in request order.</summary>
    public PropsOutInfo Interfaces { get; }

    /// <summary>The standard object reference for the interface at <paramref name="index"/>.</summary>
    /// <exception cref="DcomException">The interface was not returned, or its reference is not a standard one.</exception>
    public StandardObjRef GetStandardObjRef(int index)
    {
        var result = Interfaces.Results[index];
        if (!result.Succeeded || result.ObjRef is null)
            throw new DcomException(result.HResult, $"Interface {result.Iid} was not returned");

        try
        {
            return ObjRef.Parse(result.ObjRef) as StandardObjRef
                   ?? throw new DcomException($"Interface {result.Iid} came back as a non-standard object reference.");
        }
        catch (FormatException ex)
        {
            throw new DcomException($"The object reference for {result.Iid} is malformed: {ex.Message}");
        }
    }
}

/// <summary>
/// IRemoteSCMActivator (MS-DCOM 3.1.2.5.2.3): asks the remote machine's activation service to create an object and
/// hand back interface pointers to it. This is an ORPC interface, so each call starts with ORPCTHIS.
/// </summary>
internal sealed class RemoteScmActivator
{
    private const ushort OpRemoteCreateInstance = 4;

    private readonly RpcClient _client;
    private readonly RpcPresentationContext _context;

    /// <param name="client">A connection bound to <see cref="DcomIds.RemoteScmActivator"/>.</param>
    /// <param name="context">The presentation context that bind returned for it.</param>
    public RemoteScmActivator(RpcClient client, RpcPresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(context);
        _client = client;
        _context = context;
    }

    /// <summary>Creates an object of class <paramref name="classId"/> and returns the requested interfaces.</summary>
    public async Task<ActivationResult> CreateInstanceAsync(
        Guid classId,
        IReadOnlyList<Guid> interfaces,
        CancellationToken cancellationToken = default)
    {
        var request =
            BuildRemoteCreateInstanceRequest(Guid.NewGuid(), classId, interfaces, [DualStringArray.TcpTowerId]);
        var reply = await _client.CallAsync(_context, OpRemoteCreateInstance, request, null, cancellationToken)
            .ConfigureAwait(false);
        return ParseRemoteCreateInstanceReply(reply);
    }

    /// <summary>ORPCTHIS, a null pUnkOuter, and the activation properties as a marshaled interface pointer.</summary>
    internal static byte[] BuildRemoteCreateInstanceRequest(
        Guid causalityId, Guid classId, IReadOnlyList<Guid> interfaces, IReadOnlyList<ushort> protocolSequences)
    {
        var blob = ActivationPropertiesBlob.Build(
        [
            new SpecialPropertiesData(),
            new InstantiationInfoData(classId, interfaces),
            new ActivationContextInfoData(),
            new SecurityInfoData(),
            new LocationInfoData(),
            new ScmRequestInfoData(protocolSequences),
        ]);

        var objRef = new CustomObjRef(DcomIds.IActivationPropertiesIn, DcomIds.ActivationPropertiesIn, blob,
            reservedIncludesOwnHeader: true).ToBytes();

        var writer = new NdrWriter();
        OrpcHeader.WriteThis(writer, causalityId);
        writer.WriteUniquePointer(false, null); // pUnkOuter
        writer.WriteMInterfacePointer(objRef); // pActProperties
        return writer.ToArray();
    }

    /// <summary>ORPCTHAT, a pointer to the returned activation properties, and the HRESULT.</summary>
    internal static ActivationResult ParseRemoteCreateInstanceReply(byte[] stub)
    {
        if (stub.Length < 4)
            throw new NdrFormatException("The reply is too short to hold a status.");

        // The status is checked first: on failure the other results are empty.
        var status = BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(stub.Length - 4));
        if (status != 0)
            throw new DcomException(unchecked((int)status), "Activation failed");

        var reader = new NdrReader(stub);
        OrpcHeader.ReadThat(reader);
        var pointer = reader.ReadMInterfacePointer();
        if (pointer.IsNull || pointer.Value is null)
            throw new NdrFormatException("The activation reply holds no properties.");

        try
        {
            return ReadProperties(pointer.Value);
        }
        catch (FormatException ex)
        {
            throw new NdrFormatException($"The activation properties are malformed: {ex.Message}", ex);
        }
    }

    private static ActivationResult ReadProperties(byte[] objRefBytes)
    {
        if (ObjRef.Parse(objRefBytes) is not CustomObjRef custom || custom.Clsid != DcomIds.ActivationPropertiesOut)
            throw new FormatException("The activation reply is not an ActivationPropertiesOut object reference.");

        var properties = ActivationPropertiesBlob.Parse(custom.ObjectData);
        var reply = properties.OfType<ScmReplyInfoData>().FirstOrDefault()
                    ?? throw new FormatException("The activation reply lacks ScmReplyInfoData.");
        var propsOut = properties.OfType<PropsOutInfo>().FirstOrDefault()
                       ?? throw new FormatException("The activation reply lacks PropsOutInfo.");

        return new ActivationResult(reply, propsOut);
    }
}

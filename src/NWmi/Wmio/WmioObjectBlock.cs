namespace NWmi.Wmio;

/// <summary>Where a CIM object originated (MS-WMIO 2.2.7): the server and CIM namespace, both optional but never omitted when present.</summary>
internal sealed record WmioDecoration(string Server, string Namespace);

/// <summary>
/// One decoded CIM object: either a class or an instance (MS-WMIO 2.2.5, ObjectBlock). This is what a
/// CIM-TYPE-OBJECT value holds, and, wrapped in an EncodingUnit, what a WMI wire message carries
/// (MS-WMI 2.2.4, 2.2.14).
/// </summary>
internal sealed class WmioObjectBlock
{
    private WmioObjectBlock(WmioDecoration? decoration, WmioClass? @class, WmioInstance? instance, bool isPrototypeResult, bool hasIncompleteKeys)
    {
        Decoration = decoration;
        Class = @class;
        Instance = instance;
        IsPrototypeResult = isPrototypeResult;
        HasIncompleteKeys = hasIncompleteKeys;
    }

    public WmioDecoration? Decoration { get; }

    /// <summary>Set when this is a CIM class definition; <see cref="Instance"/> is then null.</summary>
    public WmioClass? Class { get; }

    /// <summary>Set when this is a CIM instance; <see cref="Class"/> is then null (the instance's own <c>Class</c> property holds its class).</summary>
    public WmioInstance? Instance { get; }

    public bool IsClass => Class is not null;

    /// <summary>Prototype Result Object flag (MS-WMI 2.2.4.1); only meaningful alongside <see cref="IsClass"/>.</summary>
    public bool IsPrototypeResult { get; }

    /// <summary>Set when one or more key properties are missing from a Prototype Result Object.</summary>
    public bool HasIncompleteKeys { get; }

    public static WmioObjectBlock ForClass(WmioClass @class, WmioDecoration? decoration = null) =>
        new(decoration, @class, null, false, false);

    public static WmioObjectBlock ForInstance(WmioInstance instance, WmioDecoration? decoration = null) =>
        new(decoration, null, instance, false, false);

    /// <summary>Decodes ObjectFlags [Decoration] Encoding (MS-WMIO 2.2.5).</summary>
    internal static WmioObjectBlock Decode(WmioReader reader)
    {
        var flags = (WmioObjectFlags)reader.ReadByte();
        var isClass = flags.HasFlag(WmioObjectFlags.Class);
        var isInstance = flags.HasFlag(WmioObjectFlags.Instance);
        if (isClass == isInstance)
            throw new WmioFormatException($"ObjectFlags 0x{(byte)flags:X2} must set exactly one of the class or instance bits.");

        WmioDecoration? decoration = null;
        if (flags.HasFlag(WmioObjectFlags.Decorated))
        {
            var server = reader.ReadEncodedString();
            var ns = reader.ReadEncodedString();
            decoration = new WmioDecoration(server, ns);
        }

        if (isClass)
        {
            var parent = WmioClass.Read(reader, hasMethodsPart: true); // ParentClass: not exposed (see remarks), but it holds inherited defaults
            var current = WmioClass.Read(reader, hasMethodsPart: true, parent);
            return new WmioObjectBlock(decoration, current, null, flags.HasFlag(WmioObjectFlags.PrototypeResult), flags.HasFlag(WmioObjectFlags.IncompleteKeys));
        }

        var instance = WmioInstance.Read(reader);
        return new WmioObjectBlock(decoration, null, instance, false, false);
    }

    /// <summary>
    /// A class with no name, properties, qualifiers or methods: the degenerate ParentClass a top-level class
    /// with no real superclass still needs (for example a method's <c>__PARAMETERS</c> class). The name is
    /// <see langword="null"/>, not <see cref="string.Empty"/>: the server's own placeholder has a null ClassNameRef (0xFFFFFFFF), but
    /// encoding <c>""</c> instead produces a dictionary-string reference (MS-WMIO's built-in string table
    /// happens to include the empty string), which the server's ExecMethod rejected with a generic E_FAIL
    /// RPC fault for every call, having accepted every other part of the request unchanged.
    /// </summary>
    private static readonly WmioClass NoParentClass = new(null, [], [], [], []);

    /// <summary>
    /// Encodes an instance's or a class's ObjectBlock. A class is used for a method's input parameters
    /// (MS-WMIO 2.3.3's <c>__PARAMETERS</c>, whose property values are the class's own property defaults, not
    /// a separate instance): Windows sends ExecMethod's <c>pInParams</c> this
    /// way, not as an instance of <c>__PARAMETERS</c>.
    /// </summary>
    internal void Encode(WmioWriter writer)
    {
        var flags = IsClass ? WmioObjectFlags.Class : WmioObjectFlags.Instance;
        if (Decoration is not null)
            flags |= WmioObjectFlags.Decorated;

        writer.WriteByte((byte)flags);
        if (Decoration is { } decoration)
        {
            writer.WriteEncodedString(decoration.Server);
            writer.WriteEncodedString(decoration.Namespace);
        }

        if (IsClass)
        {
            NoParentClass.Write(writer, hasMethodsPart: true);
            Class!.Write(writer, hasMethodsPart: true);
        }
        else
            Instance!.Write(writer);
    }

    /// <summary>Decodes an EncodingUnit (MS-WMIO 2.2.1): the leading Signature, ObjectEncodingLength, then the ObjectBlock.</summary>
    public static WmioObjectBlock DecodeEncodingUnit(ReadOnlyMemory<byte> data)
    {
        var reader = new WmioReader(data);
        reader.Expect(0x12345678, "EncodingUnit Signature");
        var declaredLength = reader.ReadUInt32();

        // ObjectEncodingLength is sometimes larger than what is actually transmitted (the specification's own
        // worked example says as much: "this value is larger than the actual required number of octets"), so
        // it is treated as an upper bound hint rather than an exact, enforceable size. Every structure nested
        // inside the ObjectBlock carries its own length and is self-delimiting, so decoding does not actually
        // depend on this field; only never reading past what is truly available matters here.
        var available = Math.Min((int)declaredLength, reader.Remaining);
        return Decode(new WmioReader(reader.ReadBytes(available)));
    }

    /// <summary>Encodes an EncodingUnit: Signature, the ObjectBlock's length, then the ObjectBlock itself.</summary>
    public byte[] EncodeEncodingUnit()
    {
        var body = new WmioWriter();
        Encode(body);

        var writer = new WmioWriter();
        writer.WriteUInt32(0x12345678);
        writer.WriteUInt32((uint)body.Position);
        writer.WriteBytes(body.WrittenSpan);
        return writer.ToArray();
    }
}

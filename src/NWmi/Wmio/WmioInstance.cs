namespace NWmi.Wmio;

/// <summary>
/// A decoded CIM instance (MS-WMIO 2.2.53, InstanceType): the class it belongs to (fully merged, own and
/// inherited properties together) and the resolved value of each property. A value is resolved from the
/// instance's own data when present, or falls back to the class's default when the instance marks the
/// property as using it (MS-WMIO 2.2.27); either way <see cref="Values"/> holds the final answer per property.
/// </summary>
internal sealed class WmioInstance
{
    /// <summary>The class this instance belongs to, as embedded in the instance's own encoding.</summary>
    public WmioClass Class { get; }

    /// <summary>Every property of <see cref="Class"/>, by name, with its resolved value (null for a NULL property).</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Instance-level qualifiers (MS-WMIO 2.2.57); usually empty.</summary>
    public IReadOnlyList<WmioQualifier> Qualifiers { get; }

    /// <summary>
    /// Per-property qualifiers, present only when at least one property has an instance-level qualifier
    /// (MS-WMIO 2.2.65); null when none do, which is the common case.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<WmioQualifier>>? PropertyQualifiers { get; }

    /// <summary>
    /// Builds a new instance of <paramref name="class"/>. Every property not present in
    /// <paramref name="propertyValues"/> takes the class's own default (<see cref="WmioProperty.DefaultValue"/>,
    /// which is null when the class has none); the built instance always carries a real, local value or an
    /// explicit null for every property, never an "inherited" marker (that distinction only matters when
    /// re-deriving a class from its parent, which this encoder does not do).
    /// <paramref name="qualifiers"/> and <paramref name="propertyQualifiers"/> (by property name) are the instance-level
    /// qualifier sets (MS-WMIO 2.2.57, 2.2.65); the latter is written only when some property has one.
    /// </summary>
    public static WmioInstance Create(
        WmioClass @class,
        IReadOnlyDictionary<string, object?>? propertyValues = null,
        IReadOnlyList<WmioQualifier>? qualifiers = null,
        IReadOnlyDictionary<string, IReadOnlyList<WmioQualifier>>? propertyQualifiers = null)
    {
        ArgumentNullException.ThrowIfNull(@class);
        var given = @class.ByPropertyName(propertyValues ?? new Dictionary<string, object?>(), nameof(propertyValues));
        var values = @class.Properties.ToDictionary(
            p => p.Name,
            p => given.TryGetValue(p.Name, out var explicitValue) ? explicitValue : p.DefaultValue);
        var qualified = propertyQualifiers is null
            ? null
            : @class.ByPropertyName(propertyQualifiers, nameof(propertyQualifiers));

        return new WmioInstance(@class, values, qualifiers ?? [],
            qualified is not null && qualified.Values.Any(set => set.Count > 0) ? qualified : null);
    }

    private WmioInstance(
        WmioClass @class,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<WmioQualifier> qualifiers,
        IReadOnlyDictionary<string, IReadOnlyList<WmioQualifier>>? propertyQualifiers)
    {
        Class = @class;
        Values = values;
        Qualifiers = qualifiers;
        PropertyQualifiers = propertyQualifiers;
    }

    /// <summary>
    /// Decodes an InstanceType (MS-WMIO 2.2.53): the embedded CurrentClass (merged, with no MethodsPart),
    /// then the instance's own flags, data and heap.
    /// </summary>
    internal static WmioInstance Read(WmioReader reader)
    {
        var @class = WmioClass.Read(reader, hasMethodsPart: false);

        var body = reader.ReadLengthPrefixedBlock();
        body.ReadUInt32(); // EncodingLength of this block itself
        body.ReadByte(); // InstanceFlags: reserved
        var classNameRef = body.ReadUInt32();

        var propertyCount = @class.Properties.Count;
        var ndBytes = propertyCount > 0 ? body.ReadBytes(NdTable.OctetCount(propertyCount)) : [];

        // InstanceData reuses the embedded class's own NdTableValueTableLength (MS-WMIO 2.2.29 remarks): the
        // property set and types are identical between the class and its instances, so the length need not be
        // repeated on the wire.
        var valueTableReader = new WmioReader(body.ReadBytes(@class.ValueTableLength));

        var instanceQualifierSet = body.ReadLengthPrefixedBlock();
        var propQualFlag = body.ReadByte();
        IReadOnlyDictionary<string, IReadOnlyList<WmioQualifier>>? propertyQualifiers = null;
        var propertyQualifierBlocks = propQualFlag switch
        {
            1 => null,
            2 => ReadPropertyQualifierBlocks(body, propertyCount),
            _ => throw new WmioFormatException($"InstPropQualSetFlag must be 1 or 2, not {propQualFlag}."),
        };

        var heap = WmioHeap.Read(body);

        var qualifiers = WmioQualifier.ReadSetFromBlock(instanceQualifierSet, heap);
        if (propertyQualifierBlocks is not null)
        {
            var map = new Dictionary<string, IReadOnlyList<WmioQualifier>>();
            for (var i = 0; i < propertyCount; i++)
                map[@class.Properties[i].Name] = WmioQualifier.ReadSetFromBlock(propertyQualifierBlocks[i], heap);

            propertyQualifiers = map;
        }

        var byDeclarationOrder =
            NdTable.IsDeclarationOrdered(@class.Properties.Select(p => (int)p.DeclarationOrder), propertyCount);
        var values = new Dictionary<string, object?>(propertyCount);
        for (var i = 0; i < propertyCount; i++)
        {
            var property = @class.Properties[i];
            var ndEntry = NdTable.ReadEntry(ndBytes, byDeclarationOrder ? property.DeclarationOrder : i, propertyCount,
                property.Name);
            try
            {
                values[property.Name] = HoldsEmbeddedObject(ndEntry, property, valueTableReader)
                    ? ReadInstanceValue(valueTableReader, property, heap)
                    : ndEntry.IsNull
                        ? null
                        : ndEntry.IsInherited
                            ? property.DefaultValue
                            : ReadInstanceValue(valueTableReader, property, heap);
            }
            catch (WmioFormatException ex)
            {
                throw new WmioFormatException(
                    $"Reading property '{property.Name}' (type 0x{(ushort)property.Type:X4}, offset {property.ValueTableOffset}): {ex.Message}",
                    ex);
            }
        }

        var className = heap.ResolveString(classNameRef);
        if (className is not null && @class.Name is not null &&
            !string.Equals(className, @class.Name, StringComparison.Ordinal))
            throw new WmioFormatException(
                $"The instance names class '{className}' but its embedded class is '{@class.Name}'.");

        return new WmioInstance(@class, values, qualifiers, propertyQualifiers);
    }

    /// <summary>
    /// Encodes this instance: a fresh encoding of <see cref="Class"/> (ClassPart only, no MethodsPart, per
    /// MS-WMIO 2.2.14), then this instance's own flags, data and heap.
    /// </summary>
    internal void Write(WmioWriter writer)
    {
        if (Class.RawClassPart is { } rawClass)
            writer.WriteBytes(rawClass);
        else
            Class.Write(writer, hasMethodsPart: false);

        writer.WriteLengthPrefixedBlock(body =>
        {
            body.WriteByte(0); // InstanceFlags: reserved
            var instanceHeap = new WmioHeapBuilder();
            body.WriteUInt32(instanceHeap.AddStringRef(Class.Name));

            // A decoded class keeps its original ValueTable layout (declaration order, not the sorted order of
            // Properties), so each value goes at its property's own offset; a class built in code is laid out sequentially.
            var positional = Class.RawClassPart is not null;
            var valueTable = new WmioWriter();
            var placed = positional ? new byte[Class.ValueTableLength] : [];
            foreach (var property in Class.Properties)
            {
                var value = Values.GetValueOrDefault(property.Name);
                var target = positional ? new WmioWriter() : valueTable;
                if (value is null)
                    WmioValue.WriteNoValue(target, property.Type);
                else
                    WmioValue.WriteInline(target, property.Type, value, instanceHeap);

                if (positional)
                    target.WrittenSpan.CopyTo(placed.AsSpan((int)property.ValueTableOffset));
            }

            if (positional)
                valueTable.WriteBytes(placed);

            var ndEntries = NdTable.InDeclarationOrder(
                Class.Properties,
                p => new NdTable.Entry(IsNull: Values.GetValueOrDefault(p.Name) is null, IsInherited: false));
            body.WriteBytes(NdTable.Write(ndEntries));
            body.WriteBytes(valueTable.WrittenSpan);

            WmioQualifier.WriteSet(body, Qualifiers, instanceHeap);
            if (PropertyQualifiers is null)
                body.WriteByte(1);
            else
            {
                body.WriteByte(2);
                foreach (var property in Class.Properties)
                    WmioQualifier.WriteSet(body, PropertyQualifiers.GetValueOrDefault(property.Name, []), instanceHeap);
            }

            instanceHeap.WriteTo(body);
        });
    }

    /// <summary>
    /// A server marks an event's embedded object (e.g. <c>__InstanceCreationEvent.TargetInstance</c>)
    /// "NULL and inherited" in the instance's NdTable even though its ValueTable slot holds a real reference
    /// to the object in the heap (MS-WMIO 2.2.27 says the slot is then ignored). An embedded-object
    /// property whose slot is not the null reference is therefore read regardless of its NdTable bits.
    /// </summary>
    private static bool HoldsEmbeddedObject(NdTable.Entry nd, WmioProperty property, WmioReader valueTableReader)
    {
        if (!nd.IsNull || property.Type != CimType.Object)
            return false;

        valueTableReader.Position = (int)property.ValueTableOffset;
        var slot = valueTableReader.ReadUInt32();
        return !WmioHeap.IsNull(slot);
    }

    private static object? ReadInstanceValue(WmioReader valueTableReader, WmioProperty property, WmioHeap heap)
    {
        valueTableReader.Position = (int)property.ValueTableOffset;
        return WmioValue.ReadInline(valueTableReader, property.Type, heap);
    }

    private static List<WmioReader> ReadPropertyQualifierBlocks(WmioReader reader, int propertyCount)
    {
        var blocks = new List<WmioReader>(propertyCount);
        for (var i = 0; i < propertyCount; i++)
            blocks.Add(reader.ReadLengthPrefixedBlock());

        return blocks;
    }
}

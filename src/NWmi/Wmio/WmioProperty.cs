namespace NWmi.Wmio;

/// <summary>
/// NullAndDefaultFlag (MS-WMIO 2.2.27): how a property's default behaves. <see cref="IsNull"/> set means the
/// default is NULL; <see cref="IsInherited"/> set means it comes from a parent class rather than this one.
/// Either bit set means the corresponding ValueTable slot is not meaningful.
/// </summary>
internal readonly record struct NullAndDefaultFlag(bool IsNull, bool IsInherited)
{
    /// <summary>Whether the owning ValueTable slot holds a real, directly usable value.</summary>
    public bool HasLocalValue => !IsNull && !IsInherited;
}

/// <summary>
/// One CIM property of a class, combining PropertyInfo (MS-WMIO 2.2.30) with its name and resolved default
/// value. <see cref="DefaultValue"/> is populated whenever the default can be determined from the information
/// available where this property was decoded: directly when <see cref="NdFlag"/>.HasLocalValue, or, when the
/// flag says the default comes from a parent class, by whatever the caller supplies for resolving that case.
/// </summary>
internal sealed record WmioProperty(
    string Name,
    CimType Type,
    bool DefinedInParentClass,
    ushort DeclarationOrder,
    uint ClassOfOrigin,
    IReadOnlyList<WmioQualifier> Qualifiers,
    NullAndDefaultFlag NdFlag,
    object? DefaultValue,
    uint ValueTableOffset)
{
    /// <summary>PropertyType's Inherited bit (MS-WMIO 2.2.32), OR'd with the CimType to form the 32-bit PropertyType field.</summary>
    private const uint InheritedTypeBit = 0x4000;

    internal static WmioProperty Read(string name, byte[] ndTable, int propertyCount, int listIndex,
        bool byDeclarationOrder, WmioReader valueTableReader, WmioHeap classHeap, uint propertyInfoRef,
        Func<string, object?>? resolveInheritedDefault)
    {
        var info = classHeap.Resolve(propertyInfoRef);
        var propertyTypeRaw = info.ReadUInt32();
        var type = (CimType)(propertyTypeRaw & ~InheritedTypeBit);
        if (!type.BaseType().IsSupportedBaseType())
            // Validated here, at the one place a property's CimType is ever read from untrusted data, so every
            // other piece of code that later calls InlineValueSize() on a decoded WmioProperty.Type can assume
            // it is always one of the recognized CIM base types.
            throw new WmioFormatException(
                $"Property '{name}' has an unsupported CIM type 0x{propertyTypeRaw & 0xFFFF:X4}.");

        var definedInParentClass = (propertyTypeRaw & InheritedTypeBit) != 0;
        var declarationOrder = info.ReadUInt16();
        var valueTableOffset = info.ReadUInt32();
        var classOfOrigin = info.ReadUInt32();
        var qualifiers = WmioQualifier.ReadSet(info, classHeap);

        // The NdTable is indexed by the property's DeclarationOrder, not by its place in the (name-sorted)
        // PropertyLookupTable: MS-WMIO 2.2.26 says only "ordered similar to" the lookup table, but its own example calls the
        // DeclarationOrder "the value used in NdTable", and a real client's Win32_ProcessStartup instance (PriorityClass
        // declared 1st, ShowWindow 12th, alphabetically 5th and 6th) marks exactly those two bit pairs.
        var ndEntry = NdTable.ReadEntry(ndTable, byDeclarationOrder ? declarationOrder : listIndex, propertyCount,
            name);

        var ndFlag = new NullAndDefaultFlag(ndEntry.IsNull, ndEntry.IsInherited);
        object? defaultValue = null;
        if (ndFlag.HasLocalValue)
        {
            valueTableReader.Position = (int)valueTableOffset;
            defaultValue = WmioValue.ReadInline(valueTableReader, type, classHeap);
        }
        else if (!ndFlag.IsNull)
            defaultValue = resolveInheritedDefault is not null
                ? resolveInheritedDefault(name)
                : ReadCopiedDefault(valueTableReader, valueTableOffset, type, classHeap);

        return new WmioProperty(name, type, definedInParentClass, declarationOrder, classOfOrigin, qualifiers, ndFlag,
            defaultValue, valueTableOffset);
    }

    /// <summary>
    /// The inherited default as Windows copies it into the derived class's own slot and heap, which is all an instance has, since it
    /// carries no parent class (seen on a real instance; MS-WMIO does not promise it). Null if the slot holds no readable value.
    /// </summary>
    private static object? ReadCopiedDefault(WmioReader valueTable, uint offset, CimType type, WmioHeap classHeap)
    {
        try
        {
            valueTable.Position = (int)offset;
            return WmioValue.ReadInline(valueTable, type, classHeap);
        }
        catch (WmioFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes this property's PropertyInfo (into the class heap) and its ValueTable slot (local value, or
    /// NoValue filler when the default is null or inherited).
    /// </summary>
    internal (uint NameRef, uint InfoRef) Write(WmioHeapBuilder classHeap, WmioWriter valueTable)
    {
        var nameRef = classHeap.AddStringRef(Name);
        var infoRef = classHeap.AddItem(info =>
        {
            var propertyTypeRaw = (uint)Type | (DefinedInParentClass ? InheritedTypeBit : 0);
            info.WriteUInt32(propertyTypeRaw);
            info.WriteUInt16(DeclarationOrder);
            info.WriteUInt32((uint)valueTable.Position);
            info.WriteUInt32(ClassOfOrigin);
            WmioQualifier.WriteSet(info, Qualifiers, classHeap);
        });

        if (NdFlag.HasLocalValue)
            WmioValue.WriteInline(valueTable, Type, DefaultValue, classHeap);
        else
            WmioValue.WriteNoValue(valueTable, Type);

        return (nameRef, infoRef);
    }
}

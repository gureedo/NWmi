namespace NWmi.Wmio;

/// <summary>
/// One method of a class (MS-WMIO 2.2.41). Input and output signatures are kept as raw ObjectBlock bytes; each decodes to a
/// <c>__PARAMETERS</c> class.
/// </summary>
internal sealed record WmioMethod(
    string Name,
    bool DefinedInParentClass,
    uint MethodOrigin,
    byte[] InputSignatureRaw,
    byte[] OutputSignatureRaw)
{
    /// <summary>The method's own qualifiers (<c>Static</c>, <c>Implemented</c>, ...).</summary>
    public IReadOnlyList<WmioQualifier> Qualifiers { get; init; } = [];

    /// <summary>The input parameters as a <c>__PARAMETERS</c> class, or null when the method takes none.</summary>
    public WmioClass? DecodeInputSignature() => DecodeSignature(InputSignatureRaw);

    /// <summary>The output parameters and <c>ReturnValue</c> as a <c>__PARAMETERS</c> class, or null when there are none.</summary>
    public WmioClass? DecodeOutputSignature() => DecodeSignature(OutputSignatureRaw);

    private static WmioClass? DecodeSignature(byte[] raw) =>
        raw.Length == 0 ? null : WmioObjectBlock.Decode(new WmioReader(raw)).Class;
}

/// <summary>
/// A decoded CIM class (MS-WMIO 2.2.14, ClassAndMethodsPart): its name, superclass chain, qualifiers,
/// properties (own and inherited, merged, in the sorted order they were encoded in), and methods.
/// </summary>
internal sealed record WmioClass(
    string? Name,
    IReadOnlyList<string> DerivationList,
    IReadOnlyList<WmioQualifier> Qualifiers,
    IReadOnlyList<WmioProperty> Properties,
    IReadOnlyList<WmioMethod> Methods)
{
    /// <summary>The immediate superclass name, or null for a class with none.</summary>
    public string? SuperclassName => DerivationList.Count > 0 ? DerivationList[0] : null;

    /// <summary>
    /// The ClassPart's own ValueTable length in octets (MS-WMIO 2.2.28's NdTableValueTableLength, minus the
    /// NdTable). An instance sharing this class reuses this exact value for its own ValueTable (MS-WMIO
    /// 2.2.29's remarks), rather than one recomputed from <see cref="Properties"/>' own
    /// <see cref="WmioProperty.ValueTableOffset"/>s: those can legitimately leave gaps (e.g. padding) that a
    /// "largest offset plus that property's own size" reconstruction underestimates, which then
    /// misaligns every field an instance decodes after its ValueTable (as seen with
    /// <c>Win32_NTLogEvent</c> instances). Zero for a class built directly (not decoded), which is harmless:
    /// such a class is never used to decode a real instance's ValueTable.
    /// </summary>
    internal int ValueTableLength { get; init; }

    /// <summary>
    /// The ClassPart exactly as it was decoded (null for a class built in code). An instance of a decoded
    /// class is encoded with these original bytes rather than a re-encoding of <see cref="Properties"/>: a
    /// server's PutInstance rejects (WBEM_E_INVALID_CLASS) an embedded class that differs from its own
    /// in layout, even by heap order, padding bits or size, though MS-WMIO calls all of those free.
    /// </summary>
    internal byte[]? RawClassPart { get; init; }


    /// <summary>The property called <paramref name="name"/>, or null. CIM names are case-insensitive; an exact spelling wins over a different-case one.</summary>
    public WmioProperty? FindProperty(string name) =>
        Properties.FirstOrDefault(p => p.Name == name)
        ?? Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Re-keys <paramref name="items"/> (whose keys name this class's properties in any case) by each property's own spelling.
    /// </summary>
    /// <exception cref="ArgumentException">A key names no property, or two keys name the same one.</exception>
    internal Dictionary<string, T> ByPropertyName<T>(IEnumerable<KeyValuePair<string, T>> items, string parameterName)
    {
        var unknown = new List<string>();
        var result = new Dictionary<string, T>();
        foreach (var (name, value) in items)
        {
            var property = FindProperty(name);
            if (property is null)
                unknown.Add(name);
            else if (!result.TryAdd(property.Name, value))
                throw new ArgumentException(
                    $"'{Name}' property '{property.Name}' is given more than once (names match case-insensitively).",
                    parameterName);
        }

        return unknown.Count > 0
            ? throw new ArgumentException($"'{Name}' has no property named: {string.Join(", ", unknown)}.",
                parameterName)
            : result;
    }

    /// <summary>
    /// Decodes a ClassAndMethodsPart (MS-WMIO 2.2.14): ClassPart, then a MethodsPart unless
    /// <paramref name="hasMethodsPart"/> is false (as when this is an instance's embedded CurrentClass,
    /// where MS-WMIO 2.2.14 says MethodsPart MUST NOT be present). <paramref name="parent"/> supplies the defaults this
    /// class inherits; without it (an instance carries no parent class) they come from the class's own copy of them.
    /// </summary>
    internal static WmioClass Read(WmioReader reader, bool hasMethodsPart, WmioClass? parent = null)
    {
        var classPart = reader.ReadLengthPrefixedBlock();
        classPart.ReadUInt32(); // EncodingLength (of the whole ClassPart; already consumed above)
        classPart.ReadByte(); // ReservedOctet
        var classNameRef = classPart.ReadUInt32();
        var ndTableValueTableLength = (int)classPart.ReadUInt32();

        var derivationList = ReadDerivationList(classPart);

        // The qualifier set's VALUES may be heap references, so it cannot be decoded until the class heap
        // (further down in the ClassPart) has been read. Its byte range is captured now and decoded once the
        // heap exists; WmioReader is just a view, so re-reading this sub-reader later is safe.
        var qualifierBlock = classPart.ReadLengthPrefixedBlock();

        var propertyCountRaw = classPart.ReadUInt32();
        classPart.EnsureCount(propertyCountRaw,
            8); // each PropertyLookup entry is 8 bytes; also rejects a huge/negative count before it is ever used for an allocation size
        var propertyCount = (int)propertyCountRaw;
        var lookups = new (uint NameRef, uint InfoRef)[propertyCount];
        for (var i = 0; i < propertyCount; i++)
            lookups[i] = (classPart.ReadUInt32(), classPart.ReadUInt32());

        var ndBytes = propertyCount > 0 ? classPart.ReadBytes(NdTable.OctetCount(propertyCount)) : [];
        var valueTableLength = ndTableValueTableLength - ndBytes.Length;
        if (valueTableLength < 0)
            throw new WmioFormatException(
                $"NdTableValueTableLength ({ndTableValueTableLength}) is smaller than the NdTable alone ({ndBytes.Length} bytes).");

        var valueTableReader = new WmioReader(classPart.ReadBytes(valueTableLength));
        var heap = WmioHeap.Read(classPart);

        var qualifiers = WmioQualifier.ReadSetFromBlock(qualifierBlock, heap);
        var name = heap.ResolveString(classNameRef);

        var byDeclarationOrder = NdTable.IsDeclarationOrdered(
            lookups.Select(l => NdTable.PeekDeclarationOrder(heap, l.InfoRef)), propertyCount);
        var properties = new WmioProperty[propertyCount];
        for (var i = 0; i < propertyCount; i++)
        {
            var propertyName = heap.ResolveRequiredString(lookups[i].NameRef);
            properties[i] = WmioProperty.Read(
                propertyName, ndBytes, propertyCount, i, byDeclarationOrder, valueTableReader, heap, lookups[i].InfoRef,
                parent is null ? null : inherited => parent.FindProperty(inherited)?.DefaultValue);
        }

        var methods = hasMethodsPart ? ReadMethods(reader) : [];
        return new WmioClass(name, derivationList, qualifiers, properties, methods)
            { ValueTableLength = valueTableLength, RawClassPart = classPart.ToArray() };
    }

    /// <summary>DerivationList (MS-WMIO 2.2.17): a length-prefixed sequence of ClassNameEncoding entries.</summary>
    private static List<string> ReadDerivationList(WmioReader classPart)
    {
        var block = classPart.ReadLengthPrefixedBlock();
        block.ReadUInt32();
        var names = new List<string>();
        while (!block.IsAtEnd)
        {
            var name = block.ReadEncodedString();
            block.ReadUInt32(); // the ClassNameEncoding's own EncodingLength; redundant with the string's own terminator
            names.Add(name);
        }

        return names;
    }

    /// <summary>MethodsPart (MS-WMIO 2.2.38): fixed-size MethodDescription entries, then the MethodHeap they reference.</summary>
    private static List<WmioMethod> ReadMethods(WmioReader classAndMethodsReader)
    {
        var block = classAndMethodsReader.ReadLengthPrefixedBlock();
        block.ReadUInt32();
        var count = block.ReadUInt16();
        block.ReadUInt16(); // MethodCountPadding: ignored

        var descriptors =
            new (uint NameRef, byte Flags, uint Origin, uint QualifiersRef, uint InputRef, uint OutputRef)[count];
        for (var i = 0; i < count; i++)
        {
            var nameRef = block.ReadUInt32();
            var flags = block.ReadByte();
            block.ReadBytes(3); // MethodPadding: ignored
            var origin = block.ReadUInt32();
            var qualifiersRef = block.ReadUInt32();
            var inputRef = block.ReadUInt32();
            var outputRef = block.ReadUInt32();
            descriptors[i] = (nameRef, flags, origin, qualifiersRef, inputRef, outputRef);
        }

        var heap = WmioHeap.Read(block);
        var methods = new List<WmioMethod>(count);
        foreach (var d in descriptors)
            methods.Add(new WmioMethod(
                heap.ResolveRequiredString(d.NameRef),
                ((MethodFlags)d.Flags & MethodFlags.OriginPropagated) != 0,
                d.Origin,
                ReadSignatureBlock(heap, d.InputRef),
                ReadSignatureBlock(heap, d.OutputRef))
            {
                Qualifiers = WmioHeap.IsNull(d.QualifiersRef)
                    ? []
                    : WmioQualifier.ReadSet(heap.Resolve(d.QualifiersRef), heap),
            });

        return methods;
    }

    /// <summary>MethodSignatureBlock (MS-WMIO 2.2.70): its own EncodingLength, then the raw bytes of an ObjectBlock.</summary>
    private static byte[] ReadSignatureBlock(WmioHeap heap, uint heapRef)
    {
        if (WmioHeap.IsNull(heapRef))
            return [];

        // Despite its name, a MethodSignatureBlock's length field is NOT a self-inclusive EncodingLength
        // (MS-WMIO 2.2.73's "always at least 4, includes its own length" rule does not hold here): for
        // example Win32_Process's Create method declares exactly its ObjectBlock's own byte count, with no 4 bytes added for the length field itself;
        // treating it as self-inclusive (subtracting 4) silently truncated the last 4 bytes of every
        // non-empty signature, which only ever broke decoding deep inside the ClassAndMethodsPart's own
        // trailing MethodsPart (the truncation's position, not its cause, is what first surfaces as a
        // decode error). A server also points a parameterless method's in/out signature at a heap slot
        // whose length is a literal 0, not the null HeapRef sentinel.
        var signatureReader = heap.Resolve(heapRef);
        var length = signatureReader.ReadUInt32();
        if (length == 0)
            return [];

        if (length > int.MaxValue)
            throw new WmioFormatException($"A method signature's EncodingLength of {length} is too large.");

        return signatureReader.ReadBytes((int)length);
    }

    /// <summary>
    /// Encodes a ClassAndMethodsPart: this class's properties (in <see cref="Properties"/>'s order, which must
    /// already be the correct sorted PropertyLookupTable order), then a MethodsPart unless
    /// <paramref name="hasMethodsPart"/> is false.
    /// </summary>
    internal void Write(WmioWriter writer, bool hasMethodsPart)
    {
        writer.WriteLengthPrefixedBlock(classPart =>
        {
            classPart.WriteByte(0); // ReservedOctet
            var classHeap = new WmioHeapBuilder();
            var nameRef =
                classHeap.AddStringRef(Name); // the server expects the class name first in the heap (offset 0)
            var classNameRefPosition = classPart.Position;
            classPart.WriteUInt32(0); // ClassNameRef: patched once the heap assigns it, a few lines down
            var ndValueTableLengthPosition = classPart.ReserveUInt32();

            classPart.WriteLengthPrefixedBlock(derivation =>
            {
                foreach (var superclass in DerivationList)
                {
                    derivation.WriteEncodedString(superclass);

                    // ClassNameEncoding's trailing length (MS-WMIO 2.2.18) covers only the Encoded-String
                    // itself (flag, characters, terminator), not this 4-byte field; see the
                    // MyClass example, whose DerivationList is exactly 4 (self) + 6 (Encoded-String "Base") +
                    // 4 (this field, holding 6) = 14 bytes.
                    derivation.WriteUInt32((uint)WmioWriter.EncodedStringLength(superclass));
                }
            });

            WmioQualifier.WriteSet(classPart, Qualifiers, classHeap);

            classPart.WriteUInt32((uint)Properties.Count);
            var lookupPosition = classPart.Position;
            foreach (var _ in Properties)
            {
                classPart.WriteUInt32(0); // NameRef: patched below, once every property has been written
                classPart.WriteUInt32(0); // InfoRef: patched below
            }

            var valueTable = new WmioWriter();
            var refs = new (uint NameRef, uint InfoRef)[Properties.Count];
            for (var i = 0; i < Properties.Count; i++)
                refs[i] = Properties[i].Write(classHeap, valueTable);

            var ndEntries = NdTable.InDeclarationOrder(Properties,
                p => new NdTable.Entry(p.NdFlag.IsNull, p.NdFlag.IsInherited));

            var ndBytes = NdTable.Write(ndEntries);
            classPart.WriteBytes(ndBytes);
            classPart.WriteBytes(valueTable.WrittenSpan);
            classPart.PatchUInt32(ndValueTableLengthPosition, (uint)(ndBytes.Length + valueTable.Position));

            classPart.PatchUInt32(classNameRefPosition, nameRef);
            for (var i = 0; i < refs.Length; i++)
            {
                classPart.PatchUInt32(lookupPosition + (i * 8), refs[i].NameRef);
                classPart.PatchUInt32(lookupPosition + (i * 8) + 4, refs[i].InfoRef);
            }

            classHeap.WriteTo(classPart);
        });

        if (hasMethodsPart)
            WriteMethods(writer);
    }

    private void WriteMethods(WmioWriter writer)
    {
        writer.WriteLengthPrefixedBlock(block =>
        {
            block.WriteUInt16((ushort)Methods.Count);
            block.WriteUInt16(0); // MethodCountPadding

            var methodHeap = new WmioHeapBuilder();
            foreach (var method in Methods)
            {
                block.WriteUInt32(methodHeap.AddStringRef(method.Name));
                block.WriteByte((byte)(method.DefinedInParentClass ? MethodFlags.OriginPropagated : MethodFlags.None));
                block.WriteBytes(new byte[3]); // MethodPadding
                block.WriteUInt32(method.MethodOrigin);
                block.WriteUInt32(WmioHeap.NullRef); // HeapQualifierSetRef: a client never sends a method's qualifiers
                block.WriteUInt32(WriteSignatureBlock(methodHeap, method.InputSignatureRaw));
                block.WriteUInt32(WriteSignatureBlock(methodHeap, method.OutputSignatureRaw));
            }

            methodHeap.WriteTo(block);
        });
    }

    /// <summary>Writes a non-self-inclusive length (just <paramref name="raw"/>'s own byte count), matching the server's MethodSignatureBlock; see the remarks on <see cref="ReadSignatureBlock"/>.</summary>
    private static uint WriteSignatureBlock(WmioHeapBuilder heap, byte[] raw) =>
        raw.Length == 0
            ? WmioHeap.NullRef
            : heap.AddItem(w =>
            {
                w.WriteUInt32((uint)raw.Length);
                w.WriteBytes(raw);
            });
}

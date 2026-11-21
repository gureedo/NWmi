using System.Text;

namespace NWmi.Ndr.Ole;

internal static class OleNdrReaderExtensions
{
    extension(NdrReader reader)
    {
        /// <summary>A BSTR. Both a null pointer and a blob with cBytes = 0xFFFFFFFF give null.</summary>
        public NdrDeferred<string?> ReadBstr()
        {
            ArgumentNullException.ThrowIfNull(reader);
            return reader.ReadUniquePointer(ReadBstrBlob);
        }

        /// <summary>A <c>[unique] MInterfacePointer*</c>; the result is the raw OBJREF bytes.</summary>
        public NdrDeferred<byte[]?> ReadMInterfacePointer()
        {
            ArgumentNullException.ThrowIfNull(reader);
            return reader.ReadUniquePointer<byte[]?>(static r =>
            {
                var max = r.ReadMaxCount();
                var count = r.ReadUInt32();
                if (count != max)
                    throw new NdrFormatException(
                        $"MInterfacePointer size {count} does not match its conformance {max}.");

                return r.ReadBytes(max);
            });
        }

        /// <summary>
        /// Reads an <c>[out, in, unique] T** pp</c> "give me an interface back" parameter (see
        /// <see cref="OleNdrWriterExtensions.WriteOutInterfaceSlot"/>): an outer unique pointer wrapping the
        /// MInterfacePointer value. Null outer means the server set the whole parameter to null (e.g. on failure);
        /// otherwise the deferred value is the MInterfacePointer underneath, itself possibly null.
        /// </summary>
        /// <remarks>
        /// The outer referent is consumed directly with <see cref="NdrReader.ReadUInt32"/>, not
        /// <see cref="NdrReader.ReadUniquePointer{T}"/>, so the inner <see cref="ReadMInterfacePointer"/> call
        /// below runs at the same nesting depth a plain top-level field would, and so resolves the normal way
        /// (immediately at depth 0, or deferred with everything else in an enclosing structure) instead of being
        /// a pointee nested inside another pointee, which cannot resolve until the outer one already has.
        /// </remarks>
        public NdrDeferred<byte[]?> ReadOutInterfaceSlot()
        {
            ArgumentNullException.ThrowIfNull(reader);
            var outerReferent = reader.ReadUInt32();
            if (outerReferent != 0)
                return reader.ReadMInterfacePointer();

            var empty = new NdrDeferred<byte[]?>(0);
            empty.Resolve(null);
            return empty;
        }

        public NdrDeferred<OleVariant?> ReadVariant()
        {
            ArgumentNullException.ThrowIfNull(reader);
            return reader.ReadUniquePointer<OleVariant?>(ReadVariantBody);
        }

        public NdrDeferred<OleSafeArray?> ReadSafeArray()
        {
            ArgumentNullException.ThrowIfNull(reader);
            return reader.ReadUniquePointer<OleSafeArray?>(ReadSafeArrayBody);
        }
    }

    private static string? ReadBstrBlob(NdrReader r)
    {
        var max = r.ReadMaxCount();
        var byteCount = r.ReadUInt32();
        var size = r.ReadUInt32();
        if (size != max)
            throw new NdrFormatException($"BSTR clSize {size} does not match its conformance {max}.");

        if (byteCount == OleTypes.NullBstrByteCount)
        {
            if (size != 0)
                throw new NdrFormatException("A null BSTR must have clSize 0.");

            return null;
        }

        if (byteCount != size * 2UL)
            throw new NdrFormatException(
                $"BSTR byte count {byteCount} does not match {size} characters (binary BSTRs are not supported).");

        r.EnsureCount(size, 2);
        return Encoding.Unicode.GetString(r.ReadBytes(max * 2));
    }

    private static OleVariant ReadVariantBody(NdrReader r)
    {
        r.Align(8);
        r.ReadUInt32(); // clSize: informational
        r.ReadUInt32(); // rpcReserved
        var vt = (OleVarType)r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        var discriminant = r.ReadUInt32();

        if ((vt & OleVarType.ByRef) != 0)
            throw new NdrFormatException("VT_BYREF VARIANTs are not supported.");

        var isArray = (vt & OleVarType.Array) != 0;
        var expected = isArray ? (uint)OleVarType.Array : (uint)vt;
        if (discriminant != expected && discriminant != (uint)vt)
            throw new NdrFormatException(
                $"VARIANT discriminant 0x{discriminant:X} does not match vt 0x{(ushort)vt:X}.");

        var variant = OleVariant.ForRead(vt);
        switch (vt)
        {
            case OleVarType.Empty or OleVarType.Null:
                break;
            case OleVarType.I1:
                variant.Value = unchecked((sbyte)r.ReadByte());
                break;
            case OleVarType.UI1:
                variant.Value = r.ReadByte();
                break;
            case OleVarType.I2:
                variant.Value = r.ReadInt16();
                break;
            case OleVarType.UI2:
                variant.Value = r.ReadUInt16();
                break;
            case OleVarType.Bool:
                variant.Value = r.ReadInt16() != 0;
                break;
            case OleVarType.I4 or OleVarType.Int or OleVarType.Error:
                variant.Value = r.ReadInt32();
                break;
            case OleVarType.UI4 or OleVarType.UInt:
                variant.Value = r.ReadUInt32();
                break;
            case OleVarType.R4:
                variant.Value = r.ReadSingle();
                break;
            case OleVarType.I8 or OleVarType.Cy:
                variant.Value = r.ReadInt64();
                break;
            case OleVarType.UI8:
                variant.Value = r.ReadUInt64();
                break;
            case OleVarType.R8 or OleVarType.Date:
                variant.Value = r.ReadDouble();
                break;
            case OleVarType.Bstr:
                r.ReadBstr().Then(s => variant.Value = s);
                break;
            case OleVarType.Unknown or OleVarType.Dispatch:
                r.ReadMInterfacePointer().Then(b => variant.Value = b);
                break;
            default:
                if (isArray)
                {
                    r.ReadSafeArray().Then(a =>
                    {
                        if (a is not null && a.ElementType != (vt & ~OleVarType.Array))
                            throw new NdrFormatException(
                                $"VARIANT type 0x{(ushort)vt:X} holds a SAFEARRAY of {a.ElementType}.");

                        variant.Value = a;
                    });
                    break;
                }

                throw new NdrFormatException($"VARIANT type 0x{(ushort)vt:X} is not supported.");
        }

        return variant;
    }

    private static OleSafeArray ReadSafeArrayBody(NdrReader r)
    {
        var maxDims = r.ReadMaxCount();
        var dims = r.ReadUInt16();
        var features = r.ReadUInt16();
        var elementSize = r.ReadUInt32();
        var locks = r.ReadUInt32();
        var sfType = r.ReadUInt32();

        if (dims == 0 || dims != maxDims)
            throw new NdrFormatException($"SAFEARRAY has {dims} dimensions but its bounds conformance is {maxDims}.");

        var elementType = (features & OleTypes.FadfHaveVarType) != 0
            ? (OleVarType)(locks >> 16)
            : OleTypes.DefaultElementType(sfType);
        if (!OleTypes.IsSafeArrayElementType(elementType) || OleTypes.SafeArrayTypeFor(elementType) != sfType)
            throw new NdrFormatException($"SAFEARRAY element type {elementType} does not match kind 0x{sfType:X}.");

        if (elementSize != OleTypes.ElementSizeFor(sfType))
            throw new NdrFormatException($"SAFEARRAY element size {elementSize} is wrong for kind 0x{sfType:X}.");

        var size = r.ReadUInt32();
        r.EnsureCount(size, 1);
        var elements = new object?[size];
        r.ReadUniquePointer(rr => ReadSafeArrayElements(rr, elementType, elements));

        r.EnsureCount(dims, 8);
        var bounds = new OleSafeArrayBound[dims];
        for (var i = dims - 1; i >= 0; i--)
        {
            var count = r.ReadUInt32();
            var lower = r.ReadInt32();
            bounds[i] = new OleSafeArrayBound(count, lower);
        }

        try
        {
            return new OleSafeArray(elementType, bounds, elements, validateElements: false);
        }
        catch (ArgumentException ex)
        {
            throw new NdrFormatException("SAFEARRAY bounds do not match its element count.", ex);
        }
    }

    private static bool ReadSafeArrayElements(NdrReader r, OleVarType elementType, object?[] elements)
    {
        var max = r.ReadMaxCount();
        if (max != elements.Length)
            throw new NdrFormatException($"SAFEARRAY data has {max} elements but Size is {elements.Length}.");

        for (var i = 0; i < elements.Length; i++)
        {
            var index = i;
            switch (elementType)
            {
                case OleVarType.I1:
                    elements[i] = unchecked((sbyte)r.ReadByte());
                    break;
                case OleVarType.UI1:
                    elements[i] = r.ReadByte();
                    break;
                case OleVarType.I2:
                    elements[i] = r.ReadInt16();
                    break;
                case OleVarType.UI2:
                    elements[i] = r.ReadUInt16();
                    break;
                case OleVarType.Bool:
                    elements[i] = r.ReadInt16() != 0;
                    break;
                case OleVarType.I4 or OleVarType.Int or OleVarType.Error:
                    elements[i] = r.ReadInt32();
                    break;
                case OleVarType.UI4 or OleVarType.UInt:
                    elements[i] = r.ReadUInt32();
                    break;
                case OleVarType.R4:
                    elements[i] = r.ReadSingle();
                    break;
                case OleVarType.I8 or OleVarType.Cy:
                    elements[i] = r.ReadInt64();
                    break;
                case OleVarType.UI8:
                    elements[i] = r.ReadUInt64();
                    break;
                case OleVarType.R8 or OleVarType.Date:
                    elements[i] = r.ReadDouble();
                    break;
                case OleVarType.Bstr:
                    r.ReadBstr().Then(s => elements[index] = s);
                    break;
                case OleVarType.Unknown or OleVarType.Dispatch:
                    r.ReadMInterfacePointer().Then(b => elements[index] = b);
                    break;
                case OleVarType.Variant:
                    r.ReadVariant().Then(v => elements[index] = v);
                    break;
                default:
                    throw new NdrFormatException($"SAFEARRAY of {elementType} is not supported.");
            }
        }

        return true;
    }
}

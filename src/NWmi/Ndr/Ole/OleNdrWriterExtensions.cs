using System.Text;

namespace NWmi.Ndr.Ole;

internal static class OleNdrWriterExtensions
{
    extension(NdrWriter writer)
    {
        /// <summary>
        /// A BSTR: <c>[unique] FLAGGED_WORD_BLOB*</c>. A null string is sent as a blob with cBytes = 0xFFFFFFFF
        /// (MS-OAUT 2.2.23.2), which is how Windows distinguishes it from an empty string.
        /// </summary>
        public void WriteBstr(string? value)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteUniquePointer(true, w => WriteBstrBlob(w, value));
        }

        /// <summary>
        /// A <c>[unique] MInterfacePointer*</c>: the byte count, then the marshaled OBJREF
        /// (conformance of the trailing array is hoisted, so the count appears twice).
        /// </summary>
        public void WriteMInterfacePointer(byte[]? objRef)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteUniquePointer(objRef, static (w, bytes) =>
            {
                w.WriteMaxCount(bytes.Length);
                w.WriteUInt32((uint)bytes.Length);
                w.WriteBytes(bytes);
            });
        }

        /// <summary>
        /// Writes an <c>[out, in, unique] T** pp</c> "give me an interface back" parameter, as MS-WMI uses for
        /// e.g. <c>GetObject</c>'s <c>ppObject</c>/<c>ppCallResult</c>: an always-present outer unique pointer
        /// (the slot) wrapping the MInterfacePointer value it currently holds, which on a request is always empty.
        /// </summary>
        /// <remarks>
        /// MS-WMI documents that the server MUST ignore the whole parameter if the outer pointer is null, which
        /// is exactly what a plain <see cref="WriteMInterfacePointer"/> call sends: GetObject then silently returns no
        /// object (WBEM_S_NO_ERROR, no error).
        /// </remarks>
        public void WriteOutInterfaceSlot()
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteUniquePointer(true, w => w.WriteMInterfacePointer(null));
        }

        /// <summary>A <c>[unique] _wireVARIANT*</c> (MS-OAUT 2.2.29); null writes a null pointer.</summary>
        public void WriteVariant(OleVariant? value)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteUniquePointer(value, WriteVariantBody);
        }

        /// <summary>A <c>[unique] _wireSAFEARRAY*</c> (MS-OAUT 2.2.30.10); null writes a null pointer.</summary>
        public void WriteSafeArray(OleSafeArray? value)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteUniquePointer(value, WriteSafeArrayBody);
        }
    }

    private static void WriteBstrBlob(NdrWriter w, string? value)
    {
        if (value is null)
        {
            w.WriteMaxCount(0);
            w.WriteUInt32(OleTypes.NullBstrByteCount);
            w.WriteUInt32(0);
            return;
        }

        w.WriteMaxCount(value.Length);
        w.WriteUInt32((uint)value.Length * 2);
        w.WriteUInt32((uint)value.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(value));
    }

    private static void WriteVariantBody(NdrWriter w, OleVariant variant)
    {
        var type = variant.Type;
        if ((type & OleVarType.ByRef) != 0)
            throw new NotSupportedException("VT_BYREF VARIANTs are not supported.");

        // The struct contains 8-byte union arms, so it is 8-aligned.
        w.Align(8);
        var start = w.Position;
        w.WriteUInt32(0); // clSize, patched below
        w.WriteUInt32(0); // rpcReserved
        w.WriteUInt16((ushort)type);
        w.WriteUInt16(0);
        w.WriteUInt16(0);
        w.WriteUInt16(0);

        // Union discriminant. For arrays the element type is masked off (as oleaut32 does).
        w.WriteUInt32((type & OleVarType.Array) != 0 ? (uint)OleVarType.Array : (uint)type);
        WriteVariantArm(w, variant);

        // Size of the inline part in 8-byte units; pointees follow after the structure.
        w.PatchUInt32(start, (uint)((w.Position - start + 7) / 8));
    }

    private static void WriteVariantArm(NdrWriter w, OleVariant variant)
    {
        var value = variant.Value;
        switch (variant.Type)
        {
            case OleVarType.Empty or OleVarType.Null:
                break;
            case OleVarType.I1:
                w.WriteByte(unchecked((byte)(sbyte)value!));
                break;
            case OleVarType.UI1:
                w.WriteByte((byte)value!);
                break;
            case OleVarType.I2:
                w.WriteInt16((short)value!);
                break;
            case OleVarType.UI2:
                w.WriteUInt16((ushort)value!);
                break;
            case OleVarType.Bool:
                w.WriteInt16((bool)value! ? (short)-1 : (short)0);
                break;
            case OleVarType.I4 or OleVarType.Int or OleVarType.Error:
                w.WriteInt32((int)value!);
                break;
            case OleVarType.UI4 or OleVarType.UInt:
                w.WriteUInt32((uint)value!);
                break;
            case OleVarType.R4:
                w.WriteSingle((float)value!);
                break;
            case OleVarType.I8 or OleVarType.Cy:
                w.WriteInt64((long)value!);
                break;
            case OleVarType.UI8:
                w.WriteUInt64((ulong)value!);
                break;
            case OleVarType.R8 or OleVarType.Date:
                w.WriteDouble((double)value!);
                break;
            case OleVarType.Bstr:
                w.WriteBstr((string?)value);
                break;
            case OleVarType.Unknown or OleVarType.Dispatch:
                w.WriteMInterfacePointer((byte[]?)value);
                break;
            default:
                if ((variant.Type & OleVarType.Array) != 0)
                {
                    w.WriteSafeArray((OleSafeArray?)value);
                    break;
                }

                throw new NotSupportedException($"VARIANT type {variant.Type} is not supported.");
        }
    }

    private static void WriteSafeArrayBody(NdrWriter w, OleSafeArray array)
    {
        var sfType = OleTypes.SafeArrayTypeFor(array.ElementType);

        // rgsabound is a trailing conformant array, so its max_count is hoisted to the start of the structure.
        w.WriteMaxCount(array.Bounds.Count);
        w.WriteUInt16((ushort)array.Bounds.Count);
        w.WriteUInt16(OleTypes.FeaturesFor(array.ElementType));
        w.WriteUInt32(OleTypes.ElementSizeFor(sfType));
        w.WriteUInt32((uint)array.ElementType << 16); // high word of cLocks holds the VARTYPE
        w.WriteUInt32(sfType);

        // Every arm is: Size, then a pointer to a conformant array of Size elements.
        w.WriteUInt32((uint)array.Elements.Length);
        w.WriteUniquePointer(true, ww => WriteSafeArrayElements(ww, array));

        // Bounds are sent in reverse order (MS-OAUT 2.2.30.10).
        for (var i = array.Bounds.Count - 1; i >= 0; i--)
        {
            w.WriteUInt32(array.Bounds[i].ElementCount);
            w.WriteInt32(array.Bounds[i].LowerBound);
        }
    }

    private static void WriteSafeArrayElements(NdrWriter w, OleSafeArray array)
    {
        w.WriteMaxCount(array.Elements.Length);
        foreach (var element in array.Elements)
            switch (array.ElementType)
            {
                case OleVarType.I1:
                    w.WriteByte(unchecked((byte)(sbyte)element!));
                    break;
                case OleVarType.UI1:
                    w.WriteByte((byte)element!);
                    break;
                case OleVarType.I2:
                    w.WriteInt16((short)element!);
                    break;
                case OleVarType.UI2:
                    w.WriteUInt16((ushort)element!);
                    break;
                case OleVarType.Bool:
                    w.WriteInt16((bool)element! ? (short)-1 : (short)0);
                    break;
                case OleVarType.I4 or OleVarType.Int or OleVarType.Error:
                    w.WriteInt32((int)element!);
                    break;
                case OleVarType.UI4 or OleVarType.UInt:
                    w.WriteUInt32((uint)element!);
                    break;
                case OleVarType.R4:
                    w.WriteSingle((float)element!);
                    break;
                case OleVarType.I8 or OleVarType.Cy:
                    w.WriteInt64((long)element!);
                    break;
                case OleVarType.UI8:
                    w.WriteUInt64((ulong)element!);
                    break;
                case OleVarType.R8 or OleVarType.Date:
                    w.WriteDouble((double)element!);
                    break;
                case OleVarType.Bstr:
                    w.WriteBstr((string?)element);
                    break;
                case OleVarType.Unknown or OleVarType.Dispatch:
                    w.WriteMInterfacePointer((byte[]?)element);
                    break;
                case OleVarType.Variant:
                    w.WriteVariant((OleVariant?)element);
                    break;
                default:
                    throw new NotSupportedException($"SAFEARRAY of {array.ElementType} is not supported.");
            }
    }
}

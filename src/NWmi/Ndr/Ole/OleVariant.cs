namespace NWmi.Ndr.Ole;

/// <summary>
/// A VARIANT as it travels on the wire (MS-OAUT 2.2.29).
/// <see cref="Value"/> holds:
/// sbyte (I1), byte (UI1), short (I2), ushort (UI2), int (I4, Int, Error), uint (UI4, UInt), long (I8, Cy),
/// ulong (UI8), float (R4), double (R8, Date), bool (Bool), string? (Bstr), byte[]? (Unknown/Dispatch: the
/// raw OBJREF of an MInterfacePointer), <see cref="OleSafeArray"/>? (Array), and null for Empty and Null.
/// </summary>
internal sealed class OleVariant
{
    public OleVariant(OleVarType type, object? value = null)
    {
        OleTypes.ValidateValue(type, value);
        Type = type;
        Value = value;
    }

    // A distinct marker type, so this can never be picked over the public constructor for a bool value.
    private enum Unvalidated
    {
        Yes,
    }

    private OleVariant(OleVarType type, Unvalidated marker)
    {
        _ = marker;
        Type = type;
    }

    public static OleVariant Empty => new(OleVarType.Empty);

    public static OleVariant Null => new(OleVarType.Null);

    public OleVarType Type { get; }

    public object? Value { get; internal set; }

    /// <summary>A variant whose value the reader fills in afterwards (it may be a deferred pointee).</summary>
    internal static OleVariant ForRead(OleVarType type) => new(type, Unvalidated.Yes);

    public static OleVariant FromSByte(sbyte value) => new(OleVarType.I1, value);

    public static OleVariant FromByte(byte value) => new(OleVarType.UI1, value);

    public static OleVariant FromInt16(short value) => new(OleVarType.I2, value);

    public static OleVariant FromUInt16(ushort value) => new(OleVarType.UI2, value);

    public static OleVariant FromInt32(int value) => new(OleVarType.I4, value);

    public static OleVariant FromUInt32(uint value) => new(OleVarType.UI4, value);

    public static OleVariant FromInt64(long value) => new(OleVarType.I8, value);

    public static OleVariant FromUInt64(ulong value) => new(OleVarType.UI8, value);

    public static OleVariant FromSingle(float value) => new(OleVarType.R4, value);

    public static OleVariant FromDouble(double value) => new(OleVarType.R8, value);

    public static OleVariant FromBoolean(bool value) => new(OleVarType.Bool, value);

    public static OleVariant FromString(string? value) => new(OleVarType.Bstr, value);

    public static OleVariant FromError(int hresult) => new(OleVarType.Error, hresult);

    public static OleVariant FromUnknown(byte[]? objRef) => new(OleVarType.Unknown, objRef);

    public static OleVariant FromDispatch(byte[]? objRef) => new(OleVarType.Dispatch, objRef);

    public static OleVariant FromArray(OleSafeArray? value) =>
        new(OleVarType.Array | (value?.ElementType ?? OleVarType.Variant), value);

    public override string ToString() => $"{Type}: {Value ?? "(null)"}";
}

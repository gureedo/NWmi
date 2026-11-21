namespace NWmi.Ndr.Ole;

/// <summary>Type tables shared by the VARIANT and SAFEARRAY marshalers.</summary>
internal static class OleTypes
{
    // <summary>SF_TYPE discriminants (MS-OAUT 2.2.8); each equals the VARTYPE of its element group.</summary>

    internal const uint SfI1 = 0x10;
    internal const uint SfI2 = 0x02;
    internal const uint SfI4 = 0x03;
    internal const uint SfI8 = 0x14;
    internal const uint SfBstr = 0x08;
    internal const uint SfUnknown = 0x0D;
    internal const uint SfDispatch = 0x09;
    internal const uint SfVariant = 0x0C;

    // <summary>FADF_* flags (MS-OAUT 2.2.9).</summary>

    internal const ushort FadfHaveVarType = 0x0080;
    internal const ushort FadfBstr = 0x0100;
    internal const ushort FadfUnknown = 0x0200;
    internal const ushort FadfDispatch = 0x0400;
    internal const ushort FadfVariant = 0x0800;

    internal const uint NullBstrByteCount = 0xFFFFFFFF;

    internal static bool IsSafeArrayElementType(OleVarType type) => type switch
    {
        OleVarType.I1 or OleVarType.UI1 or OleVarType.I2 or OleVarType.UI2 or OleVarType.Bool
            or OleVarType.I4 or OleVarType.UI4 or OleVarType.Int or OleVarType.UInt or OleVarType.Error or OleVarType.R4
            or OleVarType.I8 or OleVarType.UI8 or OleVarType.R8 or OleVarType.Cy or OleVarType.Date
            or OleVarType.Bstr or OleVarType.Unknown or OleVarType.Dispatch or OleVarType.Variant => true,
        _ => false,
    };

    /// <summary>The SF_TYPE discriminant used for arrays of <paramref name="element"/> (MS-OAUT 2.2.30.10 table).</summary>
    internal static uint SafeArrayTypeFor(OleVarType element) => element switch
    {
        OleVarType.I1 or OleVarType.UI1 => SfI1,
        OleVarType.I2 or OleVarType.UI2 or OleVarType.Bool => SfI2,
        OleVarType.I4 or OleVarType.UI4 or OleVarType.Int or OleVarType.UInt or OleVarType.Error
            or OleVarType.R4 => SfI4,
        OleVarType.I8 or OleVarType.UI8 or OleVarType.R8 or OleVarType.Cy or OleVarType.Date => SfI8,
        OleVarType.Bstr => SfBstr,
        OleVarType.Unknown => SfUnknown,
        OleVarType.Dispatch => SfDispatch,
        OleVarType.Variant => SfVariant,
        _ => throw new NotSupportedException($"SAFEARRAY of {element} is not supported."),
    };

    /// <summary>Element type to assume when the sender did not set FADF_HAVEVARTYPE.</summary>
    internal static OleVarType DefaultElementType(uint sfType) => sfType switch
    {
        SfI1 => OleVarType.I1,
        SfI2 => OleVarType.I2,
        SfI4 => OleVarType.I4,
        SfI8 => OleVarType.I8,
        SfBstr => OleVarType.Bstr,
        SfUnknown => OleVarType.Unknown,
        SfDispatch => OleVarType.Dispatch,
        SfVariant => OleVarType.Variant,
        _ => throw new NdrFormatException($"SAFEARRAY element kind 0x{sfType:X} is not supported."),
    };

    /// <summary>Element size in bytes that goes into <c>cbElements</c> (MS-OAUT 2.2.8).</summary>
    internal static uint ElementSizeFor(uint sfType) => sfType switch
    {
        SfI1 => 1,
        SfI2 => 2,
        SfI8 => 8,
        SfVariant => 16,
        _ => 4,
    };

    internal static ushort FeaturesFor(OleVarType element) => element switch
    {
        OleVarType.Bstr => FadfBstr | FadfHaveVarType,
        OleVarType.Unknown => FadfUnknown | FadfHaveVarType,
        OleVarType.Dispatch => FadfDispatch | FadfHaveVarType,
        OleVarType.Variant => FadfVariant | FadfHaveVarType,
        _ => FadfHaveVarType,
    };

    internal static void ValidateValue(OleVarType type, object? value)
    {
        if ((type & OleVarType.Array) != 0)
        {
            var element = type & ~OleVarType.Array;
            if (!IsSafeArrayElementType(element))
                throw new NotSupportedException($"VARIANT type {type} is not supported.");

            if (value is not null && (value is not OleSafeArray array || array.ElementType != element))
                throw new ArgumentException($"A {type} VARIANT needs a null or {element} {nameof(OleSafeArray)}.",
                    nameof(value));

            return;
        }

        switch (type)
        {
            case OleVarType.Empty or OleVarType.Null:
                if (value is not null)
                    throw new ArgumentException($"A {type} VARIANT carries no value.", nameof(value));

                break;
            case OleVarType.Bstr:
                Expect<string>(type, value, allowNull: true);
                break;
            case OleVarType.Unknown or OleVarType.Dispatch:
                Expect<byte[]>(type, value, allowNull: true);
                break;
            case OleVarType.I1 or OleVarType.UI1 or OleVarType.I2 or OleVarType.UI2 or OleVarType.Bool
                or OleVarType.I4 or OleVarType.UI4 or OleVarType.Int or OleVarType.UInt or OleVarType.Error
                or OleVarType.R4
                or OleVarType.I8 or OleVarType.UI8 or OleVarType.R8 or OleVarType.Cy or OleVarType.Date:
                ValidateScalar(type, value);
                break;
            default:
                throw new NotSupportedException($"VARIANT type {type} is not supported.");
        }
    }

    internal static void ValidateElement(OleVarType elementType, object? element)
    {
        switch (elementType)
        {
            case OleVarType.Variant:
                Expect<OleVariant>(elementType, element, allowNull: true);
                break;
            case OleVarType.Bstr:
                Expect<string>(elementType, element, allowNull: true);
                break;
            case OleVarType.Unknown or OleVarType.Dispatch:
                Expect<byte[]>(elementType, element, allowNull: true);
                break;
            default:
                ValidateScalar(elementType, element);
                break;
        }
    }

    private static void ValidateScalar(OleVarType type, object? value)
    {
        var ok = type switch
        {
            OleVarType.I1 => value is sbyte,
            OleVarType.UI1 => value is byte,
            OleVarType.I2 => value is short,
            OleVarType.UI2 => value is ushort,
            OleVarType.Bool => value is bool,
            OleVarType.I4 or OleVarType.Int or OleVarType.Error => value is int,
            OleVarType.UI4 or OleVarType.UInt => value is uint,
            OleVarType.R4 => value is float,
            OleVarType.I8 or OleVarType.Cy => value is long,
            OleVarType.UI8 => value is ulong,
            OleVarType.R8 or OleVarType.Date => value is double,
            _ => false,
        };

        if (!ok)
            throw new ArgumentException($"{value?.GetType().Name ?? "null"} is not a valid value for {type}.",
                nameof(value));
    }

    private static void Expect<T>(OleVarType type, object? value, bool allowNull)
    {
        if (value is null ? !allowNull : value is not T)
            throw new ArgumentException($"{value?.GetType().Name ?? "null"} is not a valid value for {type}.",
                nameof(value));
    }
}

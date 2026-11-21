namespace NWmi.Ndr.Ole;

/// <summary>One dimension of a SAFEARRAY (MS-OAUT 2.2.30.1).</summary>
internal readonly record struct OleSafeArrayBound(uint ElementCount, int LowerBound);

/// <summary>
/// A SAFEARRAY (MS-OAUT 2.2.30).
/// <see cref="Elements"/> is flat, in wire order, and typed as <see cref="OleVariant.Value"/> describes for <see cref="ElementType"/>;
/// Variant elements are <see cref="OleVariant"/> (or null). <see cref="Bounds"/> are in declaration order, first dimension first.
/// </summary>
internal sealed class OleSafeArray
{
    public OleVarType ElementType { get; }

    public IReadOnlyList<OleSafeArrayBound> Bounds { get; }

    public object?[] Elements { get; }

    public static OleSafeArray FromVector(OleVarType elementType, IEnumerable<object?> elements, int lowerBound = 0)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var items = elements.ToArray();
        return new OleSafeArray(elementType, [new OleSafeArrayBound((uint)items.Length, lowerBound)], items);
    }

    public static OleSafeArray FromStrings(IEnumerable<string?> values) => FromVector(OleVarType.Bstr, values);

    public OleSafeArray(OleVarType elementType, IEnumerable<OleSafeArrayBound> bounds, object?[] elements)
        : this(elementType, bounds, elements, validateElements: true)
    {
    }

    internal OleSafeArray(OleVarType elementType, IEnumerable<OleSafeArrayBound> bounds, object?[] elements,
        bool validateElements)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(elements);
        if (!OleTypes.IsSafeArrayElementType(elementType))
            throw new NotSupportedException($"SAFEARRAY of {elementType} is not supported.");

        var boundList = bounds.ToArray();
        if (boundList.Length is 0 or > ushort.MaxValue)
            throw new ArgumentException("A SAFEARRAY needs between 1 and 65535 dimensions.", nameof(bounds));

        long total = 1;
        foreach (var bound in boundList)
        {
            if (bound.ElementCount == 0)
                throw new ArgumentException("A SAFEARRAY dimension must have at least one element.", nameof(bounds));

            total *= bound.ElementCount;
            if (total > int.MaxValue)
                throw new ArgumentException("The SAFEARRAY is too large.", nameof(bounds));
        }

        if (elements.Length != total)
            throw new ArgumentException($"Bounds describe {total} elements but {elements.Length} were given.",
                nameof(elements));

        if (validateElements)
            foreach (var element in elements)
                OleTypes.ValidateElement(elementType, element);

        ElementType = elementType;
        Bounds = boundList;
        Elements = elements;
    }
}

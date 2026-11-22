namespace NWmi.Client;

/// <summary>
/// What a CIM element is and how it behaves: the kind of element (<see cref="Property"/>, <see cref="Parameter"/>, ...),
/// what its qualifiers say (<see cref="Key"/>, <see cref="ReadOnly"/>, ...), a qualifier's flavor
/// (<see cref="EnableOverride"/>, <see cref="ToSubclass"/>, ...) and the state of a value (<see cref="NullValue"/>,
/// <see cref="NotModified"/>).
/// </summary>
[Flags]
public enum WmiFlags : long
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The element is a class.</summary>
    Class = 0x1,

    /// <summary>The element is a method.</summary>
    Method = 0x2,

    /// <summary>The element is a property.</summary>
    Property = 0x4,

    /// <summary>The element is a method parameter.</summary>
    Parameter = 0x8,

    /// <summary>The element is an association class.</summary>
    Association = 0x10,

    /// <summary>The element is an indication (event) class.</summary>
    Indication = 0x20,

    /// <summary>The element is a reference.</summary>
    Reference = 0x40,

    /// <summary>Any kind of element.</summary>
    Any = 0x7F,

    /// <summary>A qualifier a subclass can override.</summary>
    EnableOverride = 0x80,

    /// <summary>A qualifier a subclass cannot override.</summary>
    DisableOverride = 0x100,

    /// <summary>A qualifier that does not propagate to subclasses.</summary>
    Restricted = 0x200,

    /// <summary>A qualifier that propagates to subclasses.</summary>
    ToSubclass = 0x400,

    /// <summary>A localized (amended) qualifier.</summary>
    Translatable = 0x800,

    /// <summary>A key property.</summary>
    Key = 0x1000,

    /// <summary>An input parameter.</summary>
    In = 0x2000,

    /// <summary>An output parameter.</summary>
    Out = 0x4000,

    /// <summary>A property that must have a value.</summary>
    Required = 0x8000,

    /// <summary>A static method or property.</summary>
    Static = 0x10000,

    /// <summary>An abstract class.</summary>
    Abstract = 0x20000,

    /// <summary>A class that cannot be derived from.</summary>
    Terminal = 0x40000,

    /// <summary>A property that is expensive to compute.</summary>
    Expensive = 0x80000,

    /// <summary>A streamed parameter.</summary>
    Stream = 0x100000,

    /// <summary>A property that is read but not written (<c>read</c> without <c>write</c>).</summary>
    ReadOnly = 0x200000,

    /// <summary>A value that has not been set since it was read.</summary>
    NotModified = 0x2000000,

    /// <summary>A value that is null.</summary>
    NullValue = 0x20000000,

    /// <summary>Reserved (MMI ownership flag); never set by NWmi.</summary>
    Borrow = 0x40000000,

    /// <summary>Reserved (MMI ownership flag); never set by NWmi.</summary>
    Adopt = 0x80000000,
}

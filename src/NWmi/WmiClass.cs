using NWmi.Generic;
using NWmi.Wmio;

namespace NWmi;

/// <summary>
/// A CIM class: its properties, methods and qualifiers, read-only. Get one with <see cref="WmiConnection.GetClassAsync"/>
/// or <see cref="WmiConnection.EnumerateClassesAsync"/>, or from an instance's <see cref="WmiInstance.Class"/>; make an
/// instance of it with <see cref="WmiInstance(WmiClass)"/>.
/// </summary>
public sealed class WmiClass
{
    private readonly Lazy<WmiReadOnlyKeyedCollection<WmiPropertyDeclaration>> _properties;
    private readonly Lazy<WmiReadOnlyKeyedCollection<WmiMethodDeclaration>> _methods;
    private readonly Lazy<WmiReadOnlyKeyedCollection<WmiQualifier>> _qualifiers;
    private readonly string? _namespace;
    private readonly string? _server;

    internal WmiClass(string name, string? superClassName,
        Func<IEnumerable<WmiPropertyDeclaration>> properties,
        Func<IEnumerable<WmiMethodDeclaration>> methods,
        Func<IEnumerable<WmiQualifier>> qualifiers,
        string? server, string? @namespace, WmioClass? wire = null)
    {
        Name = name;
        SuperClassName = superClassName;
        _properties = new Lazy<WmiReadOnlyKeyedCollection<WmiPropertyDeclaration>>(() =>
            new WmiReadOnlyKeyedCollection<WmiPropertyDeclaration>(properties(), p => p.Name));
        _methods = new Lazy<WmiReadOnlyKeyedCollection<WmiMethodDeclaration>>(() =>
            new WmiReadOnlyKeyedCollection<WmiMethodDeclaration>(methods(), m => m.Name));
        _qualifiers = new Lazy<WmiReadOnlyKeyedCollection<WmiQualifier>>(() =>
            new WmiReadOnlyKeyedCollection<WmiQualifier>(qualifiers(), q => q.Name));
        _server = server;
        _namespace = @namespace;
        Wire = wire;
    }

    /// <summary>
    /// A class decoded from the binary object format; the members are converted when first read. The class an instance
    /// carries has no methods (MS-WMIO 2.2.53).
    /// </summary>
    internal static WmiClass FromWire(WmioClass wire, string? server, string? @namespace)
    {
        var isParameters = string.Equals(wire.Name, "__PARAMETERS", StringComparison.OrdinalIgnoreCase);
        return new WmiClass(wire.Name ?? string.Empty, wire.SuperclassName,
            () => wire.Properties.Select(p => WmiTypeMapping.Declaration(p, isParameters, server, @namespace)),
            () => wire.Methods.Select(WmiTypeMapping.Method),
            () => WmiTypeMapping.Qualifiers(wire.Qualifiers),
            server, @namespace, wire);
    }

    /// <summary>The class of an instance that came with no class definition: just its properties, with no qualifiers or methods.</summary>
    internal static WmiClass Synthesized(string name, IEnumerable<WmiPropertyDeclaration> properties, string? server,
        string? @namespace)
    {
        var list = properties.ToList();
        return new WmiClass(name, null, () => list, () => [], () => [], server, @namespace);
    }

    /// <summary>The class's name.</summary>
    public string Name { get; }

    /// <summary>The immediate superclass's name, or null for a class with none.</summary>
    public string? SuperClassName { get; }

    /// <summary>The properties, its own and inherited ones.</summary>
    public WmiReadOnlyKeyedCollection<WmiPropertyDeclaration> Properties => _properties.Value;

    /// <summary>
    /// The methods. Empty for the class an instance carries over DCOM, since the binary encoding of an instance leaves its
    /// class's methods out; <see cref="WmiConnection.GetClassAsync"/> gives them.
    /// </summary>
    public WmiReadOnlyKeyedCollection<WmiMethodDeclaration> Methods => _methods.Value;

    /// <summary>The class's own qualifiers (<c>dynamic</c>, <c>provider</c>, <c>abstract</c>, ...).</summary>
    public WmiReadOnlyKeyedCollection<WmiQualifier> Qualifiers => _qualifiers.Value;

    /// <summary>The class name, namespace, server and path (<c>\\server\namespace:ClassName</c>).</summary>
    public WmiSystemProperties SystemProperties =>
        new(Name, _namespace, _server,
            _namespace is null ? null : new WmiObjectPath(Name, null, _namespace, _server).ToString(), Name);

    /// <summary>The binary class definition, for encoding instances of it; null for a class read over WinRM or synthesized.</summary>
    internal WmioClass? Wire { get; }

    /// <inheritdoc/>
    public override string ToString() => _namespace is null ? Name : $"{_namespace}:{Name}";
}

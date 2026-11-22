namespace NWmi;

/// <summary>Where a class or instance lives and how it is addressed: <c>__CLASS</c>, <c>__NAMESPACE</c>, <c>__SERVER</c>, <c>__PATH</c>, <c>__RELPATH</c>.</summary>
public sealed class WmiSystemProperties
{
    internal WmiSystemProperties(string className, string? @namespace, string? server, string? path, string? relativePath)
    {
        ClassName = className;
        Namespace = @namespace;
        Server = server;
        Path = path;
        RelativePath = relativePath;
    }

    /// <summary>The class name (<c>__CLASS</c>).</summary>
    public string ClassName { get; }

    /// <summary>The namespace (<c>__NAMESPACE</c>, e.g. <c>root\cimv2</c>); null when not known, as for an instance built on the client.</summary>
    public string? Namespace { get; }

    /// <summary>The server (<c>__SERVER</c>); null when not known.</summary>
    public string? Server { get; }

    /// <summary>
    /// <c>\\server\namespace:RelativePath</c> (<c>__PATH</c>); null when the namespace is not known, and for a class that a
    /// query returned in place of an instance.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// <c>ClassName.Key1=value1,...</c> (<c>__RELPATH</c>), or just the class name for a class, a keyless instance, or an
    /// instance whose keys have no value yet; null for a class that a query returned in place of an instance.
    /// </summary>
    public string? RelativePath { get; }

    /// <inheritdoc/>
    public override string ToString() => Path ?? RelativePath ?? ClassName;
}

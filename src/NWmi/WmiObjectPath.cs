using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NWmi;

/// <summary>
/// A parsed CIM object path (MS-WMI 2.2.2): <c>\\server\root\cimv2:Win32_Process.Handle="4"</c>, with every part
/// but the class name optional. Made by <see cref="Parse"/> or built directly, and printed back with
/// <see cref="ToString"/>.
/// </summary>
public sealed partial class WmiObjectPath
{
    /// <summary>Creates a path from its parts.</summary>
    public WmiObjectPath(
        string className,
        IEnumerable<KeyValuePair<string, object>>? keys = null,
        string? @namespace = null,
        string? server = null,
        bool isSingleton = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        Keys = [.. keys ?? []];
        if (isSingleton && Keys.Count > 0)
            throw new ArgumentException("A singleton path (Class=@) has no key values.", nameof(isSingleton));

        if (server is not null && @namespace is null)
            throw new ArgumentException("A path with a server needs a namespace.", nameof(server));

        ClassName = className;
        Namespace = @namespace;
        Server = server;
        IsSingleton = isSingleton;
    }

    /// <summary>The machine part of the namespace path (<c>.</c> for the local machine), or null.</summary>
    public string? Server { get; }

    /// <summary>The namespace, with <c>\</c> separators (<c>root\cimv2</c>), or null for a relative path.</summary>
    public string? Namespace { get; }

    /// <summary>The class name.</summary>
    public string ClassName { get; }

    /// <summary>The key property values in the order the path gives them: strings, <see cref="bool"/>s, <see cref="long"/>s (or <see cref="ulong"/>s that do not fit) and <see cref="double"/>s.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> Keys { get; }

    /// <summary>True for the single instance of a class that has just one: <c>Class=@</c>.</summary>
    public bool IsSingleton { get; }

    /// <summary>True for a path to a class (no key values and not a singleton).</summary>
    public bool IsClass => Keys.Count == 0 && !IsSingleton;

    /// <summary>True for a path to an instance, keyed or a singleton.</summary>
    public bool IsInstance => !IsClass;

    /// <summary>The value of the key property <paramref name="name"/> (compared without regard to case).</summary>
    /// <exception cref="KeyNotFoundException">The path has no such key.</exception>
    public object this[string name] =>
        TryGetKey(name, out var value) ? value : throw new KeyNotFoundException($"The path has no key named '{name}'.");

    /// <summary>Gets the value of a key property (compared without regard to case); false if the path has no such key.</summary>
    public bool TryGetKey(string name, out object value)
    {
        foreach (var (keyName, keyValue) in Keys)
            if (string.Equals(keyName, name, StringComparison.OrdinalIgnoreCase))
            {
                value = keyValue;
                return true;
            }

        value = null!;
        return false;
    }

    /// <summary><c>Class</c>, <c>Class=@</c> or <c>Class.Key1="v",Key2=2</c> (<c>__RELPATH</c>), the key values in the typed-constant form MS-WMI 2.2.2 defines.</summary>
    /// <exception cref="ArgumentException">A key value is not a string, a number or a <see cref="bool"/>.</exception>
    public string RelativePath
    {
        get
        {
            if (IsSingleton)
                return $"{ClassName}=@";

            var builder = new StringBuilder(ClassName);
            var first = true;
            foreach (var (name, value) in Keys)
            {
                builder.Append(first ? '.' : ',').Append(name).Append('=').Append(FormatTypedConstant(value));
                first = false;
            }

            return builder.ToString();
        }
    }

    /// <summary><c>\\server\namespace</c> or <c>namespace</c>; null when the path has no namespace.</summary>
    public string? NamespacePath => Namespace is null ? null : Server is null ? Namespace : $@"\\{Server}\{Namespace}";

    /// <summary>The whole path (<c>__PATH</c>).</summary>
    public override string ToString() => NamespacePath is null ? RelativePath : $"{NamespacePath}:{RelativePath}";

    /// <summary>Parses an object path: <c>Class</c>, <c>Class=@</c>, <c>Class.Key="v"</c>, each optionally behind <c>[\\server\]namespace:</c>.</summary>
    /// <exception cref="FormatException">The text is not an object path.</exception>
    public static WmiObjectPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return TryParse(path, out var result, out var error) ? result : throw new FormatException(error);
    }

    /// <summary>Parses an object path; false, with a null result, if the text is not one.</summary>
    public static bool TryParse(string? path, [NotNullWhen(true)] out WmiObjectPath? result) => TryParse(path, out result, out _);

    private static string FormatTypedConstant(object? value) => value switch
    {
        null => throw new ArgumentException("A key property value cannot be null."),
        bool b => b ? "TRUE" : "FALSE",
        string s => $"\"{s.Replace("\\", @"\\").Replace("\"", "\\\"")}\"",
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float or double => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Key values of type {value.GetType()} are not supported."),
    };

    private static bool TryParse(string? text, [NotNullWhen(true)] out WmiObjectPath? result, out string error)
    {
        result = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "An object path cannot be empty.";
            return false;
        }

        text = text.Trim();
        string? server = null;
        string? @namespace = null;
        var relative = text;
        var colon = IndexOfUnquoted(text, ':');
        if (colon >= 0)
        {
            if (!TryParseNamespacePath(text[..colon], out server, out @namespace, out error))
                return false;

            relative = text[(colon + 1)..];
        }
        else if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
        {
            error = $"'{text}' is a namespace path; an object path needs ':' and a class after it.";
            return false;
        }

        var scanner = new Scanner(relative);
        var className = scanner.ReadIdentifier();
        if (className is null)
        {
            error = $"'{relative}' does not start with a class name.";
            return false;
        }

        scanner.SkipWhitespace();
        if (scanner.AtEnd)
        {
            result = new WmiObjectPath(className, null, @namespace, server);
            return true;
        }

        if (scanner.TryRead('='))
        {
            scanner.SkipWhitespace();
            if (scanner.TryRead('@') && scanner.AtEndAfterWhitespace())
            {
                result = new WmiObjectPath(className, null, @namespace, server, isSingleton: true);
                return true;
            }

            error = $"'{relative}': after the class name '=' can only be followed by '@' (a singleton); name the key as Class.Key=value.";
            return false;
        }

        if (!scanner.TryRead('.'))
        {
            error = $"'{relative}': expected '.' and key values, or '=@', after the class name.";
            return false;
        }

        var keys = new List<KeyValuePair<string, object>>();
        while (true)
        {
            scanner.SkipWhitespace();
            var name = scanner.ReadIdentifier();
            if (name is null)
            {
                error = $"'{relative}': expected a key property name at position {scanner.Position}.";
                return false;
            }

            if (keys.Any(k => string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"'{relative}': the key '{name}' appears twice.";
                return false;
            }

            scanner.SkipWhitespace();
            if (!scanner.TryRead('='))
            {
                error = $"'{relative}': expected '=' after the key '{name}'.";
                return false;
            }

            scanner.SkipWhitespace();
            if (!scanner.TryReadTypedConstant(out var value, out error))
            {
                error = $"'{relative}': {error}";
                return false;
            }

            keys.Add(new KeyValuePair<string, object>(name, value));
            scanner.SkipWhitespace();
            if (scanner.AtEnd)
                break;

            if (!scanner.TryRead(','))
            {
                error = $"'{relative}': expected ',' or the end of the path at position {scanner.Position}.";
                return false;
            }
        }

        result = new WmiObjectPath(className, keys, @namespace, server);
        return true;
    }

    /// <summary><c>[\\machine\]namespace</c>, with either slash accepted and the namespace normalized to backslashes.</summary>
    private static bool TryParseNamespacePath(string prefix, out string? server, out string? @namespace, out string error)
    {
        server = null;
        @namespace = null;
        error = string.Empty;
        var normalized = prefix.Trim().Replace('/', '\\');
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = normalized[2..];
            var slash = rest.IndexOf('\\', StringComparison.Ordinal);
            if (slash <= 0)
            {
                error = $"'{prefix}': a machine path needs a namespace after the machine name.";
                return false;
            }

            server = rest[..slash];
            normalized = rest[(slash + 1)..];
        }

        if (normalized.Length == 0 || !normalized.Split('\\').All(segment => NamespaceSegmentRegex().IsMatch(segment)))
        {
            error = $"'{prefix}' is not a namespace path.";
            return false;
        }

        @namespace = normalized;
        return true;
    }

    /// <summary>The index of the first <paramref name="target"/> outside a double-quoted string, or -1.</summary>
    private static int IndexOfUnquoted(string text, char target)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                    i++; // the escaped character cannot end the string
                else if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == target)
                return i;
        }

        return -1;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_-]*$")]
    private static partial Regex NamespaceSegmentRegex();

    [GeneratedRegex(@"^[-+]?\d+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^[-+]?\d*\.\d+([eE][-+]?\d+)?$")]
    private static partial Regex RealRegex();

    [GeneratedRegex(@"^\d{14}\.\d{6}[+-]\d{3}$")]
    private static partial Regex DateTimeRegex();

    /// <summary>A cursor over the relative part of a path.</summary>
    private sealed class Scanner(string text)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        public bool AtEndAfterWhitespace()
        {
            SkipWhitespace();
            return AtEnd;
        }

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(text[Position]))
                Position++;
        }

        public bool TryRead(char c)
        {
            if (AtEnd || text[Position] != c)
                return false;

            Position++;
            return true;
        }

        /// <summary>A class or property name: letters, digits and underscores, not starting with a digit.</summary>
        public string? ReadIdentifier()
        {
            var start = Position;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(text[Position]) || text[Position] == '_'))
                Position++;

            var identifier = text[start..Position];
            return IdentifierRegex().IsMatch(identifier) ? identifier : null;
        }

        public bool TryReadTypedConstant(out object value, out string error)
        {
            error = string.Empty;
            value = null!;
            if (AtEnd)
            {
                error = "a key value is missing.";
                return false;
            }

            if (text[Position] is '"' or '\'')
                return TryReadString(out value, out error);

            var start = Position;
            while (!AtEnd && text[Position] != ',')
                Position++;

            var token = text[start..Position].Trim();
            if (token.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                value = true;
            else if (token.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                value = false;
            else if (IntegerRegex().IsMatch(token))
                if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    value = l;
                else if (ulong.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var u))
                    value = u;
                else
                {
                    error = $"the integer '{token}' does not fit in 64 bits.";
                    return false;
                }
            else if (RealRegex().IsMatch(token))
                value = double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (DateTimeRegex().IsMatch(token))
                value = token;
            else
            {
                error = $"'{token}' is not a key value (a quoted string, a number, TRUE, FALSE or a datetime).";
                return false;
            }

            return true;
        }

        private bool TryReadString(out object value, out string error)
        {
            error = string.Empty;
            value = null!;
            var quote = text[Position++];
            var builder = new StringBuilder();
            while (!AtEnd)
            {
                var c = text[Position++];
                if (c == '\\')
                {
                    if (AtEnd)
                        break;

                    builder.Append(text[Position++]); // a backslash escapes whatever follows it
                }
                else if (c == quote)
                {
                    value = builder.ToString();
                    return true;
                }
                else
                    builder.Append(c);
            }

            error = "a quoted key value is not closed.";
            return false;
        }
    }
}

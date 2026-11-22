
namespace NWmi.Cli;

/// <summary>One output format of the tool: <see cref="BeginAsync"/>, then each object, then <see cref="EndAsync"/>.</summary>
internal interface IResultWriter : IAsyncDisposable
{
    /// <summary>Writes whatever precedes the first object.</summary>
    ValueTask BeginAsync();

    /// <summary>Writes one result object.</summary>
    ValueTask WriteAsync(WmiInstance obj);

    /// <summary>Writes whatever follows the last object.</summary>
    ValueTask EndAsync();

    /// <summary>The writer for <paramref name="format"/>: <c>text</c>, <c>json</c> (one array) or <c>jsonl</c> (one object per line).</summary>
    static IResultWriter Create(string format, Stream stdout, TextWriter stdoutText) => format switch
    {
        "text" => new TextResultWriter(stdoutText),
        "json" => new JsonResultWriter(stdout),
        "jsonl" => new JsonLinesResultWriter(stdout),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The output format must be text, json or jsonl."),
    };
}

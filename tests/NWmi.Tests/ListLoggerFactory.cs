using Microsoft.Extensions.Logging;

namespace NWmi.Tests;

/// <summary>An <see cref="ILoggerFactory"/> that appends the formatted messages of every logger to a list.</summary>
internal sealed class ListLoggerFactory(List<string> messages) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new ListLogger(messages);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class ListLogger(List<string> messages) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (messages) messages.Add(formatter(state, exception));
    }
}

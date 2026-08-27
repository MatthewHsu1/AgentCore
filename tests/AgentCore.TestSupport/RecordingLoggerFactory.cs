using Microsoft.Extensions.Logging;

namespace AgentCore.TestSupport;

/// <summary>
/// One line the library wrote, kept with its fields rather than only its formatted text.
/// </summary>
/// <param name="Level">How serious the line is.</param>
/// <param name="EventId">The id the source-generated method carries.</param>
/// <param name="Message">The formatted text.</param>
/// <param name="Exception">The cause, when the line carried one.</param>
/// <param name="Fields">
/// The named values behind the message. A structured sink reads these and not the text, so a test
/// that only matched the text would pass on a line that flattened every field into a string.
/// </param>
public sealed record CapturedLine(
    LogLevel Level,
    int EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Fields)
{
    /// <summary>Reads one named field.</summary>
    /// <typeparam name="T">What the field is expected to hold.</typeparam>
    /// <param name="name">The field name from the message template.</param>
    /// <returns>The value, or <see langword="null"/> when the line carries no such field.</returns>
    public T? Field<T>(string name)
        => Fields.FirstOrDefault(field => string.Equals(field.Key, name, StringComparison.Ordinal))
            .Value is T value ? value : default;
}

/// <summary>A logger factory a test reads back, over one logger shared by every category.</summary>
/// <remarks>
/// Shared rather than per-assembly, because two suites need it: the knowledge provider's own facts,
/// and the composition-root fact that the boot's factory really reaches the compiled provider.
/// </remarks>
public sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly List<CapturedLine> _lines = [];

    /// <summary>Gets the lines the library wrote, oldest first.</summary>
    public IReadOnlyList<CapturedLine> Lines => [.. _lines];

    /// <summary>Reads back the lines of one event id.</summary>
    /// <param name="eventId">The id the source-generated method carries.</param>
    /// <returns>Those lines, oldest first.</returns>
    public IReadOnlyList<CapturedLine> Of(int eventId) => [.. Lines.Where(line => line.EventId == eventId)];

    public ILogger CreateLogger(string categoryName) => new Recorder(_lines);

    public void AddProvider(ILoggerProvider provider)
    {
        // Nothing routes anywhere else.
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Recorder : ILogger
    {
        private readonly List<CapturedLine> _lines;

        public Recorder(List<CapturedLine> lines) => _lines = lines;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            IReadOnlyList<KeyValuePair<string, object?>> fields =
                state is IReadOnlyList<KeyValuePair<string, object?>> named ? [.. named] : [];

            lock (_lines)
            {
                _lines.Add(new CapturedLine(
                    logLevel, eventId.Id, formatter(state, exception), exception, fields));
            }
        }
    }
}

using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// One line the library wrote.
    /// </summary>
    /// <param name="Level">How serious the line is.</param>
    /// <param name="EventId">The id the source-generated method carries.</param>
    /// <param name="Message">The formatted text.</param>
    /// <param name="Exception">The cause, when the line carried one.</param>
    internal sealed record LogLine(LogLevel Level, int EventId, string Message, Exception? Exception);

    /// <summary>
    /// A logger a test reads back. It keeps every line, in the order the library wrote them.
    /// </summary>
    internal sealed class RecordingLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<LogLine> _lines = [];

        /// <summary>Gets the lines the library wrote, oldest first.</summary>
        public IReadOnlyList<LogLine> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        /// <summary>Reads back the lines of one event id.</summary>
        /// <param name="eventId">The id the source-generated method carries.</param>
        /// <returns>Those lines, oldest first.</returns>
        public IReadOnlyList<LogLine> Of(int eventId)
        {
            return [.. Lines.Where(line => line.EventId == eventId)];
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (_gate)
            {
                _lines.Add(new LogLine(logLevel, eventId.Id, formatter(state, exception), exception));
            }
        }
    }

    /// <summary>
    /// A sink that holds every append open until a test releases it.
    /// </summary>
    internal sealed class BlockingAuditSink : IAuditSinkPort
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly List<AuditEvent> _events = [];

        /// <summary>Completes once the first append is inside the sink.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Gets the events this sink took, in the order they arrived.</summary>
        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (_lock)
                {
                    return [.. _events];
                }
            }
        }

        /// <summary>Lets every open append complete.</summary>
        public void Release()
        {
            _ = _gate.TrySetResult();
        }

        public async ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _events.Add(auditEvent);
            }

            _ = _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A sink that refuses every event.
    /// </summary>
    internal sealed class ThrowingAuditSink : IAuditSinkPort
    {
        /// <summary>The message every refusal carries.</summary>
        public const string Message = "the audit store is unreachable.";

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(Message);
        }
    }
}

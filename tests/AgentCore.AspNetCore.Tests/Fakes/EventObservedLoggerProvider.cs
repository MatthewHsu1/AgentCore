using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// Completes once the connection writes the log line named <c>eventName</c>, and counts how many
    /// times it does.
    /// </summary>
    /// <remarks>
    /// A relay client's <c>SendAsync</c> completing only proves the bytes left the client, not that the
    /// connection has read, dispatched, and finished handling them — the same gap
    /// <see cref="TelnyxRelayHost.WaitForSessionAsync"/> documents for a setup frame. A test that
    /// released a gate on <c>SendAsync</c> alone would be timing delivery, not the connection's own
    /// state, so several tests in this file wait for a named line first. Matched by
    /// <see cref="EventId.Name"/>, the same way <see cref="DtmfObservedLoggerProvider"/> already is,
    /// because none of the words a caller said ever reach a log line to match on instead.
    /// </remarks>
    internal sealed class EventObservedLoggerProvider(string eventName) : ILoggerProvider
    {
        private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once the connection first logs the named line.</summary>
        public Task Observed => _observed.Task;

        private readonly Lock _gate = new();

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return field;
                }
            }

            private set;
        }

        /// <summary>Gets the level of the most recent match, or null before the first one.</summary>
        public LogLevel? Level { get; private set; }

        /// <summary>Gets the rendered text of the most recent match, or null before the first one.</summary>
        public string? Message { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this, eventName);
        }

        public void Dispose()
        {
            // Nothing to release.
        }

        private sealed class Logger(EventObservedLoggerProvider owner, string eventName) : ILogger
        {
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
                if (eventId.Name == eventName)
                {
                    owner.Level = logLevel;
                    owner.Message = formatter(state, exception);
                    lock (owner._gate)
                    {
                        owner.Count++;
                    }

                    _ = owner._observed.TrySetResult();
                }
            }
        }
    }
}

using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>A logger that throws on every warning, so a dispose step that logs one fails.</summary>
    internal sealed class WarningThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Warning;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            throw new InvalidOperationException("the logger is down");
        }
    }
}

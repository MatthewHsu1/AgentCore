using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Ports;

/// <summary>
/// A running telemetry export, held for the life of the process and shut down with it.
/// </summary>
public interface ITelemetrySession : IAsyncDisposable
{
    /// <summary>Gets the provider that exports log lines, or <see langword="null"/> when none does.</summary>
    ILoggerProvider? Logs { get; }
}

using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Sessions;

/// <summary>
/// Runs the idle sweep of <see cref="InMemoryCallSessions"/> for as long as the host is up.
/// </summary>
/// <remarks>
/// Expiry needs something to drive it: a store that only sweeps when it is read never drops the
/// call nobody comes back to, which is the one that grows. A host that registered a session store
/// of its own gets nothing from this — that store owns its own expiry, wherever it keeps them.
/// </remarks>
internal sealed class CallSessionSweeper(
    ICallSessions sessions, TimeProvider timeProvider, ILogger<CallSessionSweeper> logger) : BackgroundService
{
    /// <summary>How often the sweep runs.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sessions is not InMemoryCallSessions memory)
        {
            return;
        }

        using PeriodicTimer timer = new(Interval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // A sweep that throws must not end the loop: the sessions it could not close are still
            // held, and the next tick is the only thing that will try them again.
            try
            {
                await memory.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                CallSessionSweeperLog.SweepFaulted(logger, fault);
            }
        }
    }
}

/// <summary>Every line the idle sweep writes.</summary>
internal static partial class CallSessionSweeperLog
{
    /// <summary>A sweep could not end one or more of the sessions it visited.</summary>
    /// <param name="logger">The logger of the sweeper.</param>
    /// <param name="exception">The cause, or the causes of a sweep that met more than one.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "the idle sweep could not end every session it visited, so those sessions are still held.")]
    public static partial void SweepFaulted(ILogger logger, Exception exception);
}

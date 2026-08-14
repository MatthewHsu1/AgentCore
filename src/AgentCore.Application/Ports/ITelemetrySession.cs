using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Ports;

/// <summary>
/// A running telemetry export, held for the life of the process and shut down with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a session and not a registration.</b> The other three vendor seams answer a
/// question the library asks: a chat client replies, a knowledge store searches, a moderation
/// evaluator judges. Telemetry is the opposite direction. Nothing in the library ever calls this
/// port. The library writes to the <c>ActivitySource</c> and the <c>Meter</c> that
/// <c>AgentCoreTelemetry</c> owns, and an exporter <i>listens</i> to those two names. So an adapter
/// here starts a listener and holds it, and the only thing it hands back is the one piece the
/// library cannot listen for on its own: the log provider.
/// </para>
/// <para>
/// That shape is also what keeps the container out of this project. Registering an exporter the
/// usual way needs <c>IServiceCollection</c>, which would put a dependency-injection type in the
/// ports layer. An adapter builds its providers standalone instead, keeps them in the object it
/// returns, and disposal is what flushes and stops them.
/// </para>
/// <para>
/// <b>Disposal must flush.</b> A process that stops holds a batch of spans and measurements that
/// were never sent. An implementation therefore drains on the way out, and the composition root
/// hands this to the container so the host shuts it down rather than the process simply exiting.
/// </para>
/// </remarks>
public interface ITelemetrySession : IAsyncDisposable
{
    /// <summary>Gets the provider that exports log lines, or <see langword="null"/> when none does.</summary>
    /// <remarks>
    /// <para>
    /// The composition root adds this to the host's <c>ILoggerFactory</c>, beside whatever the host
    /// already bound. It is added and never substituted: a deployment that reads its console keeps
    /// reading its console, and the same line also reaches the collector.
    /// </para>
    /// <para>
    /// <see langword="null"/> means this adapter exports traces and metrics only. That is a normal
    /// answer, not a failure, and the console stays the one place log lines land.
    /// </para>
    /// <para>
    /// The provider is owned by this session and disposed with it. The composition root never
    /// disposes it directly.
    /// </para>
    /// </remarks>
    ILoggerProvider? Logs { get; }
}

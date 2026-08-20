using AgentCore.Application.Audit;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Assembles the readings of one call, in the order every call then pays for.
/// </summary>
/// <remarks>
/// <para>
/// The composition root resolves an <see cref="IAuditSinkPort"/> — always one, since
/// <c>providers.audit</c> falls back to the in-process <c>memory</c> kind — and an
/// <see cref="ILogger"/>, and knows nothing of <see cref="ICallObserver"/>. This is what turns them
/// into the telemetry, logging, and audit readings of one fact. It is composition, so it belongs to
/// the composition root and not to <see cref="CallSessionFactory"/>: the factory is handed the
/// finished list and holds no opinion about what is in it.
/// </para>
/// <para>
/// The observers it builds are shared and hold no per-call state, so one list serves every call.
/// The ordering guarantee is not here either — each session gets a
/// <see cref="CallObserverDispatcher"/> of its own, because that guarantee is per instance and a
/// shared one would make every call queue behind every other.
/// </para>
/// </remarks>
public static class CallObservers
{
    /// <summary>Builds the list one host's bindings ask for.</summary>
    /// <param name="auditSink">
    /// The sink the chain of D23 is appended to. There is always one: the composition root reads
    /// <c>providers.audit</c>, which names the in-process <c>memory</c> kind when the document names
    /// no vendor, so a host that configures nothing still hands a real sink down rather than a hole.
    /// One sink serves every call, because a session names itself on every event.
    /// </param>
    /// <param name="logger">
    /// The logger the three "log once" rows of section 8.7 write to, or <see langword="null"/> for a
    /// logger that writes nowhere. The library never throws for want of one.
    /// </param>
    /// <param name="hostObservers">
    /// The host's own readings of a call, or <see langword="null"/> for a host that adds none.
    /// </param>
    /// <returns>The readings, in the order the dispatcher offers each fact to them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="auditSink"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ICallObserver> Standard(
        IAuditSinkPort auditSink,
        ILogger? logger,
        IEnumerable<ICallObserver>? hostObservers = null)
    {
        ArgumentNullException.ThrowIfNull(auditSink);

        // <b>The sink goes LAST.</b> The dispatcher offers one fact to each observer in turn, in
        // this order, and that order is what a reading of the same fact costs the turn: the counters
        // of section 8.6 and the rows of section 8.7 are taken above the enqueue, exactly as the old
        // turn loop took them. The counter and the line always answer at once; a sink is the one that
        // may not, and a durable insert is measured at 13 ms p50 and 32 ms p99 in section 7.
        //
        // What the order no longer decides is what a LATER fact costs. Each observer has a tail of
        // its own inside the dispatcher, so a sink still writing an earlier row holds up nothing but
        // its own next row: telemetry and logging keep their old synchronous cost on every event
        // whatever the sink is doing, and the remarks on TelemetryCallObserver still hold that a kind
        // is counted whatever the sink then does with it. Order is guaranteed per observer, which is
        // all the chain of D23 needs, and the slow one pays for itself alone, off the turn.
        //
        // All three readings are unconditional. The counters of section 8.6 and the rows of section
        // 8.7 are this library's own and no host opts out of them, and the audit reading is now in
        // the same position: the composition root resolves providers.audit into a sink for every
        // host, falling back to the in-process memory kind when the document names no vendor, so
        // there is no longer a "bound nothing" shape for this list to have. The chain of D23 is
        // therefore written on every call, and a host that wants it durable changes where it goes,
        // not whether it is taken.
        ICallObserver[] library =
            [new TelemetryCallObserver(), new LoggingCallObserver(logger), new AuditCallObserver(auditSink)];

        // <b>The host's own observers go after all three.</b> An observer bound through
        // AgentCoreOptions.UseObservers is code this library did not write and cannot measure, and
        // the head of a delivery — the work before the first await — is the one part every observer
        // still runs one after another. Putting the host last is what keeps the counters, the rows,
        // and the enqueue at exactly the cost they had before any host bound anything. Past that head
        // the order buys nothing and costs nothing: each observer has a tail of its own.
        return hostObservers is null ? library : [.. library, .. hostObservers];
    }
}

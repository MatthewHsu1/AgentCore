using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Builds one <see cref="CallSession"/> for each call, over one compiled agent.
/// </summary>
/// <remarks>
/// <para>
/// Everything this factory holds is shared and read-only for the life of the process: the compiled
/// agent of T44, the guard evaluator, the extractor, and the clock. Everything the session holds
/// belongs to one call. Register this factory once, and ask it for a session each time a call
/// arrives.
/// </para>
/// <para>
/// The constructor stays plain on purpose. It reads no service provider and no configuration source,
/// so the host owns the registration and this class owns nothing but the seam.
/// </para>
/// <para>
/// It is also where the observers of a call are assembled. The host still binds an
/// <see cref="IAuditSinkPort"/> and an <see cref="ILogger"/> and knows nothing of
/// <see cref="ICallObserver"/>; this factory turns them into the audit, telemetry, and logging
/// readings of one fact. The observers themselves are shared and hold no per-call state, but each
/// session gets a <see cref="CallObserverDispatcher"/> of its own, because the dispatcher's ordering
/// guarantee is per instance and a shared one would make every call queue behind every other.
/// </para>
/// </remarks>
public sealed class CallSessionFactory : ICallSessionFactory
{
    private readonly CompiledAgent _compiled;
    private readonly IGuardEvaluator _guards;
    private readonly StateExtractor? _extractor;
    private readonly TimeProvider _time;
    private readonly ICallObserver[] _observers;
    private readonly ILogger? _logger;
    private readonly PromptModerator? _moderation;

    /// <summary>Creates the factory.</summary>
    /// <param name="compiled">The compiled agent. Every call shares it.</param>
    /// <param name="guards">The evaluator that runs each exit guard and each increment rule.</param>
    /// <param name="extractor">
    /// The extractor, or <see langword="null"/> when the document declares none.
    /// <see cref="CreateExtractor"/> builds it from a chat client factory.
    /// </param>
    /// <param name="timeProvider">
    /// The clock the reserved <c>callDurationSeconds</c> slot reads, or <see langword="null"/> for
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    /// <param name="auditSink">
    /// The sink the chain of D23 is appended to, or <see langword="null"/> for a sink that writes
    /// nowhere. One sink serves every call, because a session names itself on every event.
    /// </param>
    /// <param name="logger">
    /// The logger the three "log once" rows of section 8.7 write to, or <see langword="null"/> for a
    /// logger that writes nowhere. The library never throws for want of one.
    /// </param>
    /// <param name="moderation">
    /// The moderator that reads what the caller said before the model runs, or
    /// <see langword="null"/> for a host that moderates nothing.
    /// <see cref="PromptModerator.FromRegistry"/> builds it from the evaluator registry. It holds no
    /// per-call state, so one instance serves every call.
    /// </param>
    public CallSessionFactory(
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor = null,
        TimeProvider? timeProvider = null,
        IAuditSinkPort? auditSink = null,
        ILogger? logger = null,
        PromptModerator? moderation = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);

        _compiled = compiled;
        _guards = guards;
        _extractor = extractor;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _moderation = moderation;

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
        // A host that bound no sink gets no audit observer at all: there is nothing to write to, so
        // nothing is built rather than a sink that drops what it is handed. Both other readings are
        // always present, because the counters and the rows are this library's own and no host opts
        // out of them.
        _observers = auditSink is null
            ? [new TelemetryCallObserver(), new LoggingCallObserver(logger)]
            : [new TelemetryCallObserver(), new LoggingCallObserver(logger), new AuditCallObserver(auditSink)];
    }

    /// <summary>Builds the extractor one document declares.</summary>
    /// <param name="compiled">The compiled agent.</param>
    /// <param name="chatClients">The seam that resolves <c>extractor.model</c>.</param>
    /// <returns>The extractor, or <see langword="null"/> when the document declares none.</returns>
    /// <remarks>
    /// The extractor holds no per-call state, so one instance serves every call. It takes the state
    /// document of the call as an argument instead.
    /// </remarks>
    public static StateExtractor? CreateExtractor(CompiledAgent compiled, IChatClientFactory chatClients)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(chatClients);

        return compiled.Configuration.Extractor is { } declared
            ? new StateExtractor(compiled.Configuration, chatClients.GetChatClient(declared.Model))
            : null;
    }

    /// <inheritdoc />
    public CallSession Create(string? callId = null)
        => new(
            string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId,
            _compiled,
            _guards,
            _extractor,
            _time,

            // One dispatcher for each session, over the shared observers. See the remarks above.
            new CallObserverDispatcher(_observers, _logger),
            _moderation);
}

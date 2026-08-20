using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
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
/// It does not assemble the observers of a call. <see cref="CallObservers.Standard"/> does, and the
/// finished list is handed in, so this factory holds no opinion about which readings exist or in
/// what order. The observers are shared and hold no per-call state, but each session gets a
/// <see cref="CallObserverDispatcher"/> of its own, because the dispatcher's ordering guarantee is
/// per instance and a shared one would make every call queue behind every other.
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
    /// <param name="logger">
    /// The logger the <see cref="CallObserverDispatcher"/> of each session reports a failed observer
    /// to, or <see langword="null"/> for a logger that writes nowhere. The library never throws for
    /// want of one.
    /// </param>
    /// <param name="observers">
    /// The readings of a call, in the order the dispatcher offers each fact to them, or
    /// <see langword="null"/> for a host that wants none at all.
    /// <see cref="CallObservers.Standard"/> builds the list this library expects. Each observer
    /// holds no per-call state, so one instance serves every call.
    /// </param>
    public CallSessionFactory(
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        IEnumerable<ICallObserver>? observers = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);

        _compiled = compiled;
        _guards = guards;
        _extractor = extractor;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;

        // Copied, not held: the list is the caller's, and a caller that keeps adding to it after this
        // must not change what a session already built. The order is the caller's too — see
        // CallObservers.Standard, which is where the cost of that order is argued.
        _observers = observers is null ? [] : [.. observers];
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
            new CallObserverDispatcher(_observers, _logger));
}

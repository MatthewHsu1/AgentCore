using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Builds one <see cref="CallSession"/> for each call, over one compiled agent.
/// </summary>
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
    public static StateExtractor? CreateExtractor(CompiledAgent compiled, IChatClientFactory chatClients)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(chatClients);

        return compiled.Configuration.Extractor is { } declared
            ? new StateExtractor(
                compiled.Configuration,
                chatClients.GetChatClient(declared.Model)
                           .AsBuilder()
                           .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                           .Build())
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

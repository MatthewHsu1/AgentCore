using AgentCore.Application.Calls;
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
                           .Use(static innerClient => new ModelFacingChatClient(innerClient))
                           .Build())
            : null;
    }

    /// <inheritdoc />
    public CallSession Create(string? callId = null, CallSessionState? state = null)
    {
        CallSession session = new(
            string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId,
            _compiled,
            _guards,
            _extractor,
            _time,
            new CallObserverDispatcher(_observers, _logger),
            _logger);

        // Named, not applied. The session resumes on its first turn, where store 0's own copy
        // outranks this one — see the remarks on CallSession.Resume.
        if (state is not null)
        {
            session.Resume(state);
        }

        return session;
    }
}

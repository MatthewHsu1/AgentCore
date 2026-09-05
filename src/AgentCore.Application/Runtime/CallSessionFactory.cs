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

    private readonly VocabularyCache? _vocabulary;

    /// <summary>Creates the factory.</summary>
    public CallSessionFactory(
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        IEnumerable<ICallObserver>? observers = null,
        VocabularyCache? vocabulary = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);

        _compiled = compiled;
        _guards = guards;
        _extractor = extractor;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _vocabulary = vocabulary;

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
        => CreateExtractor(compiled, chatClients, new StateValueLinkers([]));

    /// <summary>Builds the extractor one document declares, over the linkers a host bound with <c>UseStateValueLinkers</c>.</summary>
    /// <param name="compiled">The compiled agent.</param>
    /// <param name="chatClients">The seam that resolves <c>extractor.model</c>.</param>
    /// <param name="linkers">
    /// The linkers to resolve each <c>vocabulary:</c> slot's <c>vocabulary.linker</c> name against,
    /// beyond the built-in <c>exact</c>, or <see langword="null"/> for none. <c>StateValueLinkers</c>,
    /// the registry type this builds, is <see langword="internal"/> to this assembly — this overload
    /// is the public seam a host-facing project such as AspNetCore reaches it through, mirroring how
    /// <see cref="CallSessionFactory"/>'s own constructor already takes the vocabulary cache by its
    /// public type rather than an internal one.
    /// </param>
    /// <returns>The extractor, or <see langword="null"/> when the document declares none.</returns>
    public static StateExtractor? CreateExtractor(
        CompiledAgent compiled, IChatClientFactory chatClients, IEnumerable<IStateValueLinker>? linkers)
        => CreateExtractor(compiled, chatClients, new StateValueLinkers(linkers ?? []));

    /// <summary>Builds the extractor one document declares, over a <c>vocabulary.linker</c> registry (K12).</summary>
    /// <param name="compiled">The compiled agent.</param>
    /// <param name="chatClients">The seam that resolves <c>extractor.model</c>.</param>
    /// <param name="linkers">The registry the extractor resolves each <c>vocabulary:</c> slot's linker against.</param>
    /// <returns>The extractor, or <see langword="null"/> when the document declares none.</returns>
    internal static StateExtractor? CreateExtractor(CompiledAgent compiled, IChatClientFactory chatClients, StateValueLinkers linkers)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(chatClients);
        ArgumentNullException.ThrowIfNull(linkers);

        return compiled.Configuration.Extractor is { } declared
            ? new StateExtractor(
                compiled.Configuration,
                chatClients.GetChatClient(declared.Model)
                           .AsBuilder()
                           .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                           .Use(static innerClient => new ModelFacingChatClient(innerClient))
                           .Build(),
                linkers)
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
            _logger,
            _vocabulary);

        // Named, not applied. The session resumes on its first turn, where store 0's own copy
        // outranks this one — see the remarks on CallSession.Resume.
        if (state is not null)
        {
            session.Resume(state);
        }

        return session;
    }
}

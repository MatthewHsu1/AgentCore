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
/// </remarks>
public sealed class CallSessionFactory : ICallSessionFactory
{
    private readonly CompiledAgent _compiled;
    private readonly IGuardEvaluator _guards;
    private readonly StateExtractor? _extractor;
    private readonly TimeProvider _time;
    private readonly IAuditSinkPort? _audit;
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
        _audit = auditSink;
        _logger = logger;
        _moderation = moderation;
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
            _audit,
            _logger,
            _moderation);
}

using AgentCore.Application.Conversation;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Builds one <see cref="ConversationSession"/> for each conversation, over one compiled agent.
/// </summary>
public sealed class ConversationSessionFactory : IConversationSessionFactory
{
    private readonly ConversationSessionSeams _seams;

    private readonly IConversationObserver[] _observers;

    private readonly string? _workspaceRoot;

    /// <summary>
    /// Creates the factory. When <paramref name="workspaceRoot"/> is bound, every conversation this factory
    /// builds gets its own folder under it; unbound, a session it builds has no workspace.
    /// </summary>
    public ConversationSessionFactory(
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        IEnumerable<IConversationObserver>? observers = null,
        string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);

        _seams = new ConversationSessionSeams(compiled, guards, extractor, timeProvider ?? TimeProvider.System, logger);
        _workspaceRoot = workspaceRoot;

        // Copied, not held: the list is the caller's, and a caller that keeps adding to it after this
        // must not change what a session already built. The order is the caller's too — see
        // ConversationObservers.Standard, which is where the cost of that order is argued.
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
    public ConversationSession Create(string? conversationId = null, ConversationSessionState? state = null)
    {
        var resolvedConversationId = string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString("N") : conversationId;

        var workspace = _workspaceRoot is null ? null : ConversationWorkspace.Create(_workspaceRoot, resolvedConversationId);

        ConversationSession session = new(
            resolvedConversationId,
            _seams,
            new ConversationObserverDispatcher(_observers, _seams.Logger),
            workspace);

        // Named, not applied. The session resumes on its first turn, where store 0's own copy
        // outranks this one — see the remarks on ConversationSession.Resume.
        if (state is not null)
        {
            session.Resume(state);
        }

        return session;
    }
}

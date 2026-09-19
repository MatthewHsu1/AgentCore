using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace AgentCore.AspNetCore.Sessions;

/// <summary>
/// The framework's session seam over the continuation map that lives in the conversation store.
/// </summary>
public sealed class AgentCoreAgentSessionStore : AgentSessionStore
{
    private readonly IConversationStore _conversations;

    /// <summary>Creates the seam over one conversation store.</summary>
    /// <param name="conversations">The store the continuation ids open onto.</param>
    /// <exception cref="ArgumentNullException">The store is <see langword="null"/>.</exception>
    public AgentCoreAgentSessionStore(IConversationStore conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        _conversations = conversations;
    }

    /// <summary>Reads whether anything is filed under one continuation id.</summary>
    /// <param name="sessionStoreId">The conversation id or response id to look up.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when a later load would find an envelope.</returns>
    public async ValueTask<bool> ContainsAsync(string sessionStoreId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStoreId);

        return await _conversations.GetContinuationAsync(sessionStoreId, cancellationToken).ConfigureAwait(false)
            is not null;
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(
        AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(sessionStoreId);
        ArgumentNullException.ThrowIfNull(session);

        var envelope = await agent
            .SerializeSessionAsync(session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _conversations.SaveContinuationAsync(sessionStoreId, envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(sessionStoreId);

        var envelope = await _conversations.GetContinuationAsync(sessionStoreId, cancellationToken).ConfigureAwait(false);
        if (envelope is null)
        {
            return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await agent
            .DeserializeSessionAsync(envelope.Value, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask DeleteSessionAsync(
        AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(sessionStoreId);

        return _conversations.DeleteContinuationAsync(sessionStoreId, cancellationToken);
    }
}

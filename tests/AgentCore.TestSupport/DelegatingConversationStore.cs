using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// A store that forwards every call to another one, so a fake overrides only what it is testing.
/// </summary>
/// <param name="inner">Where an un-overridden conversation goes.</param>
public abstract class DelegatingConversationStore(IConversationStore inner) : IConversationStore
{
    /// <summary>Gets the store behind this one.</summary>
    protected IConversationStore Inner { get; } = inner;

    /// <inheritdoc />
    public virtual ValueTask<ConversationRecord> CreateAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => Inner.CreateAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<ConversationRecord?> GetAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => Inner.GetAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<ConversationPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        ConversationStatus? status = null,
        CancellationToken cancellationToken = default)
        => Inner.ListAsync(principalKey, after, limit, status, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask RenameAsync(
        string conversationId, string title, CancellationToken cancellationToken = default)
        => Inner.RenameAsync(conversationId, title, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SetStatusAsync(
        string conversationId, ConversationStatus status, CancellationToken cancellationToken = default)
        => Inner.SetStatusAsync(conversationId, status, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SetCustomAsync(
        string conversationId, JsonElement? custom, CancellationToken cancellationToken = default)
        => Inner.SetCustomAsync(conversationId, custom, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SetExternalIdAsync(
        string conversationId, string? externalId, CancellationToken cancellationToken = default)
        => Inner.SetExternalIdAsync(conversationId, externalId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
        => Inner.DeleteAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
        string conversationId,
        IReadOnlyList<ConversationMessageDraft> messages,
        ConversationSessionState? state = null,
        CancellationToken cancellationToken = default)
        => Inner.AppendAsync(conversationId, messages, state, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask RewriteAsync(
        string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        => Inner.RewriteAsync(conversationId, messageId, content, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => Inner.ReadForSessionAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(
        string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
        => Inner.ReadWindowAsync(conversationId, window, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<int?> OrdinalOfAsync(
        string conversationId, string messageId, CancellationToken cancellationToken = default)
        => Inner.OrdinalOfAsync(conversationId, messageId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<ConversationCut> TruncateAsync(
        string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
        => Inner.TruncateAsync(conversationId, fromOrdinal, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<int> EraseAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => Inner.EraseAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<int> SweepAsync(
        TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
        => Inner.SweepAsync(retention, batchSize, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask AttachPrincipalAsync(
        string conversationId, string principalKey, string role, CancellationToken cancellationToken = default)
        => Inner.AttachPrincipalAsync(conversationId, principalKey, role, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask DetachPrincipalAsync(
        string conversationId, string principalKey, CancellationToken cancellationToken = default)
        => Inner.DetachPrincipalAsync(conversationId, principalKey, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SaveContinuationAsync(
        string continuationId, JsonElement envelope, CancellationToken cancellationToken = default)
        => Inner.SaveContinuationAsync(continuationId, envelope, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<JsonElement?> GetContinuationAsync(
        string continuationId, CancellationToken cancellationToken = default)
        => Inner.GetContinuationAsync(continuationId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask DeleteContinuationAsync(
        string continuationId, CancellationToken cancellationToken = default)
        => Inner.DeleteContinuationAsync(continuationId, cancellationToken);
}

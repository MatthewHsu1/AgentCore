using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// The door a host or a consumer reads and writes stored conversations through: rows, words, and the
/// files a conversation published, together. What the turn loop and the sweeper need beyond this stays
/// on <see cref="IConversationStore"/>, which is the adapter seam and not a consumer surface.
/// </summary>
public interface IConversations
{
    /// <inheritdoc cref="IConversationStore.CreateAsync"/>
    ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.GetAsync"/>
    ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Reads one conversation whole: its row, its words, and a link to every file it kept.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The conversation, or <see langword="null"/> when the store holds none under that id.</returns>
    ValueTask<StoredConversation?> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.ListAsync"/>
    ValueTask<ConversationPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        ConversationStatus? status = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.RenameAsync"/>
    ValueTask RenameAsync(string conversationId, string title, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.SetStatusAsync"/>
    ValueTask SetStatusAsync(string conversationId, ConversationStatus status, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.SetCustomAsync"/>
    ValueTask SetCustomAsync(string conversationId, JsonElement? custom, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.SetExternalIdAsync"/>
    ValueTask SetExternalIdAsync(string conversationId, string? externalId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the conversation: its row, its words, its claims, and every file it kept.</summary>
    /// <param name="conversationId">The conversation to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.AppendMessageAsync"/>
    ValueTask<ConversationMessage> AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.ReadAsync"/>
    ValueTask<IReadOnlyList<ConversationMessage>> ReadAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.EraseAsync"/>
    ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.AttachPrincipalAsync"/>
    ValueTask AttachPrincipalAsync(string conversationId, string principalKey, string role, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IConversationStore.DetachPrincipalAsync"/>
    ValueTask DetachPrincipalAsync(string conversationId, string principalKey, CancellationToken cancellationToken = default);
}

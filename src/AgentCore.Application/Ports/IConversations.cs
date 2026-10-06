using AgentCore.Application.Blobs;
using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// The door a host or a consumer reads and writes stored conversations through.
    /// </summary>
    public interface IConversations
    {
        /// <inheritdoc cref="IConversationStore.CreateAsync"/>
        ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default);

        /// <inheritdoc cref="IConversationStore.GetAsync"/>
        ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default);

        /// <summary>Reads one window of a conversation: its row, the newest turns before a given one, and the facts of every file those turns kept.</summary>
        /// <param name="conversationId">The conversation to read.</param>
        /// <param name="window">Which turns: how many, and before which.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>
        /// The window, with <see cref="StoredConversation.OlderBefore"/> set when an older one may exist, or
        /// <see langword="null"/> when the store holds no conversation under that id.
        /// </returns>
        ValueTask<StoredConversation?> LoadWindowAsync(string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default);

        /// <summary>
        /// Makes a fresh, short-lived link to one file a conversation kept. Call it when the person asks for the file.
        /// A host that proxies the bytes itself, or needs a different lifetime, goes through <see cref="IBlobStore"/> instead.
        /// </summary>
        /// <param name="conversationId">The conversation that owns the file. The caller has already checked who may read it.</param>
        /// <param name="name">The file's name, as <see cref="StoredConversation.Files"/> lists it.</param>
        /// <param name="cancellationToken">Cancels the lookup and the signing.</param>
        /// <returns>
        /// The URL, or <see langword="null"/> when the host opened no blob store, the conversation id fails
        /// <see cref="BlobOwner.IsSafe"/> or the name fails <see cref="BlobName.IsSafe"/> (blank included; this never throws for either), the store holds no blob under that owner and name, or the store has no web door.
        /// </returns>
        ValueTask<Uri?> LinkFileAsync(string conversationId, string name, CancellationToken cancellationToken = default);

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

        /// <inheritdoc cref="IConversationStore.EraseAsync"/>
        ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default);

        /// <inheritdoc cref="IConversationStore.AttachPrincipalAsync"/>
        ValueTask AttachPrincipalAsync(string conversationId, string principalKey, string role, CancellationToken cancellationToken = default);

        /// <inheritdoc cref="IConversationStore.DetachPrincipalAsync"/>
        ValueTask DetachPrincipalAsync(string conversationId, string principalKey, CancellationToken cancellationToken = default);

        /// <inheritdoc cref="IConversationStore.SweepAsync"/>
        ValueTask<int> SweepAsync(
            TimeSpan retention,
            int batchSize = 500,
            CancellationToken cancellationToken = default);
    }
}

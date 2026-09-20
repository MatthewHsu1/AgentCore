using System.Text.Json;
using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Conversation;

/// <summary>
/// Everything a host or a consumer does with a stored conversation: its row, its words, and the files it
/// published. The one door; the stores behind it are adapters nobody else needs to name. A consumer
/// takes it as <see cref="IConversations"/>; the turn loop and the sweeper take it as
/// <see cref="IConversationStore"/>.
/// </summary>
public sealed class Conversations : IConversations, IConversationStore
{
    private readonly IConversationStore _conversations;

    private readonly IBlobStore? _blobs;

    /// <summary>Makes the door over the stores the document opened.</summary>
    /// <param name="conversations">The store the rows and words live in.</param>
    /// <param name="blobs">The store the published files live in, or <see langword="null"/> when the document names none.</param>
    public Conversations(IConversationStore conversations, IBlobStore? blobs)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        _conversations = conversations;
        _blobs = blobs;
    }

    /// <summary>The adapter the rows live in. For a host checking which vendor it opened; go through the door for everything else.</summary>
    public IConversationStore Store => _conversations;

    /// <inheritdoc />
    public async ValueTask<StoredConversation?> LoadWindowAsync(
        string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var record = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        var messages = await _conversations.ReadWindowAsync(conversationId, window, cancellationToken).ConfigureAwait(false);
        
        var files = await LinkFilesAsync(conversationId, messages.Select(message => message.Content), cancellationToken).ConfigureAwait(false);

        var turns = messages.Select(message => message.TurnIndex).Distinct().Count();

        return new StoredConversation(record, messages, files)
        {
            OlderBefore = turns == window.Turns ? messages.Min(message => message.TurnIndex) : null,
        };
    }

    /// <summary>Links every published file the messages carry and the store kept, each name once, in first-seen order.</summary>
    /// <param name="conversationId">The conversation that owns the files.</param>
    /// <param name="messages">The messages to read the references off: the stored transcript, or one turn's updates.</param>
    /// <param name="cancellationToken">Cancels the signing.</param>
    /// <returns>One link per kept file.</returns>
    public async Task<IReadOnlyList<FileLink>> LinkFilesAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        if (_blobs is null)
        {
            return [];
        }

        List<BlobRef> kept = [];

        foreach (var file in messages.SelectMany(message => message.Contents).OfType<FileContent>())
        {
            if (!file.Kept || !BlobName.IsSafe(file.Name))
            {
                continue;
            }

            BlobRef blob = new(conversationId, file.Name, file.MediaType, file.Length);
            var at = kept.FindIndex(known => string.Equals(known.Name, blob.Name, StringComparison.Ordinal));

            if (at >= 0)
            {
                kept[at] = blob;
            }
            else
            {
                kept.Add(blob);
            }
        }

        List<FileLink> links = [];

        foreach (var blob in kept)
        {
            var url = await _blobs.LinkAsync(blob, BlobLink.Lifetime, cancellationToken).ConfigureAwait(false);
            links.Add(new FileLink(blob, url));
        }

        return links;
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_blobs is not null)
        {
            await _blobs.DeleteByOwnerAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }

        await _conversations.DeleteAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
        => _conversations.CreateAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
        => _conversations.GetAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConversationPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        ConversationStatus? status = null,
        CancellationToken cancellationToken = default)
        => _conversations.ListAsync(principalKey, after, limit, status, cancellationToken);

    /// <inheritdoc />
    public ValueTask RenameAsync(string conversationId, string title, CancellationToken cancellationToken = default)
        => _conversations.RenameAsync(conversationId, title, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetStatusAsync(string conversationId, ConversationStatus status, CancellationToken cancellationToken = default)
        => _conversations.SetStatusAsync(conversationId, status, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetCustomAsync(string conversationId, JsonElement? custom, CancellationToken cancellationToken = default)
        => _conversations.SetCustomAsync(conversationId, custom, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetExternalIdAsync(string conversationId, string? externalId, CancellationToken cancellationToken = default)
        => _conversations.SetExternalIdAsync(conversationId, externalId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
        string conversationId,
        IReadOnlyList<ConversationMessageDraft> messages,
        ConversationSessionState? state = null,
        CancellationToken cancellationToken = default)
        => _conversations.AppendAsync(conversationId, messages, state, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConversationMessage> AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken cancellationToken = default)
        => _conversations.AppendMessageAsync(conversationId, message, cancellationToken);

    /// <inheritdoc />
    public ValueTask RewriteAsync(string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        => _conversations.RewriteAsync(conversationId, messageId, content, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(string conversationId, CancellationToken cancellationToken = default)
        => _conversations.ReadForSessionAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
        => _conversations.ReadWindowAsync(conversationId, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int?> OrdinalOfAsync(string conversationId, string messageId, CancellationToken cancellationToken = default)
        => _conversations.OrdinalOfAsync(conversationId, messageId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConversationCut> TruncateAsync(string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
        => _conversations.TruncateAsync(conversationId, fromOrdinal, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
        => _conversations.EraseAsync(conversationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> SweepAsync(TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
        => _conversations.SweepAsync(retention, batchSize, cancellationToken);

    /// <inheritdoc />
    public ValueTask AttachPrincipalAsync(string conversationId, string principalKey, string role, CancellationToken cancellationToken = default)
        => _conversations.AttachPrincipalAsync(conversationId, principalKey, role, cancellationToken);

    /// <inheritdoc />
    public ValueTask DetachPrincipalAsync(string conversationId, string principalKey, CancellationToken cancellationToken = default)
        => _conversations.DetachPrincipalAsync(conversationId, principalKey, cancellationToken);

    /// <inheritdoc />
    public ValueTask SaveContinuationAsync(string continuationId, JsonElement envelope, CancellationToken cancellationToken = default)
        => _conversations.SaveContinuationAsync(continuationId, envelope, cancellationToken);

    /// <inheritdoc />
    public ValueTask<JsonElement?> GetContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
        => _conversations.GetContinuationAsync(continuationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask DeleteContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
        => _conversations.DeleteContinuationAsync(continuationId, cancellationToken);
}

using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>Where store 0 keeps what a conversation is, apart from its words.</summary>
public interface IConversationStore
{
    /// <summary>Makes the conversation's row, or returns the one already there.</summary>
    /// <param name="conversationId">The conversation to record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The row, whether this conversation made it or found it.</returns>
    ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Reads one conversation's row.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/> when store 0 holds none.</returns>
    ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Lists one principal's conversations, most recently active first.</summary>
    /// <param name="principalKey">The opaque key to list by.</param>
    /// <param name="after">A cursor from an earlier page, or <see langword="null"/> for the first.</param>
    /// <param name="limit">How many rows this page may hold.</param>
    /// <param name="status">The one status to return, or <see langword="null"/> for every status.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page, and a cursor when a following page exists.</returns>
    ValueTask<ConversationPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        ConversationStatus? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a conversation's title.</summary>
    /// <param name="conversationId">The conversation to rename.</param>
    /// <param name="title">What to show in a list.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RenameAsync(string conversationId, string title, CancellationToken cancellationToken = default);

    /// <summary>Archives a conversation, or brings it back.</summary>
    /// <param name="conversationId">The conversation to move.</param>
    /// <param name="status">Where to move it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetStatusAsync(string conversationId, ConversationStatus status, CancellationToken cancellationToken = default);

    /// <summary>Replaces a conversation's consumer-owned fields.</summary>
    /// <param name="conversationId">The conversation to write.</param>
    /// <param name="custom">The fields, or <see langword="null"/> to clear them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetCustomAsync(string conversationId, JsonElement? custom, CancellationToken cancellationToken = default);

    /// <summary>Replaces a conversation's consumer-owned id.</summary>
    /// <param name="conversationId">The conversation to write.</param>
    /// <param name="externalId">The consumer's own id for the conversation, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetExternalIdAsync(string conversationId, string? externalId, CancellationToken cancellationToken = default);

    /// <summary>Erases the conversation's row and every attachment to it.</summary>
    /// <param name="conversationId">The conversation to erase.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Writes a turn's new messages, and the state the session holds after them.</summary>
    /// <param name="conversationId">The conversation every message in <paramref name="messages"/> belongs to.</param>
    /// <param name="messages">The rows to write, oldest first.</param>
    /// <param name="state">
    /// What the session holds after this turn, or <see langword="null"/> to leave the stored state
    /// alone. It rides with the words on purpose: a crash between the two would leave the stage
    /// behind the words it belongs to. For the same reason, a non-<see langword="null"/> state is
    /// silently dropped when <paramref name="messages"/> is empty: an empty turn writes no words for
    /// it to ride with, so there is nothing to write it beside.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The rows as written, in the order given, each with its ordinal and its turn index.
    /// </returns>
    /// <remarks>
    /// The store numbers every row from the conversation's own counter, in one atomic step, so two writers on
    /// two machines never collide on an ordinal — one from the conversation's live session, the other from a
    /// host appending outside any turn.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The conversation named by <paramref name="conversationId"/> does not exist. A row is never written against a
    /// conversation that has no row of its own.
    /// </exception>
    ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
        string conversationId,
        IReadOnlyList<ConversationMessageDraft> messages,
        ConversationSessionState? state = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends one message to a conversation from outside any turn. The store numbers the row and stamps it
    /// with the turn the conversation takes next. Works whether or not a session for the conversation is live.
    /// </summary>
    /// <param name="conversationId">The conversation to append to.</param>
    /// <param name="message">The message to append.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The row as written: its ordinal, its turn index, and its id.</returns>
    async ValueTask<ConversationMessage> AppendMessageAsync(
        string conversationId,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var messageId = string.IsNullOrEmpty(message.MessageId) ? ConversationMessageIds.New() : message.MessageId;
        ConversationMessageDraft draft = new(TurnIndex: null, message, messageId);

        var rows = await AppendAsync(conversationId, [draft], state: null, cancellationToken).ConfigureAwait(false);
        return rows[0];
    }

    /// <summary>Rewrites one already-written message in place, on a barge-in.</summary>
    /// <param name="conversationId">The conversation the message belongs to.</param>
    /// <param name="messageId">
    /// The message to rewrite, by name rather than by ordinal: the session's in-memory ordinal is
    /// still provisional while a turn runs, but the message id is fixed the moment the row is cut.
    /// </param>
    /// <param name="content">What the caller actually heard.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RewriteAsync(
        string conversationId,
        string messageId,
        ChatMessage content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a conversation's words as its session opens them: the newest summary row, and every row
    /// above what it covers. The rows a summary stands in for are not read. With no summary, every row.
    /// This is the one read that returns a summary row.
    /// </summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows, oldest ordinal first. The summary sits at its own ordinal, above the rows it covers.</returns>
    ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the newest turns of a conversation before a given one, oldest message first. Summary rows are left out.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="window">Which turns: how many, and before which.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every message of those turns, or an empty list when none fall in the window.</returns>
    ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(
        string conversationId,
        TranscriptWindow window,
        CancellationToken cancellationToken = default);

    /// <summary>Finds the ordinal of one message somebody said. Summary rows are not found.</summary>
    /// <param name="conversationId">The conversation to search.</param>
    /// <param name="messageId">The message to find.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The ordinal, or <see langword="null"/> when the conversation holds no such message.</returns>
    ValueTask<int?> OrdinalOfAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws the tail of a conversation's words, from one ordinal onward. A summary row goes
    /// only when the cut reaches a row it covers: written after the rows it stands for, its own
    /// ordinal says nothing about where the cut falls.
    /// </summary>
    /// <param name="conversationId">The conversation to cut.</param>
    /// <param name="fromOrdinal">The first ordinal to remove. It goes too.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many rows went, and the span of turns they belonged to.</returns>
    ValueTask<ConversationCut> TruncateAsync(
        string conversationId,
        int fromOrdinal,
        CancellationToken cancellationToken = default);

    /// <summary>Erases one conversation's words, and leaves its row in the listing.</summary>
    /// <param name="conversationId">The conversation to quieten.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many messages went.</returns>
    ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every conversation whose last activity is older than the retention window.</summary>
    /// <param name="retention">
    /// How long a conversation is kept, measured from its most recent message, or from when it was made when
    /// it holds none. The window belongs to a deployment: it is not a schema key, and nothing here
    /// defaults it.
    /// </param>
    /// <param name="batchSize">
    /// How many conversations one transaction may delete. The sweep loops until a batch deletes nothing, so
    /// this bounds one transaction and never the work.
    /// </param>
    /// <param name="cancellationToken">Cancels the sweep between batches, and inside one.</param>
    /// <returns>How many conversations went, over every batch.</returns>
    ValueTask<int> SweepAsync(
        TimeSpan retention,
        int batchSize = 500,
        CancellationToken cancellationToken = default);

    /// <summary>Gives a principal a claim on a conversation.</summary>
    /// <param name="conversationId">The conversation to claim.</param>
    /// <param name="principalKey">The opaque key that claims it.</param>
    /// <param name="role">What the key is to this conversation. AgentCore assigns its values no meaning.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask AttachPrincipalAsync(
        string conversationId,
        string principalKey,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>Takes a principal's claim off a conversation.</summary>
    /// <param name="conversationId">The conversation to unclaim.</param>
    /// <param name="principalKey">The key to remove.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DetachPrincipalAsync(
        string conversationId,
        string principalKey,
        CancellationToken cancellationToken = default);

    /// <summary>Files one session envelope under one continuation id, replacing any envelope already there.</summary>
    /// <param name="continuationId">The continuation id: a conversation id or a response id.</param>
    /// <param name="envelope">The serialized session, as the agent wrote it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The continuation names a conversation whose words live in store 1, so the map lives in this store:
    /// a resume must never find the key without the words.
    /// </remarks>
    ValueTask SaveContinuationAsync(
        string continuationId,
        JsonElement envelope,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the envelope one continuation id names.</summary>
    /// <param name="continuationId">The continuation id to look up.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The envelope, or <see langword="null"/> when nothing is filed under that id.</returns>
    ValueTask<JsonElement?> GetContinuationAsync(
        string continuationId,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws whatever one continuation id names, if anything.</summary>
    /// <param name="continuationId">The continuation id to forget.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DeleteContinuationAsync(
        string continuationId,
        CancellationToken cancellationToken = default);
}

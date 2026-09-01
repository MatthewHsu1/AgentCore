using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>Where store 0 keeps what a call is, apart from its words.</summary>
public interface ICallStore
{
    /// <summary>Makes the call's row, or returns the one already there.</summary>
    /// <param name="callId">The call to record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The row, whether this call made it or found it.</returns>
    ValueTask<CallRecord> CreateAsync(string callId, CancellationToken cancellationToken = default);

    /// <summary>Reads one call's row.</summary>
    /// <param name="callId">The call to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/> when store 0 holds none.</returns>
    ValueTask<CallRecord?> GetAsync(string callId, CancellationToken cancellationToken = default);

    /// <summary>Lists one principal's calls, most recently active first.</summary>
    /// <param name="principalKey">The opaque key to list by.</param>
    /// <param name="after">A cursor from an earlier page, or <see langword="null"/> for the first.</param>
    /// <param name="limit">How many rows this page may hold.</param>
    /// <param name="status">The one status to return, or <see langword="null"/> for every status.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page, and a cursor when a following page exists.</returns>
    ValueTask<CallPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        CallStatus? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a call's title.</summary>
    /// <param name="callId">The call to rename.</param>
    /// <param name="title">What to show in a list.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RenameAsync(string callId, string title, CancellationToken cancellationToken = default);

    /// <summary>Archives a call, or brings it back.</summary>
    /// <param name="callId">The call to move.</param>
    /// <param name="status">Where to move it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetStatusAsync(string callId, CallStatus status, CancellationToken cancellationToken = default);

    /// <summary>Replaces a call's consumer-owned fields.</summary>
    /// <param name="callId">The call to write.</param>
    /// <param name="custom">The fields, or <see langword="null"/> to clear them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetCustomAsync(string callId, JsonElement? custom, CancellationToken cancellationToken = default);

    /// <summary>Replaces a call's consumer-owned id.</summary>
    /// <param name="callId">The call to write.</param>
    /// <param name="externalId">The consumer's own id for the call, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetExternalIdAsync(string callId, string? externalId, CancellationToken cancellationToken = default);

    /// <summary>Erases the call's row and every attachment to it.</summary>
    /// <param name="callId">The call to erase.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default);

    /// <summary>Writes a turn's new messages, and the state the session holds after them.</summary>
    /// <param name="messages">The rows to write, oldest first.</param>
    /// <param name="state">
    /// What the session holds after this turn, or <see langword="null"/> to leave the stored state
    /// alone. It rides with the words on purpose: a crash between the two would leave the stage
    /// behind the words it belongs to. For the same reason, a non-<see langword="null"/> state is
    /// silently dropped when <paramref name="messages"/> is empty: an empty turn writes no words for
    /// it to ride with, so there is nothing to write it beside.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default);

    /// <summary>Rewrites one already-written message in place, on a barge-in.</summary>
    /// <param name="callId">The call the message belongs to.</param>
    /// <param name="ordinal">The message's position within the call.</param>
    /// <param name="content">What the caller actually heard.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default);

    /// <summary>Reads one whole call's words, oldest message first.</summary>
    /// <param name="callId">The call to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every message of the call, or an empty list when it holds none.</returns>
    ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws the tail of a call's words, from one ordinal onward.</summary>
    /// <param name="callId">The call to cut.</param>
    /// <param name="fromOrdinal">The first ordinal to remove. It goes too.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many messages went.</returns>
    ValueTask<int> TruncateAsync(
        string callId, int fromOrdinal, CancellationToken cancellationToken = default);

    /// <summary>Erases one call's words, and leaves its row in the listing.</summary>
    /// <param name="callId">The call to quieten.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many messages went.</returns>
    ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every call whose last activity is older than the retention window.</summary>
    /// <param name="retention">
    /// How long a call is kept, measured from its most recent message, or from when it was made when
    /// it holds none. The window belongs to a deployment: it is not a schema key, and nothing here
    /// defaults it.
    /// </param>
    /// <param name="batchSize">
    /// How many calls one transaction may delete. The sweep loops until a batch deletes nothing, so
    /// this bounds one transaction and never the work.
    /// </param>
    /// <param name="cancellationToken">Cancels the sweep between batches, and inside one.</param>
    /// <returns>How many calls went, over every batch.</returns>
    ValueTask<int> SweepAsync(
        TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default);

    /// <summary>Gives a principal a claim on a call.</summary>
    /// <param name="callId">The call to claim.</param>
    /// <param name="principalKey">The opaque key that claims it.</param>
    /// <param name="role">What the key is to this call. AgentCore assigns its values no meaning.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask AttachPrincipalAsync(
        string callId, string principalKey, string role, CancellationToken cancellationToken = default);

    /// <summary>Takes a principal's claim off a call.</summary>
    /// <param name="callId">The call to unclaim.</param>
    /// <param name="principalKey">The key to remove.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DetachPrincipalAsync(
        string callId, string principalKey, CancellationToken cancellationToken = default);
}

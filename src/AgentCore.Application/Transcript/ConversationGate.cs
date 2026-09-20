using AgentCore.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Transcript;

/// <summary>
/// What one conversation holds outside its state bag: its lock, its transcript, and its queue of
/// store writes, which never faults and lets the conversation outlive a store that refuses.
/// </summary>
internal sealed class ConversationGate
{
    private readonly ILogger _logger;

    /// <summary>Creates the gate of one conversation.</summary>
    /// <param name="logger">Where a refused write is logged.</param>
    public ConversationGate(ILogger logger) => _logger = logger;

    /// <summary>Gets the lock every read and every change of this conversation's transcript takes.</summary>
    public Lock Sync { get; } = new();

    /// <summary>Gets the conversation's live transcript. It never enters the state bag, so serializing the bag stays small.</summary>
    public ConversationTranscript Transcript { get; } = new();

    /// <summary>Gets the tail of this conversation's store writes. It never faults.</summary>
    public Task Writes { get; private set; } = Task.CompletedTask;

    /// <summary>Gets or sets where a dropped write of this conversation is reported, if anywhere.</summary>
    public TranscriptWriteDropped? Dropped { get; set; }

    /// <summary>Queues one store write behind everything this conversation has already queued. Runs under <see cref="Sync"/>.</summary>
    /// <param name="write">The write.</param>
    public void Enqueue(Func<ValueTask> write)
        => Writes = WriteAfterAsync(Writes, write, Transcript.ConversationId, Transcript.TurnIndex);

    /// <summary>Writes to the store, and lets the conversation outlive a store that refuses.</summary>
    private async Task WriteAfterAsync(Task previous, Func<ValueTask> write, string conversationId, int turnIndex)
    {
        await previous.ConfigureAwait(false);

        try
        {
            await write().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A store 1 write failure never ends a conversation, and never breaks the chain behind it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.TranscriptWriteFailed(_logger, conversationId, turnIndex, exception);

            // The words are lost and the conversation is not. The report is what turns that into a fact the
            // host can count; the contract of TranscriptWriteDropped is that it cannot throw, which
            // is what keeps this chain from ever faulting.
            Dropped?.Invoke(turnIndex, exception);
        }
    }
}

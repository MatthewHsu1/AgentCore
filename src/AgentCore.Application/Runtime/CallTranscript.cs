using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>The words of one call, and the ordinals that address them.</summary>
/// <remarks>
/// <para>
/// It is state and rules, and nothing else: every method is synchronous, allocates the ordinals it
/// needs, and hands back the rows a caller must write. The connection, the gate, and the
/// write-failure policy belong to <see cref="AgentCoreChatHistoryProvider"/>, which is its only
/// caller. That split is what lets the rules below be tested without an agent, a session, or a
/// store.
/// </para>
/// <para>
/// <b>It is not safe for two callers at once.</b> The provider's per-call gate is what serialises
/// it, and that gate is what keeps two appends off one ordinal and keeps a barge-in from rewriting
/// a reply the caller fully heard.
/// </para>
/// <para>
/// It is the value the provider keeps in the session's state bag, so every member here has to
/// survive a round trip through JSON.
/// </para>
/// </remarks>
internal sealed class CallTranscript
{
    /// <summary>Gets or sets the id of the call. The turn loop stamps it.</summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based index of the turn now running. The turn loop stamps it.</summary>
    public int TurnIndex { get; set; }

    /// <summary>Gets or sets the next free ordinal of the call.</summary>
    public int NextOrdinal { get; set; }

    /// <summary>Gets or sets the reply a barge-in would cut, or null when this turn has spoken none yet.</summary>
    public int? LastAssistantOrdinal { get; set; }

    /// <summary>Gets the live history of the call, oldest first.</summary>
    public List<StoredMessage> Messages { get; } = [];

    /// <summary>Opens a turn, and closes the previous turn's reply to a cut.</summary>
    /// <param name="turnIndex">The zero-based index of the turn about to run.</param>
    /// <remarks>
    /// A barge-in that finds no reply for the turn now running must do nothing and let the append
    /// record the already-cut text. It must never reach back a turn and replace a sentence the
    /// caller heard in full.
    /// </remarks>
    public void BeginTurn(int turnIndex)
    {
        TurnIndex = turnIndex;
        LastAssistantOrdinal = null;
    }

    /// <summary>Reads the whole call, oldest message first.</summary>
    public IReadOnlyList<ChatMessage> Read() => [.. Messages.Select(stored => stored.Message)];

    /// <summary>Adds new messages to the call, and returns the rows they became.</summary>
    /// <param name="messages">The new messages, oldest first.</param>
    public IReadOnlyList<CallMessage> Append(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var rows = new List<CallMessage>(messages.Count);
        foreach (var message in messages)
        {
            var ordinal = NextOrdinal++;
            Messages.Add(new StoredMessage { Ordinal = ordinal, TurnIndex = TurnIndex, Message = message });
            rows.Add(new CallMessage(CallId, ordinal, TurnIndex, message));

            // A tool-calling turn produces two assistant messages and only the second is spoken.
            // The textless one is not a reply anybody can be cut off in the middle of.
            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
            {
                LastAssistantOrdinal = ordinal;
            }
        }

        return rows;
    }

    /// <summary>Replaces this turn's reply with the words the caller actually heard.</summary>
    /// <param name="heard">The text the caller heard, as the vendor reported it. Nothing is estimated.</param>
    /// <returns>The row to rewrite, or <see langword="null"/> when this turn has no reply to cut.</returns>
    /// <remarks>
    /// Everything the message carried besides its words is kept. A reply also carries the tokens it
    /// cost, and rebuilding it from the heard text alone would drop them.
    /// </remarks>
    public CallMessage? TruncateLastReply(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);

        if (LastAssistantOrdinal is not int ordinal)
        {
            return null;
        }

        var stored = Messages.Find(message => message.Ordinal == ordinal);
        if (stored is null)
        {
            return null;
        }

        var truncated = stored.Message.Clone();
        truncated.Contents = [new TextContent(heard), .. stored.Message.Contents.Where(c => c is not TextContent)];
        stored.Message = truncated;

        return new CallMessage(CallId, ordinal, stored.TurnIndex, truncated);
    }

    /// <summary>One message of the live history, with the ordinal its stored row carries.</summary>
    internal sealed class StoredMessage
    {
        /// <summary>Gets or sets the message's position within the call.</summary>
        public int Ordinal { get; set; }

        /// <summary>Gets or sets the turn the message belongs to. It is the join to the audit chain.</summary>
        public int TurnIndex { get; set; }

        /// <summary>Gets or sets the message. A barge-in replaces it with what the caller heard.</summary>
        public ChatMessage Message { get; set; } = new(ChatRole.Assistant, string.Empty);
    }
}

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
/// <b>It is not safe for two callers at once.</b> The provider's per-call lock is what serialises
/// it, and that lock is what keeps two appends off one ordinal when a barge-in lands on one thread
/// while a turn is still finishing on another.
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

    /// <summary>Gets or sets the reply a barge-in would cut, or null when the call has spoken none.</summary>
    /// <remarks>
    /// It survives the turn that produced it. The vendor paces the audio, so a held prompt starts
    /// the next turn while the caller is still hearing the last one, and the reply a barge-in cuts is
    /// then the previous turn's. Whether that turn may still be corrected is
    /// <see cref="CallSession"/>'s decision, and this class does not second-guess it.
    /// </remarks>
    public int? LastAssistantOrdinal { get; set; }

    /// <summary>Gets the live history of the call, oldest first.</summary>
    public List<StoredMessage> Messages { get; } = [];

    /// <summary>Opens a turn, so the rows it appends carry its index.</summary>
    /// <param name="turnIndex">The zero-based index of the turn about to run.</param>
    public void BeginTurn(int turnIndex) => TurnIndex = turnIndex;

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

    /// <summary>Cuts the turn the caller was hearing down to the words the caller actually heard.</summary>
    /// <param name="heard">The text the caller heard, as the vendor reported it. Nothing is estimated.</param>
    /// <returns>The rows to rewrite, oldest first, or empty when the call has no reply to cut.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every word of the turn goes, not only the last one.</b> A model routinely writes a line and
    /// puts the tool call it announces on the same message — "Let me check that for you", then the
    /// call — and a graph row writes one reply for each node. The caller heard as much of the turn as
    /// the vendor played, and nothing else, so the prose of every earlier message is dropped and the
    /// heard text lands once, on the reply the cut fell in.
    /// </para>
    /// <para>
    /// Nothing but the words changes. A row keeps its tool content, so a side effect that ran stays
    /// visible to the next turn, and a reply keeps the tokens it cost. No row is removed, because an
    /// ordinal is permanent: a turn the caller cut before it spoke leaves a reply row holding nothing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CallMessage> TruncateLastReply(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);

        if (LastAssistantOrdinal is not int ordinal
            || Messages.Find(message => message.Ordinal == ordinal) is not { } spoken)
        {
            return [];
        }

        List<CallMessage> rows = [];
        foreach (var stored in Messages)
        {
            if (stored.TurnIndex != spoken.TurnIndex || stored.Message.Role != ChatRole.Assistant)
            {
                continue;
            }

            var isReply = stored.Ordinal == ordinal;
            if (!isReply && !stored.Message.Contents.Any(content => content is TextContent))
            {
                continue;
            }

            List<AIContent> kept = [.. stored.Message.Contents.Where(content => content is not TextContent)];

            var corrected = stored.Message.Clone();
            corrected.Contents = isReply && heard.Length > 0 ? [new TextContent(heard), .. kept] : kept;
            stored.Message = corrected;

            rows.Add(new CallMessage(CallId, stored.Ordinal, stored.TurnIndex, corrected));
        }

        return rows;
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

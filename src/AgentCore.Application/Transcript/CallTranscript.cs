using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>The words of one call, and the ordinals that address them.</summary>
internal sealed class CallTranscript
{
    /// <summary>Gets or sets the id of the call. The turn loop stamps it.</summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based index of the turn now running. The turn loop stamps it.</summary>
    public int TurnIndex { get; set; }

    /// <summary>Gets or sets the next free ordinal of the call.</summary>
    public int NextOrdinal { get; set; }

    /// <summary>Gets or sets the reply a barge-in would cut, or null when the call has spoken none.</summary>
    public int? LastAssistantOrdinal { get; set; }

    /// <summary>
    /// Gets or sets the live history of the call, oldest first. It needs a setter, not just a getter:
    /// with no setter, System.Text.Json silently skips it on deserialize instead of populating it.
    /// </summary>
    public List<StoredMessage> Messages { get; set; } = [];

    /// <summary>Reads a call that already has words back into a transcript that has none.</summary>
    /// <param name="rows">Every stored message of the call. Order does not matter.</param>
    /// <returns>The index the next turn of this call takes.</returns>
    public int Resume(IReadOnlyList<CallMessage> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return 0;
        }

        // Stripped for the reason Append gives: the live history is serialised into the session
        // state bag every turn, and what store 1 keeps is bigger than that bag should ever carry.
        Messages = [.. rows
            .OrderBy(row => row.Ordinal)
            .Select(row => new StoredMessage
            {
                Ordinal = row.Ordinal,
                TurnIndex = row.TurnIndex,
                Message = row.Content.WithoutHostContent(),
            })];

        NextOrdinal = rows.Max(row => row.Ordinal) + 1;

        return rows.Max(row => row.TurnIndex) + 1;
    }

    /// <summary>Opens a turn, so the rows it appends carry its index.</summary>
    public void BeginTurn(int turnIndex) => TurnIndex = turnIndex;

    /// <summary>Reads the whole call, oldest message first.</summary>
    public IReadOnlyList<ChatMessage> Read() => [.. Messages.Select(stored => stored.Message)];

    /// <summary>Adds new messages to the call, and returns the rows they became.</summary>
    public IReadOnlyList<CallMessage> Append(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var rows = new List<CallMessage>(messages.Count);
        foreach (var message in messages)
        {
            var ordinal = NextOrdinal++;

            // The row is what the store persists, so it keeps the full message, drawing and source
            // included. Messages is the live history, serialised into the session state bag every
            // turn, so it never sees either: a multi-kilobyte RenderContent, or a SourceContent citing
            // a document, has no business growing that bag.
            rows.Add(new CallMessage(CallId, ordinal, TurnIndex, message));

            Messages.Add(new StoredMessage
            {
                Ordinal = ordinal,
                TurnIndex = TurnIndex,
                Message = message.WithoutHostContent(),
            });

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

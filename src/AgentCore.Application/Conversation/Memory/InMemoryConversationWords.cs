using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Conversation.Memory;

/// <summary>
/// The words of every conversation the in-memory store holds, by ordinal, and the next ordinal each
/// conversation will issue. Every member runs under the owning store's lock; nothing here takes one.
/// </summary>
internal sealed class InMemoryConversationWords
{
    private readonly Dictionary<(string ConversationId, int Ordinal), ConversationMessage> _rows = [];

    /// <summary>The next free ordinal of each conversation. Never rewound, even when its words are.</summary>
    private readonly Dictionary<string, int> _nextOrdinal = [];

    /// <summary>Reads the ordinal a conversation's next row will take.</summary>
    /// <param name="conversationId">The conversation to ask about.</param>
    /// <returns>The next free ordinal, or zero for a conversation that has never spoken.</returns>
    public int NextOrdinal(string conversationId) => _nextOrdinal.GetValueOrDefault(conversationId);

    /// <summary>Numbers and keeps a turn's new rows.</summary>
    /// <param name="conversationId">The conversation the rows belong to.</param>
    /// <param name="drafts">The rows to keep, oldest first.</param>
    /// <param name="fallbackTurnIndex">The turn a draft that names none is filed under.</param>
    /// <returns>The rows as kept, in the order given.</returns>
    public IReadOnlyList<ConversationMessage> Append(
        string conversationId, IReadOnlyList<ConversationMessageDraft> drafts, int fallbackTurnIndex)
    {
        var first = NextOrdinal(conversationId);

        var rows = new List<ConversationMessage>(drafts.Count);
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            ConversationMessage row = new(
                conversationId, first + index, draft.TurnIndex ?? fallbackTurnIndex, draft.Content, draft.MessageId)
            {
                CoversUpTo = draft.CoversUpTo,
            };

            rows.Add(row);
            _rows.Add((row.ConversationId, row.Ordinal), row);
        }

        _nextOrdinal[conversationId] = first + drafts.Count;

        return rows;
    }

    /// <summary>Replaces one row's content in place, by the name the caller knows it by.</summary>
    /// <param name="conversationId">The conversation the row belongs to.</param>
    /// <param name="messageId">The row to rewrite.</param>
    /// <param name="content">What it now says.</param>
    public void Rewrite(string conversationId, string messageId, ChatMessage content)
    {
        foreach (var pair in _rows)
        {
            if (pair.Key.ConversationId != conversationId || pair.Value.MessageId != messageId)
            {
                continue;
            }

            _rows[pair.Key] = pair.Value with { Content = content };
            break;
        }
    }

    private IReadOnlyList<ConversationMessage> Read(string conversationId)
        => [.. Said(conversationId).OrderBy(row => row.Ordinal)];

    /// <summary>Reads one conversation as its session opens it: the newest summary, and every row above what it covers.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <returns>The rows, oldest ordinal first.</returns>
    public IReadOnlyList<ConversationMessage> ReadForSession(string conversationId)
    {
        var summary = Of(conversationId).Where(row => row.CoversUpTo is not null).MaxBy(row => row.Ordinal);
        if (summary is not { CoversUpTo: { } covered })
        {
            return Read(conversationId);
        }

        return [.. Of(conversationId)
            .Where(row => row.Ordinal > covered && (row.CoversUpTo is null || row.Ordinal == summary.Ordinal))
            .OrderBy(row => row.Ordinal)];
    }

    /// <summary>Reads the newest turns of one conversation before a given one, oldest row first. Summary rows are left out.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="window">Which turns.</param>
    /// <returns>Every row of those turns.</returns>
    public IReadOnlyList<ConversationMessage> Read(string conversationId, TranscriptWindow window)
    {
        var rows = Said(conversationId).Where(row => window.Admits(row.TurnIndex)).ToList();

        var kept = rows
            .Select(row => row.TurnIndex)
            .Distinct()
            .OrderByDescending(turn => turn)
            .Take(window.Turns)
            .ToHashSet();

        return [.. rows.Where(row => kept.Contains(row.TurnIndex)).OrderBy(row => row.Ordinal)];
    }

    /// <summary>Finds the ordinal of one row somebody said (<see cref="IConversationStore.OrdinalOfAsync"/>).</summary>
    /// <param name="conversationId">The conversation to search.</param>
    /// <param name="messageId">The row to find.</param>
    /// <returns>The ordinal, or <see langword="null"/> when no spoken row carries that id.</returns>
    public int? OrdinalOf(string conversationId, string messageId)
        => Said(conversationId).FirstOrDefault(row => string.Equals(row.MessageId, messageId, StringComparison.Ordinal))?.Ordinal;

    /// <summary>
    /// Removes a conversation's rows from one ordinal onward. A summary row goes only when the cut
    /// reaches a row it covers (<see cref="IConversationStore.TruncateAsync"/>).
    /// </summary>
    /// <param name="conversationId">The conversation to cut.</param>
    /// <param name="fromOrdinal">The first ordinal to remove. It goes too.</param>
    /// <returns>How many rows went, and the span of turns they belonged to.</returns>
    public ConversationCut Truncate(string conversationId, int fromOrdinal)
    {
        var spoken = Said(conversationId).Where(row => row.Ordinal >= fromOrdinal).Select(row => row.TurnIndex).ToList();
        WithdrawnTurns? turns = spoken.Count == 0 ? null : new(spoken.Min(), spoken.Max());

        var rows = Remove(conversationId, row => row.Ordinal >= fromOrdinal && (row.CoversUpTo is null || row.CoversUpTo >= fromOrdinal));

        return new(rows, turns);
    }

    /// <summary>Removes every row of a conversation and keeps its next ordinal.</summary>
    /// <param name="conversationId">The conversation to quieten.</param>
    /// <returns>How many rows went.</returns>
    public int Erase(string conversationId) => Remove(conversationId, static _ => true);

    /// <summary>Removes every row of a conversation and forgets its next ordinal.</summary>
    /// <param name="conversationId">The conversation that is going.</param>
    public void Forget(string conversationId)
    {
        _nextOrdinal.Remove(conversationId);
        Erase(conversationId);
    }

    private IEnumerable<ConversationMessage> Of(string conversationId)
        => _rows.Values.Where(row => string.Equals(row.ConversationId, conversationId, StringComparison.Ordinal));

    private IEnumerable<ConversationMessage> Said(string conversationId)
        => Of(conversationId).Where(static row => row.CoversUpTo is null);

    private int Remove(string conversationId, Func<ConversationMessage, bool> going)
    {
        var keys = Of(conversationId)
            .Where(going)
            .Select(row => (row.ConversationId, row.Ordinal))
            .ToList();

        foreach (var key in keys)
        {
            _rows.Remove(key);
        }

        return keys.Count;
    }
}

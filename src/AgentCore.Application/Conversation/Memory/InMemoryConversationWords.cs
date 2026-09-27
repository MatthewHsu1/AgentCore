using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Conversation.Memory
{
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
        public int NextOrdinal(string conversationId)
        {
            return _nextOrdinal.GetValueOrDefault(conversationId);
        }

        /// <summary>Numbers and keeps a turn's new rows.</summary>
        /// <param name="conversationId">The conversation the rows belong to.</param>
        /// <param name="drafts">The rows to keep, oldest first.</param>
        /// <param name="fallbackTurnIndex">The turn a draft that names none is filed under.</param>
        /// <returns>The rows as kept, in the order given.</returns>
        public IReadOnlyList<ConversationMessage> Append(
            string conversationId, IReadOnlyList<ConversationMessageDraft> drafts, int fallbackTurnIndex)
        {
            int first = NextOrdinal(conversationId);

            List<ConversationMessage> rows = new(drafts.Count);
            for (int index = 0; index < drafts.Count; index++)
            {
                ConversationMessageDraft draft = drafts[index];
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
            foreach (KeyValuePair<(string ConversationId, int Ordinal), ConversationMessage> pair in _rows)
            {
                if (pair.Key.ConversationId != conversationId || pair.Value.MessageId != messageId)
                {
                    continue;
                }

                _rows[pair.Key] = pair.Value with { Content = content };
                break;
            }
        }

        /// <summary>Deletes one row, by the name the caller knows it by. The rows around it keep their ordinals.</summary>
        /// <param name="conversationId">The conversation the row belongs to.</param>
        /// <param name="messageId">The row to delete.</param>
        public void Delete(string conversationId, string messageId)
        {
            foreach ((string ConversationId, int Ordinal) key in _rows.Keys)
            {
                if (key.ConversationId == conversationId && _rows[key].MessageId == messageId)
                {
                    _ = _rows.Remove(key);
                    break;
                }
            }
        }

        private IReadOnlyList<ConversationMessage> Read(string conversationId)
        {
            return [.. Said(conversationId).OrderBy(row => row.Ordinal)];
        }

        /// <summary>Reads one conversation as its session opens it: the newest summary, and every row above what it covers.</summary>
        /// <param name="conversationId">The conversation to read.</param>
        /// <returns>The rows, oldest ordinal first.</returns>
        public IReadOnlyList<ConversationMessage> ReadForSession(string conversationId)
        {
            ConversationMessage? summary = Of(conversationId).Where(row => row.CoversUpTo is not null).MaxBy(row => row.Ordinal);
            return summary is not { CoversUpTo: { } covered }
                ? Read(conversationId)
                : [.. Of(conversationId)
                .Where(row => row.Ordinal > covered && (row.CoversUpTo is null || row.Ordinal == summary.Ordinal))
                .OrderBy(row => row.Ordinal)];
        }

        /// <summary>Reads the newest turns of one conversation before a given one, oldest row first. Summary rows are left out.</summary>
        /// <param name="conversationId">The conversation to read.</param>
        /// <param name="window">Which turns.</param>
        /// <returns>Every row of those turns.</returns>
        public IReadOnlyList<ConversationMessage> Read(string conversationId, TranscriptWindow window)
        {
            List<ConversationMessage> rows = [.. Said(conversationId).Where(row => window.Admits(row.TurnIndex))];

            HashSet<int> kept = [.. rows
                .Select(row => row.TurnIndex)
                .Distinct()
                .OrderByDescending(turn => turn)
                .Take(window.Turns)];

            return [.. rows.Where(row => kept.Contains(row.TurnIndex)).OrderBy(row => row.Ordinal)];
        }

        /// <summary>Finds the ordinal of one row somebody said (<see cref="IConversationStore.OrdinalOfAsync"/>).</summary>
        /// <param name="conversationId">The conversation to search.</param>
        /// <param name="messageId">The row to find.</param>
        /// <returns>The ordinal, or <see langword="null"/> when no spoken row carries that id.</returns>
        public int? OrdinalOf(string conversationId, string messageId)
        {
            return Said(conversationId).FirstOrDefault(row => string.Equals(row.MessageId, messageId, StringComparison.Ordinal))?.Ordinal;
        }

        /// <summary>
        /// Removes a conversation's rows from one ordinal onward. A summary row goes only when the cut
        /// reaches a row it covers (<see cref="IConversationStore.TruncateAsync"/>).
        /// </summary>
        /// <param name="conversationId">The conversation to cut.</param>
        /// <param name="fromOrdinal">The first ordinal to remove. It goes too.</param>
        /// <returns>How many rows went, and the span of turns they belonged to.</returns>
        public ConversationCut Truncate(string conversationId, int fromOrdinal)
        {
            List<int> spoken = [.. Said(conversationId).Where(row => row.Ordinal >= fromOrdinal).Select(row => row.TurnIndex)];
            WithdrawnTurns? turns = spoken.Count == 0 ? null : new(spoken.Min(), spoken.Max());

            int rows = Remove(conversationId, row => row.Ordinal >= fromOrdinal && (row.CoversUpTo is null || row.CoversUpTo >= fromOrdinal));

            return new(rows, turns);
        }

        /// <summary>Removes every row of a conversation and keeps its next ordinal.</summary>
        /// <param name="conversationId">The conversation to quieten.</param>
        /// <returns>How many rows went.</returns>
        public int Erase(string conversationId)
        {
            return Remove(conversationId, static _ => true);
        }

        /// <summary>Removes every row of a conversation and forgets its next ordinal.</summary>
        /// <param name="conversationId">The conversation that is going.</param>
        public void Forget(string conversationId)
        {
            _ = _nextOrdinal.Remove(conversationId);
            _ = Erase(conversationId);
        }

        private IEnumerable<ConversationMessage> Of(string conversationId)
        {
            return _rows.Values.Where(row => string.Equals(row.ConversationId, conversationId, StringComparison.Ordinal));
        }

        private IEnumerable<ConversationMessage> Said(string conversationId)
        {
            return Of(conversationId).Where(static row => row.CoversUpTo is null);
        }

        private int Remove(string conversationId, Func<ConversationMessage, bool> going)
        {
            List<(string ConversationId, int Ordinal)> keys = [.. Of(conversationId)
                .Where(going)
                .Select(row => (row.ConversationId, row.Ordinal))];

            foreach ((string ConversationId, int Ordinal) key in keys)
            {
                _ = _rows.Remove(key);
            }

            return keys.Count;
        }
    }
}

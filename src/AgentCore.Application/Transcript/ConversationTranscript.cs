using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>The words of one conversation, and the ordinals that address them.</summary>
    internal sealed class ConversationTranscript
    {
        /// <summary>Gets or sets the id of the conversation. The turn loop stamps it.</summary>
        public string ConversationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the zero-based index of the turn now running. The turn loop stamps it.</summary>
        public int TurnIndex { get; set; }

        /// <summary>Gets or sets the next free ordinal of the conversation.</summary>
        public int NextOrdinal { get; set; }

        /// <summary>Gets or sets the reply a barge-in would cut, or null when the conversation has spoken none.</summary>
        public int? LastAssistantOrdinal { get; set; }

        /// <summary>
        /// Gets or sets the live history of the conversation, oldest first. It needs a setter, not just a getter:
        /// with no setter, System.Text.Json silently skips it on deserialize instead of populating it.
        /// </summary>
        public List<StoredMessage> Messages { get; set; } = [];

        /// <summary>Gets the summary that stands in for the oldest rows, or <see langword="null"/> when every row is live.</summary>
        public StoredMessage? Summary => Messages.Find(static stored => stored.CoversUpTo is not null);

        /// <summary>
        /// Gets a count that moves on every change to the words. A compaction computed outside the lock
        /// names the revision it read, and is refused when the words moved underneath it.
        /// </summary>
        public int Revision { get; private set; }

        /// <summary>Reads a conversation that already has words back into a transcript that has none.</summary>
        /// <param name="rows">The rows of the conversation the session reads, as <see cref="Resync"/> takes them.</param>
        /// <param name="marks">How far the conversation had got, from the state stored beside these words.</param>
        /// <returns>The index the next turn of this conversation takes.</returns>
        public int Resume(IReadOnlyList<ConversationMessage> rows, TranscriptMarks marks)
        {
            Resync(rows, marks.NextOrdinal);

            return marks.NextTurnIndex;
        }

        /// <summary>
        /// Replaces the live history with what store 1 holds, without disturbing <see cref="TurnIndex"/>
        /// or <see cref="ConversationId"/>.
        /// </summary>
        /// <param name="rows">
        /// The rows store 1 read for the session: every row, or the newest summary and the rows above what
        /// it covers. A row a summary stands in for is the caller's mistake, not this method's to catch:
        /// it lands in the live history beside the summary that already speaks for it. Order does not matter.
        /// </param>
        /// <param name="nextOrdinal">The next free ordinal of the conversation, from store 0's own counter.</param>
        public void Resync(IReadOnlyList<ConversationMessage> rows, int nextOrdinal)
        {
            ArgumentNullException.ThrowIfNull(rows);

            // Stripped for the reason Append gives: the live history is serialised into the session
            // state bag every turn, and what store 1 keeps is bigger than that bag should ever carry.
            Messages = [.. rows
                .OrderBy(row => row.Ordinal)
                .Select(row => new StoredMessage
                {
                    Ordinal = row.Ordinal,
                    TurnIndex = row.TurnIndex,
                    MessageId = row.MessageId,
                    Message = row.Content.WithoutHostContent(),
                    CoversUpTo = row.CoversUpTo,
                })];

            NextOrdinal = nextOrdinal;
            Revision++;

            RestoreLastAssistantOrdinal();
        }

        /// <summary>Finds the ordinal a message sits at, by the name a caller knows it under.</summary>
        /// <param name="messageId">What the caller calls the message.</param>
        /// <returns>
        /// The ordinal, or <see langword="null"/> when the live history holds no message of that name.
        /// A message the <see cref="Summary"/> stands in for is not held, and answers null too.
        /// </returns>
        public int? OrdinalOf(string messageId)
        {
            ArgumentException.ThrowIfNullOrEmpty(messageId);

            return Messages.Find(stored => string.Equals(stored.MessageId, messageId, StringComparison.Ordinal))
                ?.Ordinal;
        }

        /// <summary>
        /// Whether an edit hanging off one message can be cut from what this transcript holds. It can
        /// unless the cut would reach a row the <see cref="Summary"/> stands in for: the whole
        /// conversation, or a parent this transcript does not hold. Such an edit needs every row read
        /// back first, because the summary must go and the rows it spoke for must come back.
        /// </summary>
        /// <param name="parentMessageId">The message the edit hangs off, or <see langword="null"/> for the whole conversation.</param>
        public bool CanTruncateFrom(string? parentMessageId)
        {
            return Summary is not { CoversUpTo: { } covered } || (parentMessageId is not null && OrdinalOf(parentMessageId) is { } ordinal && ordinal > covered);
        }

        /// <summary>
        /// Withdraws the tail of the conversation, from one ordinal onward. The <see cref="Summary"/>
        /// stays when the cut lies above what it covers, whatever its own ordinal: it was written after
        /// the rows it stands for, so its ordinal says nothing about where the cut falls.
        /// </summary>
        /// <param name="fromOrdinal">The first ordinal to drop. It goes too. It lies above what the <see cref="Summary"/> covers.</param>
        /// <returns>The turns the withdrawal took, or <see langword="null"/> when nothing went.</returns>
        /// <exception cref="InvalidOperationException">The cut reaches a row the <see cref="Summary"/> stands in for.</exception>
        public WithdrawnTurns? TruncateFrom(int fromOrdinal)
        {
            if (Summary is { CoversUpTo: { } covered } && fromOrdinal <= covered)
            {
                throw new InvalidOperationException(
                    $"The cut at ordinal {fromOrdinal} reaches under the summary of the conversation '{ConversationId}', "
                    + $"which covers every row up to {covered}. Read every row back before cutting there.");
            }

            int first = int.MaxValue;
            int last = int.MinValue;
            foreach (StoredMessage stored in Messages)
            {
                if (!Withdrawn(stored, fromOrdinal))
                {
                    continue;
                }

                first = Math.Min(first, stored.TurnIndex);
                last = Math.Max(last, stored.TurnIndex);
            }

            if (last < first)
            {
                return null;
            }

            _ = Messages.RemoveAll(stored => Withdrawn(stored, fromOrdinal));
            RestoreLastAssistantOrdinal();
            Revision++;
            return new WithdrawnTurns(first, last);
        }

        /// <summary>The ids of the rows <see cref="TruncateFrom"/> withdraws from <paramref name="fromOrdinal"/> on.</summary>
        public IReadOnlyList<string> IdsFrom(int fromOrdinal)
        {
            return [.. Messages.Where(stored => Withdrawn(stored, fromOrdinal)).Select(static stored => stored.MessageId)];
        }

        private static bool Withdrawn(StoredMessage stored, int fromOrdinal)
        {
            return stored.Ordinal >= fromOrdinal && stored.CoversUpTo is null;
        }

        private void RestoreLastAssistantOrdinal()
        {
            LastAssistantOrdinal = Messages
                        .Where(stored => stored.CoversUpTo is null
                                      && stored.Message.Role == ChatRole.Assistant
                                      && !string.IsNullOrWhiteSpace(stored.Message.Text))
                        .Select(stored => (int?)stored.Ordinal)
                        .LastOrDefault();
        }

        /// <summary>Opens a turn, so the rows it appends carry its index.</summary>
        public void BeginTurn(int turnIndex)
        {
            TurnIndex = turnIndex;
        }

        /// <summary>
        /// Reads the conversation as the model sees it: the <see cref="Summary"/> first, then every live
        /// row, oldest first. The summary leads whatever its ordinal: it was written after the newest
        /// rows it does not cover, and those must follow it, not precede it.
        /// </summary>
        public IReadOnlyList<ChatMessage> Read()
        {
            return [.. View(Messages.Count).Select(entry => entry.Message)];
        }

        /// <summary>
        /// Names what a compaction of the conversation would cover: everything before the newest live
        /// turn. That turn stays live so the edit a caller most often makes — of the last question, which
        /// hangs off the reply before it — still finds its parent without a full read.
        /// </summary>
        /// <returns>
        /// The view the compaction would run over, or <see langword="null"/> when nothing lies before the
        /// newest live turn: a conversation of one turn, or a summary with one turn above it — which the
        /// summary already covers.
        /// </returns>
        public TranscriptFloor? Floor()
        {
            int? newest = Messages.FindLast(static stored => stored.CoversUpTo is null)?.TurnIndex;

            int last = Messages.FindLastIndex(stored => stored.CoversUpTo is null && stored.TurnIndex < newest);

            return last < 0 ? null : new TranscriptFloor(Revision, Summary?.CoversUpTo, [.. View(last + 1)]);
        }

        /// <summary>The model view of the first <paramref name="count"/> rows: the summary first, then each live row at its own ordinal.</summary>
        private IEnumerable<ViewMessage> View(int count)
        {
            if (Summary is { CoversUpTo: { } covered } summary)
            {
                yield return new ViewMessage(summary.Message, covered);
            }

            foreach (StoredMessage? stored in Messages.Take(count).Where(stored => stored.CoversUpTo is null))
            {
                yield return new ViewMessage(stored.Message, stored.Ordinal);
            }
        }

        /// <summary>
        /// Puts one summary in place of every row at or below <paramref name="coversUpTo"/> and of the
        /// summary that stood before it, unless the words moved since <see cref="Floor"/> was read.
        /// </summary>
        /// <param name="summary">The message that now speaks for those rows.</param>
        /// <param name="coversUpTo">The last ordinal it speaks for. Never below what the standing <see cref="Summary"/> covers.</param>
        /// <param name="revision">The <see cref="Revision"/> the compaction read.</param>
        /// <returns>The row the summary became, for store 1; or <see langword="null"/> when the compaction was stale and dropped.</returns>
        public ConversationMessage? Compact(ChatMessage summary, int coversUpTo, int revision)
        {
            ArgumentNullException.ThrowIfNull(summary);

            if (Summary is { CoversUpTo: { } covered })
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(coversUpTo, covered);
            }

            if (revision != Revision)
            {
                return null;
            }

            _ = Messages.RemoveAll(stored => stored.Ordinal <= coversUpTo || stored.CoversUpTo is not null);

            int ordinal = NextOrdinal++;
            string messageId = NewMessageId();
            Messages.Add(new StoredMessage
            {
                Ordinal = ordinal,
                TurnIndex = TurnIndex,
                MessageId = messageId,
                Message = summary.WithoutHostContent(),
                CoversUpTo = coversUpTo,
            });

            RestoreLastAssistantOrdinal();
            Revision++;
            return new ConversationMessage(ConversationId, ordinal, TurnIndex, summary, messageId) { CoversUpTo = coversUpTo };
        }

        /// <summary>Adds new messages to the conversation, and returns the rows they became.</summary>
        /// <param name="messages">The messages the turn produced, oldest first.</param>
        /// <param name="firstMessageId">
        /// What the caller calls the first of them, or <see langword="null"/> to name it here. Only the
        /// first: it is the one the caller sent and so the only one the caller had a name for.
        /// </param>
        public IReadOnlyList<ConversationMessage> Append(
            IReadOnlyList<ChatMessage> messages, string? firstMessageId = null)
        {
            ArgumentNullException.ThrowIfNull(messages);

            List<ConversationMessage> rows = new(messages.Count);
            for (int index = 0; index < messages.Count; index++)
            {
                ChatMessage message = messages[index];

                int ordinal = NextOrdinal++;

                string messageId = (index == 0 ? firstMessageId : null) ?? NewMessageId();

                rows.Add(new ConversationMessage(ConversationId, ordinal, TurnIndex, message, messageId));

                Messages.Add(new StoredMessage
                {
                    Ordinal = ordinal,
                    TurnIndex = TurnIndex,
                    MessageId = messageId,
                    Message = message.WithoutHostContent(),
                });

                if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
                {
                    LastAssistantOrdinal = ordinal;
                }
            }

            Revision++;
            return rows;
        }

        /// <summary>Cuts the turn the caller was hearing down to the words the caller actually heard.</summary>
        /// <param name="heard">The words the caller heard, across every step of the turn's reply.</param>
        /// <param name="turnIndex">The turn the last reply must belong to, or <see langword="null"/> for whichever turn it is.</param>
        /// <returns>
        /// The rows rewritten in place, and the rows removed because the cut left them carrying nothing, by the
        /// same rule a running turn's cut drops them (<see cref="TurnWords.Carries"/>).
        /// </returns>
        public ReplyRewrite RewriteReply(string heard, int? turnIndex = null)
        {
            ArgumentNullException.ThrowIfNull(heard);

            if (LastAssistantOrdinal is not int ordinal
                || Messages.Find(message => message.Ordinal == ordinal) is not { } spoken
                || (turnIndex is { } wanted && spoken.TurnIndex != wanted))
            {
                return new ReplyRewrite([], []);
            }

            List<StoredMessage> replies = Messages.FindAll(stored =>
                stored.TurnIndex == spoken.TurnIndex && stored.Message.Role == ChatRole.Assistant && stored.CoversUpTo is null);
            List<ChatMessage> laid = ShownWords.Lay([.. replies.Select(stored => stored.Message)], heard);

            List<ConversationMessage> rows = [];
            List<ConversationMessage> removed = [];
            for (int index = 0; index < replies.Count; index++)
            {
                StoredMessage stored = replies[index];
                if (ReferenceEquals(laid[index], stored.Message) && stored.Ordinal != ordinal)
                {
                    continue;
                }

                ConversationMessage row = new(ConversationId, stored.Ordinal, stored.TurnIndex, laid[index], stored.MessageId);
                if (!TurnWords.Carries(laid[index]))
                {
                    _ = Messages.Remove(stored);
                    removed.Add(row);
                    continue;
                }

                stored.Message = laid[index];
                rows.Add(row);
            }

            ReplyRewrite rewrite = new(rows, removed);
            if (removed.Count > 0)
            {
                RestoreLastAssistantOrdinal();
            }

            if (rewrite.Changed)
            {
                Revision++;
            }

            return rewrite;
        }

        /// <summary>Mints a name for a row the caller had no name for.</summary>
        private static string NewMessageId()
        {
            return ConversationMessageIds.New();
        }

        /// <summary>One message of the live history, with the ordinal its stored row carries.</summary>
        internal sealed class StoredMessage
        {
            /// <summary>Gets or sets the message's position within the conversation.</summary>
            public int Ordinal { get; set; }

            /// <summary>Gets or sets the turn the message belongs to. It is the join to the audit chain.</summary>
            public int TurnIndex { get; set; }

            /// <summary>Gets or sets the name this message is known by, inside the conversation and outside it.</summary>
            public string MessageId { get; set; } = string.Empty;

            /// <summary>Gets or sets the message. A barge-in replaces it with what the caller heard.</summary>
            public ChatMessage Message { get; set; } = new(ChatRole.Assistant, string.Empty);

            /// <inheritdoc cref="ConversationMessage.CoversUpTo"/>
            public int? CoversUpTo { get; set; }
        }
    }
}

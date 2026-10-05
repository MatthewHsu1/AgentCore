using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// The durable writes of a conversation: each changes the session's transcript under its lock and queues the
    /// matching store write in the same step, so the store sees the writes in the order the transcript took them.
    /// </summary>
    /// <param name="gates">The gate of every session.</param>
    /// <param name="store">The message store, which an edit under the summary cuts directly.</param>
    /// <param name="logger">Where cuts and withdrawals are logged.</param>
    internal sealed class ConversationWrites(ConversationGates gates, IConversationStore store, ILogger logger)
    {
        private readonly ConversationGates _gates = gates;

        private readonly IConversationStore _store = store;

        private readonly ILogger _logger = logger;

        /// <summary>Appends one sealed turn, taking everything staged for it.</summary>
        /// <returns>
        /// The ids of the user's message and of the last message, which is null when the user's is the only one, the words
        /// the rows say, and the append's outcome.
        /// </returns>
        public TurnWrite Commit(AgentSession session, TurnCommit commit)
        {
            return _gates.Under(
                session,
                (transcript, gate) =>
                {
                    int user = commit.Before.Count;
                    IReadOnlyList<ConversationMessage> rows = transcript.Append(
                        TurnWords.Compose(commit, gate.TakeStaged()), commit.UserMessageId, user);
                    Task<bool> refused = gate.QueueAppend(rows, commit.State);
                    return new TurnWrite(
                        rows[user].MessageId,
                        rows.Count > user + 1 ? rows[^1].MessageId : null,
                        TurnWords.Spoken(rows.Select(row => row.Content)),
                        refused);
                });
        }

        /// <summary>Appends rows between turns, under the turn the conversation takes next.</summary>
        public void AppendBetweenTurns(AgentSession session, IReadOnlyList<ChatMessage> messages, int nextTurnIndex)
        {
            _ = _gates.Under(
                session,
                (transcript, gate) => gate.QueueAppend(transcript.Append(messages, turnIndex: nextTurnIndex), state: null));
        }

        /// <summary>Queues the write of a state no turn's words carry.</summary>
        public void SaveState(AgentSession session, ConversationSessionState state)
        {
            _ = _gates.Under(
                session,
                (_, gate) =>
                {
                    gate.QueueState(state);
                    return true;
                });
        }

        /// <summary>Rewrites the last reply to <paramref name="text"/>, when it belongs to <paramref name="turnIndex"/>.</summary>
        /// <returns><see langword="true"/> when a row was rewritten or removed.</returns>
        public bool Rewrite(AgentSession session, int turnIndex, string text)
        {
            return _gates.Under(
                session,
                (transcript, gate) =>
                {
                    ReplyRewrite rewrite = transcript.RewriteReply(text, turnIndex);
                    if (!rewrite.Changed)
                    {
                        return false;
                    }

                    gate.QueueRewrite(rewrite.Rewritten);
                    gate.QueueRemove(rewrite.Removed);
                    return true;
                });
        }

        /// <summary>Withdraws every row after <paramref name="parentMessageId"/>, or every row when it is null.</summary>
        /// <returns>The turns withdrawn, or <see langword="null"/> when nothing went.</returns>
        public async ValueTask<WithdrawnTurns?> TruncateFromAsync(AgentSession session, string? parentMessageId, CancellationToken cancellationToken)
        {
            ITranscriptLossCounter? losses = _gates.Under(session, static (_, gate) => gate.Losses);

            // Judged before the truncate, which may delete every row of an append the store took and still reported lost.
            TranscriptLossVerdict? verdict = losses is null
                ? null
                : await losses.JudgeAsync(session, cancellationToken).ConfigureAwait(false);

            WithdrawnTurns? withdrawn = _gates.Under(
                session,
                (transcript, gate) =>
                {
                    int from;
                    if (parentMessageId is null)
                    {
                        from = 0;
                    }
                    else if (transcript.OrdinalOf(parentMessageId) is { } parent)
                    {
                        from = parent + 1;
                    }
                    else
                    {
                        return (WithdrawnTurns?)null;
                    }

                    IReadOnlyList<string> ids = transcript.IdsFrom(from);
                    if (transcript.TruncateFrom(from) is not { } taken)
                    {
                        return null;
                    }

                    Log.ConversationTruncated(_logger, transcript.ConversationId, from, transcript.TurnIndex);

                    gate.QueueTruncate(from, ids);
                    return taken;
                });

            if (withdrawn is not null && verdict is { } judged && losses is not null)
            {
                losses.Realigned(judged);
            }

            return withdrawn;
        }

        /// <summary>
        /// Withdraws every row after <paramref name="parentMessageId"/> in the store itself, once every queued write
        /// has landed, and reads the rows back into the session: the summary stands in for rows the session no longer holds.
        /// </summary>
        /// <returns>The turns withdrawn, or <see langword="null"/> when the store holds no message of that name.</returns>
        public async ValueTask<WithdrawnTurns?> CutUnderSummaryAsync(
            AgentSession session, string? parentMessageId, CancellationToken cancellationToken)
        {
            await _gates.Writes(session).ConfigureAwait(false);

            (string conversationId, int turnIndex, ITranscriptLossCounter? losses) = _gates.Under(
                session, static (transcript, gate) => (transcript.ConversationId, transcript.TurnIndex, gate.Losses));

            int from;
            if (parentMessageId is null)
            {
                from = 0;
            }
            else if (await _store.OrdinalOfAsync(conversationId, parentMessageId, cancellationToken).ConfigureAwait(false) is { } parent)
            {
                from = parent + 1;
            }
            else
            {
                return null;
            }

            // Judged before the cut, which may delete rows the store took and still reported lost.
            TranscriptLossVerdict? verdict = losses is null
                ? null
                : await losses.JudgeAsync(session, cancellationToken).ConfigureAwait(false);

            ConversationCut cut = await _store.TruncateAsync(conversationId, from, cancellationToken).ConfigureAwait(false);
            Log.ConversationTruncated(_logger, conversationId, from, turnIndex);

            ConversationRecord refreshed = await _store.GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"The conversation store holds no conversation '{conversationId}' for its own session to edit.");
            IReadOnlyList<ConversationMessage> rows = await _store.ReadForSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);

            _ = _gates.Under(
                session,
                (transcript, _) =>
                {
                    transcript.Resync(rows, refreshed.NextOrdinal);
                    return true;
                });

            if (verdict is { } judged && losses is not null)
            {
                losses.Realigned(judged);
            }

            return cut.Turns;
        }

        /// <summary>Puts one summary in place of every row at or below <paramref name="coversUpTo"/>, unless the words moved since <paramref name="revision"/>.</summary>
        /// <returns><see langword="true"/> when the summary now stands.</returns>
        public bool Compact(AgentSession session, ChatMessage summary, int coversUpTo, int revision)
        {
            return _gates.Under(
                session,
                (transcript, gate) =>
                {
                    if (transcript.Compact(summary, coversUpTo, revision) is not { } row)
                    {
                        return false;
                    }

                    _ = gate.QueueAppend([row], state: null);
                    return true;
                });
        }
    }
}

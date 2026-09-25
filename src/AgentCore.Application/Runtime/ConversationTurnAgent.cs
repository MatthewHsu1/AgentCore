using System.Runtime.CompilerServices;
using AgentCore.Application.Conversation;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The outermost turn layer, and the one trigger of a turn's durable write. On entry it runs the turn's edit;
    /// when the run ends it seals the turn once: the completer decides it, the provider commits it in one append,
    /// and the ids of that append ride the turn's last update.
    /// </summary>
    /// <param name="inner">The layered agent of one entry or one stage.</param>
    /// <param name="history">Store 1, the only door to the conversation's words.</param>
    internal sealed class ConversationTurnAgent(AIAgent inner, AgentCoreChatHistoryProvider history) : DelegatingAIAgent(inner)
    {
        /// <summary>The cut of a run the host cancelled or the reader abandoned: it keeps everything yielded (W05, W06).</summary>
        private static readonly TurnCut Stopped = new(ShownText: null, Played: null);

        private readonly AgentCoreChatHistoryProvider _history = history ?? throw new ArgumentNullException(nameof(history));

        /// <inheritdoc />
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Filed(options) is null)
            {
                return await base.RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            }

            List<AgentResponseUpdate> updates = [];
            await foreach (AgentResponseUpdate update in RunCoreStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!update.Contents.OfType<TurnCommittedContent>().Any())
                {
                    updates.Add(update);
                }
            }

            return updates.ToAgentResponse();
        }

        /// <inheritdoc />
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Filed(options) is not { } turn)
            {
                await foreach (AgentResponseUpdate update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return update;
                }

                yield break;
            }

            IEnumerable<ChatMessage> request = await EnterAsync(turn, messages, cancellationToken).ConfigureAwait(false);

            List<AgentResponseUpdate> seen = [];
            Exception? fault = null;
            bool hostCancelled = false;
            Seal seal = default;
            IAsyncEnumerator<AgentResponseUpdate> stream = base
                .RunCoreStreamingAsync(request, session, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (turn.CutSlot.IsCut)
                    {
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        hostCancelled = true;
                        break;
                    }
#pragma warning disable CA1031 // Section 8.7, row six: a fault above every fallback still ends in a sealed turn.
                    catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        fault = exception;
                        break;
                    }

                    seen.Add(stream.Current);
                    yield return stream.Current;
                }

                seal = await SealAsync(turn, seen, fault, hostCancelled ? Stopped : null).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);

                // A reader that stops reading disposes this stream mid-run, and the turn keeps what it was
                // yielded (design row W06). A turn already sealed seals nothing here.
                _ = await SealAsync(turn, seen, fault, Stopped).ConfigureAwait(false);
            }

            // The host asked to stop: the turn is committed, and the host still gets its cancellation.
            if (hostCancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (seal.Refused)
            {
                throw new ConversationTurnConflictException(
                    $"Another request saved turn {turn.Invocation.TurnIndex} of conversation '{turn.Invocation.ConversationId}' "
                    + "first, so the words of this one were not kept.");
            }

            if (seal.Ids is { } committed)
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, [new TurnCommittedContent(committed.UserMessageId, committed.ReplyMessageId)]);
            }
        }

        private static FiledTurn? Filed(AgentRunOptions? options)
        {
            return TurnInvocation.From(options) is
            {
                Nested: false,
                HistorySession: { } historySession,
                User: { } user,
                Completer: { } completer,
                CutSlot: { } slot,
            } invocation
                ? new FiledTurn(invocation, historySession, user, completer, slot)
                : null;
        }

        /// <summary>Runs the turn's edit, and builds the request the run gets.</summary>
        private async ValueTask<IEnumerable<ChatMessage>> EnterAsync(
            FiledTurn turn, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
        {
            if (turn.Invocation.Origin is { NamesParent: true } origin)
            {
                WithdrawnTurns? withdrawn = _history.CanTruncateFrom(turn.HistorySession, origin.ParentMessageId)
                    ? await _history.TruncateFromAsync(turn.HistorySession, origin.ParentMessageId, cancellationToken).ConfigureAwait(false)
                    : await _history.CutUnderSummaryAsync(turn.HistorySession, origin.ParentMessageId, cancellationToken).ConfigureAwait(false);

                if (withdrawn is { } taken)
                {
                    turn.Completer.Superseded(taken);
                }
            }

            return turn.Invocation.RendersHistory
                && TurnMessages.GraphHistory(_history.Read(turn.HistorySession)) is { } rendered
                    ? [rendered, .. messages]
                    : messages;
        }

        /// <summary>Seals the turn: decide it, write it once, and raise its events.</summary>
        /// <param name="turn">The turn being sealed.</param>
        /// <param name="seen">Every update the run yielded.</param>
        /// <param name="fault">The fault the run threw, or <see langword="null"/>.</param>
        /// <param name="stopped">The cut to seal with when no cut reached the slot, or <see langword="null"/> for none.</param>
        /// <returns>
        /// The ids the commit wrote, which are <see langword="null"/> when the turn was already sealed or the session
        /// holds no conversation, and whether the store refused them.
        /// </returns>
        private async ValueTask<Seal> SealAsync(
            FiledTurn turn, List<AgentResponseUpdate> seen, Exception? fault, TurnCut? stopped)
        {
            if (!turn.CutSlot.TrySeal(out TurnCut? cut))
            {
                return default;
            }

            cut ??= stopped;

            TurnCommit sealing = new(turn.User)
            {
                Seen = seen.ToAgentResponse(),
                Disposition = ConversationTurnStream.ReadDisposition(seen),
                Cut = cut,
                CallerFacing = !turn.Invocation.CarriesHistory,
                UserMessageId = turn.Invocation.Origin?.MessageId,
            };

            TurnCommit commit = await turn.Completer.CompleteAsync(sealing, fault).ConfigureAwait(false);

            TurnWrite? written;
            lock (turn.CutSlot.Gate)
            {
                written = _history.CommitTurn(turn.HistorySession, commit);
            }

            // Another session of the conversation may have saved this turn first. The store's answer decides
            // whether the turn is published at all, so it is awaited here, outside the lock. A cut that arrives
            // meanwhile waits as a late cut, exactly as one between the seal and the commit does.
            if (written is not null && await turn.Completer.RefusedAsync(written.Refused).ConfigureAwait(false))
            {
                return new Seal(null, Refused: true);
            }

            // A session that holds no conversation writes no rows, so the words are read off what the rows would be.
            (string UserMessageId, string? ReplyMessageId)? ids = written?.Ids;

            string spoken = written?.Spoken ?? TurnWords.Spoken(TurnWords.Compose(commit, []));

            lock (turn.CutSlot.Gate)
            {
                TurnCut? late = turn.CutSlot.Commit(out TurnCut? recut);
                turn.Completer.Committed(ids, spoken, late, recut);
            }

            await turn.Completer.FinishAsync().ConfigureAwait(false);

            return new Seal(ids, Refused: false);
        }

        /// <summary>What sealing a turn wrote, and whether the store refused it.</summary>
        private readonly record struct Seal((string UserMessageId, string? ReplyMessageId)? Ids, bool Refused);

        private sealed record FiledTurn(
            TurnInvocation Invocation,
            AgentSession HistorySession,
            ChatMessage User,
            ITurnCompleter Completer,
            TurnCutSlot CutSlot);
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    internal sealed class ConversationTurnStream
    {
        private readonly ConversationSession _session;

        internal ConversationTurnStream(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>Streams one turn of the conversation against the session the conversation holds.</summary>
        /// <param name="userInput">What the caller said or answered.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <param name="cancellationToken">Cancels the model calls.</param>
        /// <returns>The reply, one update at a time.</returns>
        internal IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingCoreAsync(
            ChatMessage userInput,
            ConversationTurnOrigin? origin,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            return RunTurnStreamingIteratorAsync(userInput, origin, result: null, cancellationToken);
        }

        /// <summary>Runs one turn to its end over the streaming core, and returns what it did.</summary>
        /// <param name="userInput">What the caller said or answered.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <param name="cancellationToken">Cancels the turn.</param>
        /// <returns>What the turn did.</returns>
        internal async Task<TurnResult> RunTurnCoreAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            StrongBox<TurnResult?> result = new();

            await foreach (ChatResponseUpdate _ in RunTurnStreamingIteratorAsync(userInput, origin, result, cancellationToken)
                .ConfigureAwait(false))
            {
            }

            return result.Value!;
        }

        /// <summary>Starts one turn: the turn is admitted and filed, and <see cref="IConversationPort.Cut"/> reaches it.</summary>
        /// <param name="userInput">What the caller said or answered.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <param name="cancellationToken">Cancels the turn, from the start through its last update.</param>
        /// <returns>The turn's index and its reply. The reply runs only as it is enumerated.</returns>
        internal async Task<TurnRun> StartTurnAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            (ConversationTurn turn, TurnCutSlot slot) = await StartAsync(userInput, origin, cancellationToken).ConfigureAwait(false);

            return new TurnRun(
                turn.Index,
                RunAsync(turn, slot, result: null, new StrongBox<int>()),
                abandon: () => AbandonRun(turn, slot));
        }

        /// <summary>Streams one turn. The caller checks the arguments.</summary>
        /// <param name="userInput">What the caller said or answered.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <param name="result">Receives what the turn did, for a caller that collects the turn, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the model calls.</param>
        private async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingIteratorAsync(
            ChatMessage userInput,
            ConversationTurnOrigin? origin,
            StrongBox<TurnResult?>? result,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            (ConversationTurn turn, TurnCutSlot slot) = await StartAsync(userInput, origin, cancellationToken).ConfigureAwait(false);

            await foreach (ChatResponseUpdate update in RunAsync(turn, slot, result, new StrongBox<int>()).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private async Task<(ConversationTurn Turn, TurnCutSlot Slot)> StartAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            // The mark comes before every read: a turn of another session still filing its words holds it, and the
            // reads below must see those words and the turn index after them.
            await _session.Busy.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (AgentSession session, TranscriptCatchUp? catchUp) = await _session.Ledger.OpenForTurnAsync(cancellationToken).ConfigureAwait(false);

                _session.Runner.AdmitTurn();
                try
                {
                    await _session.Ledger.CatchUpAsync(session, catchUp, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _session.Cuts.ReleaseTurn();
                    throw;
                }

                ConversationTurn turn = _session.Runner.BeginTurn(userInput, _session.Ledger.Session() ?? session, origin);

                return (turn, _session.Cuts.StartRun(turn.Index, cancellationToken));
            }
            catch
            {
                _ = _session.Busy.Exit();
                throw;
            }
        }

        /// <summary>Runs one started turn to its seal, and frees the conversation for the next.</summary>
        /// <param name="turn">The turn <see cref="StartAsync"/> began.</param>
        /// <param name="slot">Where a cut of the turn waits for the seal.</param>
        /// <param name="result">Receives what the turn did, for a caller that collects the turn, or <see langword="null"/>.</param>
        /// <param name="enumerated">Set once the reply is enumerated. A second enumeration would end the run twice.</param>
        private async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
            ConversationTurn turn, TurnCutSlot slot, StrongBox<TurnResult?>? result, StrongBox<int> enumerated)
        {
            if (Interlocked.Exchange(ref enumerated.Value, 1) != 0)
            {
                throw new InvalidOperationException(
                    $"The reply of turn {turn.Index} of the conversation '{_session.ConversationId}' is already being read.");
            }

            // The span opened in StartAsync's frame; an async method hands no Activity.Current back.
            if (turn.Activity is { } span)
            {
                Activity.Current = span;
            }

            try
            {
                ConversationTurnCompletion completion = new(_session, turn);
                TurnInvocation invocation = _session.Runner.TurnInvocationOf(turn, completion, slot);
                AgentSession runSession = await _session.Runner.OpenRunAsync(turn, slot.Token).ConfigureAwait(false);

                TurnRegistry.Set(runSession, invocation);

                IAsyncEnumerable<AgentResponseUpdate> runStream = turn.Agent
                    .RunStreamingAsync(
                        [turn.Spoken],
                        runSession,
                        invocation.RunOptions(),
                        cancellationToken: slot.Token);

                IAsyncEnumerator<AgentResponseUpdate> stream = TurnUpdateMerge
                    .RunAsync(runStream, invocation.Notices!, slot.Token)
                    .GetAsyncEnumerator(slot.Token);

                try
                {
                    while (true)
                    {
                        AgentResponseUpdate update;

                        try
                        {
                            if (!await stream.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            update = stream.Current;
                        }
                        catch (OperationCanceledException) when (slot.IsCut)
                        {
                            break;
                        }

                        // Rides through like a notice: it carries no text of its own, so a caller
                        // that reads only IsOutput-gated content never sees it unless it asks.
                        if (update.Contents.OfType<TurnCommittedContent>().Any()
                            || update.Contents.OfType<NoticeContent>().Any())
                        {
                            yield return update.AsChatResponseUpdate();

                            continue;
                        }

                        ChatResponseUpdate content = update.AsChatResponseUpdate();

                        if (TurnMessages.CarriesContent(content) && IsOutput(update))
                        {
                            yield return content;
                        }
                    }
                }
                finally
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                result?.Value = completion.Result
                    ?? throw new InvalidOperationException(
                        $"The turn {turn.Index} of the conversation '{_session.ConversationId}' ended without a seal.");
            }
            finally
            {
                EndRun(turn, slot);
            }
        }

        /// <summary>Closes the turn's cut window and span, and frees the conversation for the next turn, here and in the store.</summary>
        private void EndRun(ConversationTurn turn, TurnCutSlot slot)
        {
            _session.Cuts.EndRun(slot);
            turn.Activity?.Dispose();
            _ = _session.Busy.Exit();
        }

        /// <summary>
        /// Ends a turn whose <see cref="TurnRun"/> was disposed before its reply was ever read. The turn's
        /// iterator never started, so nothing in <see cref="RunAsync"/> ran: no model call, no seal, and
        /// without this, no outcome tag and no <c>turn.duration</c> point either, though the span
        /// <see cref="ConversationTurnRunner.BeginTurn"/> opened is still live and must still end.
        /// It is given the outcome and duration a stopped turn gets, because that is exactly what it is:
        /// a turn cut before it produced anything, with nothing played. It keeps none of its words, so it is refused
        /// as dropped, and the refusal leaves its log line and its audit row.
        /// </summary>
        private void AbandonRun(ConversationTurn turn, TurnCutSlot slot)
        {
            TurnRefusals.Raise(_session, turn.Index, TurnRefusals.Dropped);

            AgentCoreTelemetry.EndTurn(
                turn.Activity,
                _session.Time.GetElapsedTime(turn.StartedAt),
                AgentCoreTelemetry.OutcomeInterrupted,
                turn.StageBefore,
                failure: null);

            EndRun(turn, slot);
        }

        /// <summary>Reads whether the caller hears the node that produced one update.</summary>
        /// <param name="update">One update of the run.</param>
        /// <returns><see langword="true"/> when the host should speak it.</returns>
        internal bool IsOutput(AgentResponseUpdate update)
        {
            return _session.Compiled.OutputAgents is not { } spoken
                    || (update.AuthorName is { } author && spoken.Contains(author))
                    || update.AdditionalProperties?.Contains<TurnDisposition>() is true;
        }

        /// <summary>Reads what the turn layers reported about one finished turn.</summary>
        /// <param name="response">What the agent answered.</param>
        /// <returns>The disposition, or <see langword="null"/> when no layer marked the turn.</returns>
        internal static TurnDisposition? ReadDisposition(AgentResponse response)
        {
            return response.AdditionalProperties is { } properties
                    && properties.TryGetValue(out TurnDisposition? disposition)
                    ? disposition
                    : null;
        }

        /// <summary>Reads what the turn layers reported across the updates of one streaming turn.</summary>
        /// <param name="updates">Every update the run produced, in order, before the seam filtered them.</param>
        /// <returns>The disposition, or <see langword="null"/> when no update carried one.</returns>
        internal static TurnDisposition? ReadDisposition(List<AgentResponseUpdate> updates)
        {
            TurnDisposition? found = null;
            foreach (AgentResponseUpdate update in updates)
            {
                if (update.AdditionalProperties is { } properties
                    && properties.TryGetValue(out TurnDisposition? disposition))
                {
                    found = disposition;
                }
            }

            return found;
        }
    }
}

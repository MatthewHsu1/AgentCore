using System.Runtime.CompilerServices;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

internal sealed class CallTurnStream
{
    private readonly CallSession _session;

    internal CallTurnStream(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Streams one turn of the call against the session the call holds.</summary>
    /// <param name="userInput">What the caller said or answered.</param>
    /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The reply, one update at a time.</returns>
    internal IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingCoreAsync(
        ChatMessage userInput,
        CallTurnOrigin? origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        return RunTurnStreamingIteratorAsync(userInput, origin, cancellationToken);
    }

    /// <summary>Streams one turn. The caller checks the arguments.</summary>
    private async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingIteratorAsync(
        ChatMessage userInput,
        CallTurnOrigin? origin,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = await _session.Ledger.OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        var turn = _session.Runner.BeginTurn(userInput, session, origin);

        // A streaming turn becomes audible only once it hands the host its first piece of content.
        // Until then nothing of it has reached the caller, and a barge-in belongs elsewhere.
        var cancellation = _session.Interruptions.StartRun(cancellationToken);
        try
        {
            List<AgentResponseUpdate> updates = [];
            string? toolFault = null;

            var invocation = _session.Runner.TurnInvocationOf(turn);
            var runSession = await _session.Runner.RunSessionAsync(turn, cancellation.Token).ConfigureAwait(false);
            
            TurnRegistry.Set(runSession, invocation);

            var stream = turn.Agent
                .RunStreamingAsync(
                    turn.Request,
                    runSession,
                    invocation.RunOptions(),
                    cancellationToken: cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            try
            {
                while (true)
                {
                    AgentResponseUpdate update;

                    // The enumeration itself sits in its own try, because a method that yields takes
                    // no catch clause around the yield.
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        update = stream.Current;
                    }
                    catch (OperationCanceledException) when (_session.Interruptions.CurrentInterruption() is not null)
                    {
                        // The caller spoke over the reply, and the relay already reported how much of
                        // it played. Nothing here estimates that value: see item 6c.
                        break;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Section 8.7, sixth row.
                        toolFault = exception.Message;
                        break;
                    }

                    updates.Add(update);

                    var content = update.AsChatResponseUpdate();
                    if (TurnMessages.CarriesContent(content) && Speaks(update))
                    {
                        // Marked before the yield, not after it: an async iterator only resumes past
                        // a yield once the host comes back for the next update, and by then the host
                        // has already queued this piece for the caller.
                        _session.Interruptions.RunIsAudible = true;

                        // The host speaks this, so it leaves the seam as fast as the model produced it.
                        yield return content;
                    }
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            // CompleteTurnAsync publishes LastTurn itself. See the same note in RunTurnAsync. The
            // disposition comes off the raw updates and never off the folded response: update-level
            // properties do not survive streaming coalescing.
            _ = await _session.Completion.CompleteTurnAsync(
                    turn, updates.ToAgentResponse(), toolFault, ReadDisposition(updates), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _session.Interruptions.EndRun(cancellation);
            turn.Activity?.Dispose();
        }
    }

    /// <summary>Reads whether the caller hears the node that produced one update.</summary>
    /// <param name="update">One update of the run.</param>
    /// <returns><see langword="true"/> when the host should speak it.</returns>
    internal bool Speaks(AgentResponseUpdate update)
        => _session.Compiled.SpokenBy is not { } spoken
            || (update.AuthorName is { } author && spoken.Contains(author))
            || update.AdditionalProperties?.Contains<TurnDisposition>() is true;

    /// <summary>Reads what the turn layers reported about one finished turn.</summary>
    /// <param name="response">What the agent answered.</param>
    /// <returns>The disposition, or <see langword="null"/> when no layer marked the turn.</returns>
    internal static TurnDisposition? ReadDisposition(AgentResponse response)
        => response.AdditionalProperties is { } properties
            && properties.TryGetValue<TurnDisposition>(out var disposition)
            ? disposition
            : null;

    /// <summary>Reads what the turn layers reported across the updates of one streaming turn.</summary>
    /// <param name="updates">Every update the run produced, in order, before the seam filtered them.</param>
    /// <returns>The disposition, or <see langword="null"/> when no update carried one.</returns>
    internal static TurnDisposition? ReadDisposition(List<AgentResponseUpdate> updates)
    {
        TurnDisposition? found = null;
        foreach (var update in updates)
        {
            if (update.AdditionalProperties is { } properties
                && properties.TryGetValue<TurnDisposition>(out var disposition))
            {
                found = disposition;
            }
        }

        return found;
    }
}

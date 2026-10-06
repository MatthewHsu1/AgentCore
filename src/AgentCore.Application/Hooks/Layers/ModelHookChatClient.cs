using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>
    /// BeforeModel, AfterModel and AfterModelFailed over each model round trip of an agent, directly inside <see cref="ModelFacingChatClient"/>, so a
    /// hook sees what the model sees. The function-invoking client sits above, so every round passes here.
    /// </summary>
    /// <param name="inner">The vendor's client.</param>
    /// <param name="hooks">The hooks of this compile.</param>
    internal sealed class ModelHookChatClient(IChatClient inner, HookRuntime hooks) : DelegatingChatClient(inner)
    {
        /// <summary>The most times one round is retried after a hook's <c>Retry</c>.</summary>
        internal const int MaxRetries = 2;

        private readonly GateRunner _gates = hooks.Gates;

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Turn() is not { Hooks: { } session } turn)
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }

            ModelRound round = new(session, turn);
            ModelRoundGates.Asked asked = await ModelRoundGates.BeforeAsync(_gates, round, messages, options, cancellationToken).ConfigureAwait(false);

            ChatResponse response;
            Exception? recovered = null;
            if (asked.Answer is { } answered)
            {
                response = answered;
            }
            else
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        response = await base.GetResponseAsync(asked.Messages, asked.Options, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        ModelRoundGates.Recovery recovery = await ModelRoundGates
                            .FailedAsync(_gates, round, attempt, failure, canRetry: attempt < MaxRetries, cancellationToken).ConfigureAwait(false);

                        if (recovery.Retrying)
                        {
                            continue;
                        }

                        if (recovery.Answer is { } substitute)
                        {
                            response = substitute;
                            recovered = failure;
                            break;
                        }

                        round.Called(response: null, asked.Options, failure);
                        throw;
                    }
                }
            }

            response = await ModelRoundGates.AfterAsync(_gates, round, response, cancellationToken).ConfigureAwait(false);
            if (asked.Answer is null)
            {
                round.Called(response, asked.Options, recovered);
            }

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Turn() is not { Hooks: { } session } turn)
            {
                await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return update;
                }

                yield break;
            }

            ModelRound round = new(session, turn);
            ModelRoundGates.Asked asked = await ModelRoundGates.BeforeAsync(_gates, round, messages, options, cancellationToken).ConfigureAwait(false);

            // A round is buffered only when some hook may replace it.
            bool buffer = _gates.Overrides(GatePoint.AfterModel);
            bool collect = buffer || session.Wants<ModelCalled>();
            List<ChatResponseUpdate> seen = [];
            bool yielded = false;
            Exception? recovered = null;

            if (asked.Answer is { } answered)
            {
                seen.AddRange(answered.ToChatResponseUpdates());
            }
            else
            {
                for (int attempt = 0; ; attempt++)
                {
                    Exception? failure = null;
                    IAsyncEnumerator<ChatResponseUpdate>? stream = null;
                    try
                    {
                        stream = base.GetStreamingResponseAsync(asked.Messages, asked.Options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    }
                    catch (Exception thrown) when (thrown is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        failure = thrown;
                    }

                    if (stream is not null)
                    {
                        try
                        {
                            while (true)
                            {
                                bool more;
                                try
                                {
                                    more = await stream.MoveNextAsync().ConfigureAwait(false);
                                }
                                catch (Exception thrown) when (thrown is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                                {
                                    failure = thrown;
                                    break;
                                }

                                if (!more)
                                {
                                    break;
                                }

                                if (collect)
                                {
                                    seen.Add(stream.Current);
                                }

                                if (!buffer)
                                {
                                    // Set just before the yield, so it is true exactly when the caller has something.
                                    yielded = true;
                                    yield return stream.Current;
                                }
                            }
                        }
                        finally
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                        }
                    }

                    if (failure is null)
                    {
                        break;
                    }

                    ModelRoundGates.Recovery recovery = await ModelRoundGates
                        .FailedAsync(_gates, round, attempt, failure, canRetry: !yielded && attempt < MaxRetries, cancellationToken).ConfigureAwait(false);

                    if (recovery.Retrying)
                    {
                        seen.Clear();
                        continue;
                    }

                    if (recovery.Answer is { } substitute)
                    {
                        recovered = failure;
                        seen.Clear();
                        seen.AddRange(substitute.ToChatResponseUpdates());
                        if (yielded)
                        {
                            // Without an id of its own the answer would merge into the partial message the caller holds.
                            string messageId = Guid.NewGuid().ToString("N");
                            foreach (ChatResponseUpdate update in seen)
                            {
                                update.MessageId ??= messageId;
                            }
                        }

                        if (!buffer)
                        {
                            foreach (ChatResponseUpdate update in seen)
                            {
                                yield return update;
                            }
                        }

                        break;
                    }

                    round.Called(seen.Count > 0 ? seen.ToChatResponse() : null, asked.Options, failure);
                    ExceptionDispatchInfo.Throw(failure);
                }
            }

            if (!buffer && asked.Answer is null)
            {
                round.Called(collect ? seen.ToChatResponse() : null, asked.Options, recovered);
                yield break;
            }

            ChatResponse collected = seen.ToChatResponse();
            ChatResponse final = await ModelRoundGates.AfterAsync(_gates, round, collected, cancellationToken).ConfigureAwait(false);
            if (asked.Answer is null)
            {
                round.Called(final, asked.Options, recovered);
            }

            foreach (ChatResponseUpdate update in ReferenceEquals(final, collected) ? seen : [.. final.ToChatResponseUpdates()])
            {
                yield return update;
            }
        }

        private static TurnInvocation? Turn()
        {
            return TurnInvocation.From(AIAgent.CurrentRunContext?.RunOptions);
        }
    }
}

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// Answers a run that threw, or one that spoke no words at all, with the spoken fallback.
    /// </summary>
    internal sealed class FallbackAgent : DelegatingAIAgent
    {
        private readonly string _fallbackReply;

        private readonly IReadOnlySet<string>? _outputAgents;

        /// <summary>Puts the fallback in front of one turn agent.</summary>
        /// <param name="inner">The agent a turn runs.</param>
        /// <param name="fallbackReply">What a failed turn speaks.</param>
        /// <param name="outputAgents">
        /// The <c>agents.items</c> ids whose reply the caller hears, or <see langword="null"/> when the
        /// last thing the run produced is that reply. It is null for rows 1 and 2, which run one agent,
        /// and for the graph patterns where any participant may answer last.
        /// </param>
        public FallbackAgent(AIAgent inner, string fallbackReply, IReadOnlySet<string>? outputAgents = null)
            : base(inner)
        {
            ArgumentNullException.ThrowIfNull(fallbackReply);
            _fallbackReply = fallbackReply;
            _outputAgents = outputAgents;
        }

        /// <inheritdoc />
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            AgentResponse response;
            try
            {
                response = await base.RunCoreAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Section 8.7, row six: the run throws, the turn ends, and the conversation lives.
            catch (Exception exception) when (IsFault(exception, cancellationToken))
#pragma warning restore CA1031
            {
                AgentResponse spoken = new(new ChatMessage(ChatRole.Assistant, _fallbackReply));
                spoken.AdditionalProperties ??= [];
                spoken.AdditionalProperties.Add(Disposition(FallbackCause.Faulted, exception));
                return spoken;
            }

            if (!string.IsNullOrWhiteSpace(SpokenText(response)) || HasApprovalRequest(response.Messages))
            {
                return response;
            }

            response.Messages.Add(new ChatMessage(ChatRole.Assistant, _fallbackReply));
            response.AdditionalProperties ??= [];
            response.AdditionalProperties.Add(Disposition(FallbackCause.EmptyReply, null));
            return response;
        }

        /// <inheritdoc />
        /// <remarks>
        /// C# forbids <c>yield return</c> inside a <c>catch</c> (CS1631), so the inner enumerator is
        /// driven by hand: the fault goes to a local, and the fallback is emitted after the try/catch.
        /// </remarks>
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IAsyncEnumerator<AgentResponseUpdate> updates = base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            bool spokeText = false;
            FallbackCause cause = FallbackCause.None;
            Exception? reason = null;

            try
            {
                while (true)
                {
                    AgentResponseUpdate? current = null;
                    try
                    {
                        if (await updates.MoveNextAsync().ConfigureAwait(false))
                        {
                            current = updates.Current;
                        }
                    }
#pragma warning disable CA1031 // Section 8.7, row six. See the buffered path above.
                    catch (Exception exception) when (IsFault(exception, cancellationToken))
#pragma warning restore CA1031
                    {
                        cause = FallbackCause.Faulted;
                        reason = exception;
                    }

                    if (cause is not FallbackCause.None || current is null)
                    {
                        break;
                    }

                    spokeText = spokeText || HasApprovalRequest(current) || (IsOutput(current.AuthorName) && !string.IsNullOrWhiteSpace(current.Text));
                    yield return current;
                }
            }
            finally
            {
                await updates.DisposeAsync().ConfigureAwait(false);
            }

            if (cause is FallbackCause.None && !spokeText)
            {
                cause = FallbackCause.EmptyReply;
            }

            if (cause is not FallbackCause.None)
            {
                AgentResponseUpdate spoken = new(ChatRole.Assistant, _fallbackReply);
                spoken.AdditionalProperties ??= [];
                spoken.AdditionalProperties.Add(Disposition(cause, reason));
                yield return spoken;
            }
        }

        /// <summary>
        /// Distinguishes a caller cancel from every other <see cref="OperationCanceledException"/>: only the
        /// former is not a fault.
        /// </summary>
        /// <param name="exception">What the run or the stream threw.</param>
        /// <param name="cancellationToken">The token this run was cancelled by, if the caller cancelled it.</param>
        /// <returns><see langword="true"/> when the turn should take the fallback rather than propagate.</returns>
        private static bool IsFault(Exception exception, CancellationToken cancellationToken)
        {
            return exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
        }

        /// <summary>Reads whether the run asks the caller to approve a tool call.</summary>
        /// <param name="messages">What the inner agent answered.</param>
        /// <returns>Whether any message carries an approval request.</returns>
        private static bool HasApprovalRequest(IEnumerable<ChatMessage> messages)
        {
            return messages.Any(message => HasApprovalRequest(message.Contents));
        }

        /// <summary>Reads whether one update asks the caller to approve a tool call.</summary>
        /// <param name="update">One update of the run.</param>
        /// <returns>Whether the update carries an approval request.</returns>
        private static bool HasApprovalRequest(AgentResponseUpdate update)
        {
            return HasApprovalRequest(update.Contents);
        }

        private static bool HasApprovalRequest(IEnumerable<AIContent> contents)
        {
            return contents.OfType<ToolApprovalRequestContent>().Any();
        }

        /// <summary>Reads the words this run would put in the caller's ear.</summary>
        /// <param name="response">What the inner agent answered.</param>
        /// <returns>The spoken text, or an empty string when nothing the caller hears was produced.</returns>
        private string SpokenText(AgentResponse response)
        {
            return ReplyText.From(response.Messages, _outputAgents);
        }

        /// <summary>Reads whether the caller hears what this author said.</summary>
        /// <param name="authorName">The node that produced the message or the update, if any.</param>
        /// <returns><see langword="true"/> when the text counts as the reply.</returns>
        private bool IsOutput(string? authorName)
        {
            return _outputAgents is null || (authorName is not null && _outputAgents.Contains(authorName));
        }

        /// <summary>
        /// What this layer alone knows. <see cref="ModerationAgent"/> folds its own verdict in above.
        /// </summary>
        private static TurnDisposition Disposition(FallbackCause cause, Exception? reason)
        {
            return new(Moderation: null, FlaggedCategories: null, cause, reason, ModerationReason: null);
        }
    }
}

using System.Runtime.CompilerServices;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Runtime.Agents
{
    /// <summary>
    /// Refuses a turn whose caller text the moderation endpoint flagged, before the agent runs.
    /// </summary>
    internal sealed class ModerationAgent : DelegatingAIAgent
    {
        /// <summary>How long a turn waits for the moderation endpoint before it answers anyway.</summary>
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

        private readonly PromptModerator _moderator;

        private readonly string _refusalReply;

        private readonly TimeSpan _timeout;

        /// <summary>Puts moderation in front of one turn agent.</summary>
        /// <param name="inner">The agent a turn runs.</param>
        /// <param name="moderator">The endpoint seam.</param>
        /// <param name="refusalReply">What a refused turn speaks. Never the fallback line.</param>
        /// <param name="timeout">How long the caller waits for a verdict before the turn runs anyway.</param>
        public ModerationAgent(AIAgent inner, PromptModerator moderator, string refusalReply, TimeSpan timeout)
            : base(inner)
        {
            ArgumentNullException.ThrowIfNull(moderator);
            ArgumentNullException.ThrowIfNull(refusalReply);

            _moderator = moderator;
            _refusalReply = refusalReply;
            _timeout = timeout;
        }

        /// <inheritdoc />
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            TurnDisposition verdict = await JudgeAsync(messages, options, cancellationToken).ConfigureAwait(false);
            if (verdict.Moderation is ModerationOutcome.Flagged)
            {
                AgentResponse refusal = new(new ChatMessage(ChatRole.Assistant, _refusalReply));
                Attach(refusal.AdditionalProperties ??= [], verdict);
                return refusal;
            }

            AgentResponse response = await base.RunCoreAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);

            Attach(response.AdditionalProperties ??= [], verdict);
            return response;
        }

        /// <inheritdoc />
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            return RunCoreStreamingCoreAsync(messages, session, options, cancellationToken);
        }

        /// <summary>Streams the verdict marker and the run, or the refusal when the caller is flagged.</summary>
        private async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            TurnDisposition verdict = await JudgeAsync(messages, options, cancellationToken).ConfigureAwait(false);
            if (verdict.Moderation is ModerationOutcome.Flagged)
            {
                AgentResponseUpdate refusal = new(ChatRole.Assistant, _refusalReply);
                Attach(refusal.AdditionalProperties ??= [], verdict);
                yield return refusal;
                yield break;
            }

            AgentResponseUpdate marker = new() { Role = ChatRole.Assistant };
            Attach(marker.AdditionalProperties ??= [], verdict);
            yield return marker;

            await foreach (AgentResponseUpdate? update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }

        /// <summary>Decides, then raises the verdict at once, so it is stamped when decided and not at the seal.</summary>
        /// <param name="messages">The whole request. The caller's words are its last user message.</param>
        /// <param name="options">The run's options, which carry the turn the verdict is raised for.</param>
        /// <param name="cancellationToken">The host's own token. A cancel it asked for still propagates.</param>
        /// <returns>The verdict, as the disposition the turn loop reads.</returns>
        private async ValueTask<TurnDisposition> JudgeAsync(
            IEnumerable<ChatMessage> messages,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            TurnDisposition verdict = await DecideAsync(messages, cancellationToken).ConfigureAwait(false);

            if (TurnInvocation.From(options) is { Hooks: { } hooks } turn)
            {
                _ = hooks.Raise(new InputModerated(
                    hooks.Scope(turn.TurnIndex, turn.Stage),
                    verdict.Moderation switch
                    {
                        ModerationOutcome.Flagged => InputVerdict.Flagged,
                        ModerationOutcome.Unavailable => InputVerdict.Unavailable,
                        _ => InputVerdict.Clean,
                    },
                    verdict.FlaggedCategories is { Length: > 0 } joined ? joined.Split(',') : [],
                    verdict.ModerationReason switch
                    {
                        TurnFailureReasons.ModerationTimedOut => ModerationUnavailableReason.TimedOut,
                        TurnFailureReasons.ModerationFaulted => ModerationUnavailableReason.Threw,
                        _ => null,
                    }));
            }

            return verdict;
        }

        /// <summary>Reads the endpoint's verdict on what the caller said, and never lets it drop a turn.</summary>
        /// <param name="messages">The whole request. The caller's words are its last user message.</param>
        /// <param name="cancellationToken">The host's own token. A cancel it asked for still propagates.</param>
        /// <returns>The verdict, as the disposition the turn loop reads.</returns>
        private async ValueTask<TurnDisposition> DecideAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken)
        {
            string callerText = CallerText(messages);

            IReadOnlyList<string> categories;
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(_timeout);
                try
                {
                    categories = await _moderator.FlaggedCategoriesAsync(callerText, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The deadline passed, and not the caller's own token. A cancel the host asked for
                    // still propagates, because that is the host ending the turn and not a slow vendor.
                    return Unavailable(TurnFailureReasons.ModerationTimedOut);
                }
#pragma warning disable CA1031 // Moderation guards the turn. It must never be the thing that drops it.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    return Unavailable(TurnFailureReasons.ModerationFaulted);
                }
            }

            return categories.Count == 0
                ? new TurnDisposition(ModerationOutcome.Clean, null, FallbackCause.None, null, null)

                // The order is the endpoint's, because AuditPayloadKeys.ModerationCategories promises it.
                : new TurnDisposition(
                    ModerationOutcome.Flagged, string.Join(',', categories), FallbackCause.None, null, null);
        }

        private static TurnDisposition Unavailable(string reason)
        {
            return new(ModerationOutcome.Unavailable, null, FallbackCause.None, null, reason);
        }

        /// <summary>Reads the words the caller spoke this turn out of the whole request.</summary>
        /// <param name="messages">The request the turn is about to run.</param>
        /// <returns>The text of the last user message, or an empty string when the request holds none.</returns>
        private static string CallerText(IEnumerable<ChatMessage> messages)
        {
            string? spoken = null;
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.User)
                {
                    spoken = message.Text;
                }
            }

            return spoken ?? string.Empty;
        }

        /// <summary>Puts the verdict on the run, folding in what the fallback layer already said.</summary>
        /// <param name="properties">Where the turn loop reads the marker from.</param>
        /// <param name="verdict">What moderation decided.</param>
        private static void Attach(AdditionalPropertiesDictionary properties, TurnDisposition verdict)
        {
            if (properties.TryGetValue(out TurnDisposition? existing) && existing is not null)
            {
                verdict = verdict with { Fallback = existing.Fallback, FallbackFault = existing.FallbackFault };
                _ = properties.Remove<TurnDisposition>();
            }

            properties.Add(verdict);
        }
    }
}

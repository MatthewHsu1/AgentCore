using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// Holds a person's approval answers back until every request of the round has one, so a round that asked for
    /// two or more approvals can be answered one request at a time.
    /// </summary>
    internal static class ApprovalAnswerHold
    {
        /// <summary>Adds the layer. It goes directly around the agent, inside MAF's approval layer when there is one.</summary>
        internal static AIAgentBuilder Use(AIAgentBuilder builder)
        {
            return builder.Use(
                runFunc: static (messages, session, options, inner, cancellationToken) => RunAsync(messages, session, options, inner, cancellationToken),
                runStreamingFunc: static (messages, session, options, inner, cancellationToken) => StreamAsync(messages, session, options, inner, cancellationToken));
        }

        private static async Task<AgentResponse> RunAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner, CancellationToken cancellationToken)
        {
            List<ChatMessage> input = [.. messages];
            List<AIContent> open = Take(input, session, options);
            return open.Count > 0
                ? new AgentResponse(new ChatMessage(ChatRole.Assistant, open))
                : await inner.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
        }

        private static async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent inner,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            List<ChatMessage> input = [.. messages];
            List<AIContent> open = Take(input, session, options);
            if (open.Count > 0)
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, open);
                yield break;
            }

            await foreach (AgentResponseUpdate update in inner.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        /// <summary>
        /// Takes in the run's answers. Returns the requests still open when some are, after holding the answers;
        /// returns nothing when the run may go on, after noting every refused call and dropping what was held.
        /// </summary>
        private static List<AIContent> Take(List<ChatMessage> input, AgentSession? session, AgentRunOptions? options)
        {
            List<ToolApprovalResponseContent> answers = [.. input.SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>()];
            if (session is null || answers.Count == 0)
            {
                return [];
            }

            IReadOnlyList<ToolApprovalRequestContent> queued = PendingApprovalQueue.Requests(session);
            List<ToolApprovalResponseContent> held = PendingApprovalQueue.Held(session);
            HashSet<string> answered = new(held.Concat(answers).Select(static answer => answer.RequestId), StringComparer.Ordinal);
            List<ToolApprovalRequestContent> open = [.. queued.Where(request => !answered.Contains(request.RequestId))];
            TurnInvocation? turn = TurnInvocation.From(options);

            if (open.Count == 0)
            {
                foreach (ToolApprovalResponseContent refused in held.Concat(answers).Where(static answer => !answer.Approved))
                {
                    turn?.Results?.NoteDenied(refused.ToolCall.CallId);
                }

                PendingApprovalQueue.Hold(session, []);
                return [];
            }

            HashSet<string> asked = new(queued.Select(static request => request.RequestId), StringComparer.Ordinal);
            HashSet<string> kept = new(held.Select(static answer => answer.RequestId), StringComparer.Ordinal);
            held.AddRange(answers.Where(answer => asked.Contains(answer.RequestId) && kept.Add(answer.RequestId)));
            PendingApprovalQueue.Hold(session, held);

            foreach (ToolApprovalRequestContent request in open)
            {
                turn?.Results?.NoteReshown(request.ToolCall.CallId);
            }

            return [.. open];
        }
    }
}

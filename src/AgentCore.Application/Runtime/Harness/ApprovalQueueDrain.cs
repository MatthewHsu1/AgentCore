using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// Lets a message that answers requests and carries new words reach the model through MAF's approval layer.
    /// </summary>
    internal static class ApprovalQueueDrain
    {
        /// <summary>Adds the layer. It goes directly outside <c>UseToolApproval</c>.</summary>
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
            (HashSet<string> answered, List<ChatMessage> words) = Split(input);

            for (int pass = 0; ; pass++)
            {
                AgentResponse response = await inner.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
                if (pass >= answered.Count || !OnlyAnswered(response.Messages.SelectMany(static message => message.Contents), answered))
                {
                    return response;
                }

                input = words;
            }
        }

        private static async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent inner,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            List<ChatMessage> input = [.. messages];
            (HashSet<string> answered, List<ChatMessage> words) = Split(input);

            for (int pass = 0; ; pass++)
            {
                List<AgentResponseUpdate> held = [];
                bool spoke = false;
                await foreach (AgentResponseUpdate update in inner.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false))
                {
                    if (update.Contents.OfType<ToolApprovalRequestContent>().Any())
                    {
                        held.Add(update);
                        continue;
                    }

                    spoke = spoke || update.Contents.Any(static content => content is not TextContent { Text.Length: 0 });
                    yield return update;
                }

                if (spoke || pass >= answered.Count || !OnlyAnswered(held.SelectMany(static update => update.Contents), answered))
                {
                    foreach (AgentResponseUpdate kept in held)
                    {
                        yield return kept;
                    }

                    yield break;
                }

                input = words;
            }
        }

        /// <summary>The request ids the message answers, and the message without its answers; no answer or no words, no replay.</summary>
        private static (HashSet<string> Answered, List<ChatMessage> Words) Split(List<ChatMessage> input)
        {
            HashSet<string> answered = new(
                input.SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>().Select(static answer => answer.RequestId),
                StringComparer.Ordinal);

            List<ChatMessage> words = [];
            foreach (ChatMessage message in input)
            {
                List<AIContent> rest = [.. message.Contents.Where(static content => content is not ToolApprovalResponseContent)];
                if (rest.Count > 0)
                {
                    ChatMessage kept = message.Clone();
                    kept.Contents = rest;
                    words.Add(kept);
                }
            }

            return words.Count == 0 ? ([], words) : (answered, words);
        }

        /// <summary>Whether the run only handed back requests the message already answered.</summary>
        private static bool OnlyAnswered(IEnumerable<AIContent> contents, HashSet<string> answered)
        {
            List<AIContent> said = [.. contents.Where(static content => content is not TextContent { Text.Length: 0 })];
            return said.Count > 0
                && said.All(content => content is ToolApprovalRequestContent request && answered.Contains(request.RequestId));
        }
    }
}

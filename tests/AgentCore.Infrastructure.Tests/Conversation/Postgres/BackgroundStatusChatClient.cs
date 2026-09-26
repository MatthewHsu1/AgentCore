using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// A parent model that starts one <c>background:</c> task when the caller says <c>go</c>, asks for every task's
    /// status when the caller says <c>status?</c>, and otherwise answers in words. It keeps what each status call returned.
    /// </summary>
    internal sealed class BackgroundStatusChatClient : IChatClient
    {
        /// <summary>Gets what each status call returned, in call order.</summary>
        public List<string> Statuses { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> request = [.. messages];
            await Task.Yield();
            string id = Guid.NewGuid().ToString("N");

            int asked = request.FindLastIndex(message => message.Role == ChatRole.User);
            FunctionResultContent? answered = request.Skip(asked + 1).SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (answered is not null)
            {
                if (answered.CallId.StartsWith("status_", StringComparison.Ordinal))
                {
                    lock (Statuses)
                    {
                        Statuses.Add(answered.Result?.ToString() ?? string.Empty);
                    }
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "ok") { ResponseId = id, MessageId = id };
                yield break;
            }

            FunctionCallContent? call = (asked < 0 ? null : request[asked].Text) switch
            {
                "go" => new FunctionCallContent("start_" + id, "background_agents_start_task", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "blocker",
                    ["input"] = "work",
                    ["description"] = "d",
                }),
                "status?" => new FunctionCallContent("status_" + id, "background_agents_get_all_tasks", new Dictionary<string, object?>(StringComparer.Ordinal)),
                _ => null,
            };

            yield return call is null
                ? new ChatResponseUpdate(ChatRole.Assistant, "ok") { ResponseId = id, MessageId = id }
                : new ChatResponseUpdate(ChatRole.Assistant, [call]) { ResponseId = id, MessageId = id };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}

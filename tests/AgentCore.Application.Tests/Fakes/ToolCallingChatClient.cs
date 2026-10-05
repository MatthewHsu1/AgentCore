using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// A deterministic offline model that calls the first tool it is offered, once, then answers.
    /// </summary>
    internal sealed class ToolCallingChatClient(string reply, Dictionary<string, object?>? arguments = null, bool everyRun = false) : IChatClient
    {
        private const string ConversationId = "conversation_1";

        private readonly string _reply = reply;
        private readonly Dictionary<string, object?> _arguments = arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        private int _calls;

        /// <summary>Gets the text of the last user message of each request, in call order.</summary>
        public List<string> Prompts { get; } = [];

        /// <summary>Gets every tool result this client read back, in call order.</summary>
        public List<string> ToolResults { get; } = [];

        /// <summary>Gets the name of each tool this client asked to call.</summary>
        public List<string> Called { get; } = [];

        /// <summary>Gets every function this client was offered, in call order. This is what the model reads.</summary>
        public List<AIFunction> Offered { get; } = [];

        /// <summary>Gets how many requests this client answered.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            _ = Interlocked.Increment(ref _calls);
            await Task.Yield();

            List<ChatMessage> transcript = [.. messages];
            bool answered = false;

            lock (Prompts)
            {
                int lastUser = everyRun ? transcript.FindLastIndex(message => message.Role == ChatRole.User) : -1;
                foreach (ChatMessage? message in transcript.Skip(lastUser + 1))
                {
                    foreach (AIContent content in message.Contents)
                    {
                        if (content is FunctionResultContent result)
                        {
                            answered = true;
                            ToolResults.Add(result.Result?.ToString() ?? string.Empty);
                        }
                    }
                }

                ChatMessage? prompt = transcript.LastOrDefault(message => message.Role == ChatRole.User);
                Prompts.Add(prompt?.Text ?? string.Empty);
            }

            string responseId = Guid.NewGuid().ToString("N");
            List<AIFunction> functions = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
            AIFunction? tool = functions.FirstOrDefault();

            lock (Prompts)
            {
                Offered.AddRange(functions);
            }

            if (tool is not null && !answered)
            {
                lock (Prompts)
                {
                    Called.Add(tool.Name);
                }

                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(ConversationId, tool.Name, _arguments)])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}

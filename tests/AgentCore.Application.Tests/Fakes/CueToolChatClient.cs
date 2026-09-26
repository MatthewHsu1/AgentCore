using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// Calls one offered tool, once, when the user's words since the last reply hold <paramref name="cue"/>, and answers
    /// text otherwise. The tool is the first whose name ends in <c>_add</c>, else the first offered.
    /// </summary>
    /// <param name="cue">The words that ask for the tool.</param>
    /// <param name="arguments">Builds the call's arguments.</param>
    /// <param name="answer">What every other request is answered.</param>
    internal sealed class CueToolChatClient(string cue, Func<Dictionary<string, object?>> arguments, string answer = "ok") : IChatClient
    {
        /// <summary>Gets every request this client read, in call order.</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> transcript = [.. messages];
            lock (Requests)
            {
                Requests.Add(transcript);
            }

            await Task.Yield();

            string id = Guid.NewGuid().ToString("N");
            bool answered = transcript.Count > 0 && transcript[^1].Contents.OfType<FunctionResultContent>().Any();
            int lastReply = transcript.FindLastIndex(message => message.Role == ChatRole.Assistant);
            bool cued = transcript.Skip(lastReply + 1)
                .Any(message => message.Role == ChatRole.User && message.Text.Contains(cue, StringComparison.Ordinal));
            AIFunction? tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault(function => function.Name.EndsWith("_add", StringComparison.Ordinal))
                ?? options?.Tools?.OfType<AIFunction>().FirstOrDefault();

            if (!answered && cued && tool is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call_" + id, tool.Name, arguments())])
                {
                    ResponseId = id,
                    MessageId = id,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, answer) { ResponseId = id, MessageId = id };
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

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A deterministic offline model: on a message with no tool result yet, calls whatever the first
    /// offered tool is with <see cref="Command"/>; once it sees the tool's result, records it and answers
    /// "done". Works unmodified across separate turns because each turn's last message before the
    /// model runs is the fresh user input, not an earlier turn's tool result — so the same instance
    /// drives as many turns as a test needs, one shell pid recorded per turn.
    /// </summary>
    internal sealed class ShellPidChatClient : IChatClient
    {
        public List<string> ToolResults { get; } = [];

        /// <summary>Gets the user words of every request the model was sent, one list per request, oldest first.</summary>
        public List<List<string>> UserWords { get; } = [];

        /// <summary>Gets or sets the shell command the next tool call runs. Its first line of output must be the pid.</summary>
        public string Command { get; set; } = "echo $$";

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            List<ChatMessage> transcript = [.. messages];
            UserWords.Add([.. transcript.Where(message => message.Role == ChatRole.User && message.Text.Length > 0).Select(message => message.Text)]);
            string responseId = Guid.NewGuid().ToString("N");

            FunctionResultContent? result = transcript.Count > 0
                ? transcript[^1].Contents.OfType<FunctionResultContent>().FirstOrDefault()
                : null;

            if (result is not null)
            {
                ToolResults.Add(result.Result?.ToString() ?? string.Empty);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            AIFunction tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault()
                ?? throw new InvalidOperationException("The request offered no shell tool for the client to call.");

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "call_" + Guid.NewGuid().ToString("N"),
                    tool.Name,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = Command })])
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
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken)
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
        }

        /// <summary>Reads the pid off the first line of a <c>run_shell</c> tool result, as MAF formats it.</summary>
        internal static int ParsePid(string toolResult)
        {
            return int.Parse(toolResult.Split('\n', StringSplitOptions.TrimEntries)[0]);
        }
    }
}

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// Calls the first offered tool, once, when the newest user message holds <paramref name="cue"/>, and answers
    /// <paramref name="answer"/> otherwise. A reply to the words in <see cref="Held"/> waits for <see cref="Release"/>.
    /// </summary>
    /// <param name="cue">The words that ask for the tool.</param>
    /// <param name="answer">What every other request is answered.</param>
    internal sealed class NewestWordsToolChatClient(string cue, string answer = "ok") : IChatClient
    {
        /// <summary>Gets every request this client read, in call order.</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        /// <summary>Gets or sets the user words whose reply waits for <see cref="Release"/>, or <see langword="null"/>.</summary>
        public string? Held { get; set; }

        /// <summary>Completes once the reply to <see cref="Held"/> was asked for.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the reply to <see cref="Held"/> go.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> request = [.. messages];
            lock (Requests)
            {
                Requests.Add(request);
            }

            await Task.Yield();

            string id = Guid.NewGuid().ToString("N");
            int newest = request.FindLastIndex(message => message.Role == ChatRole.User);
            string words = newest < 0 ? string.Empty : request[newest].Text;
            bool answered = request.Skip(newest + 1).Any(message => message.Contents.OfType<FunctionResultContent>().Any());
            if (!answered && words.Contains(cue, StringComparison.Ordinal) && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call_" + id, tool.Name)]) { MessageId = id };
                yield break;
            }

            if (words == Held)
            {
                _ = Holding.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, answer) { MessageId = id };
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

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// A model that answers each call from a script: text in fragments, one tool call, or a fault. Ported from the
    /// design probes' <c>Script</c> (docs/probes/maf-native-engine/ProviderOwnsWritesProbes.cs).
    /// </summary>
    internal sealed class TurnScriptChatClient : IChatClient
    {
        private readonly List<(string? Tool, bool Fault, string[] Fragments)> _steps;

        private int _calls;

        private TurnScriptChatClient(List<(string?, bool, string[])> steps)
        {
            _steps = steps;
        }

        /// <summary>Gets how many calls reached the model.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Gets the messages of every call, in order.</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        /// <summary>Gets or sets the call, from zero, that waits for <see cref="Release"/> before it answers; -1 for none.</summary>
        public int GateBeforeCall { get; set; } = -1;

        /// <summary>Gets the signal that the gated call is waiting.</summary>
        public TaskCompletionSource Gated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the gate the gated call waits on.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the signal that the first call reached the model.</summary>
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets or sets the pause before each text fragment, in milliseconds.</summary>
        public int FragmentDelayMs { get; set; }

        /// <summary>Answers every call with the same text, in fragments.</summary>
        public static TurnScriptChatClient Text(params string[] fragments)
        {
            return new([(null, false, fragments)]);
        }

        /// <summary>Calls one tool, then answers with text.</summary>
        public static TurnScriptChatClient ToolThenText(string tool, params string[] fragments)
        {
            return new([(tool, false, []), (null, false, fragments)]);
        }

        /// <summary>Calls one tool, then throws.</summary>
        public static TurnScriptChatClient ToolThenFault(string tool)
        {
            return new([(tool, false, []), (null, true, [])]);
        }

        /// <summary>Answers each call with the next reply.</summary>
        public static TurnScriptChatClient Sequence(params string[][] replies)
        {
            return new([.. replies.Select(reply => ((string?)null, false, reply))]);
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref _calls) - 1;
            lock (Requests)
            {
                Requests.Add([.. messages]);
            }

            (string? tool, bool fault, string[] fragments) = _steps[Math.Min(index, _steps.Count - 1)];
            string id = $"m{index}";
            _ = Called.TrySetResult();
            await Task.Yield();
            if (index == GateBeforeCall)
            {
                _ = Gated.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            if (fault)
            {
                throw new InvalidOperationException("model down");
            }

            if (tool is not null)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant, [new FunctionCallContent($"c{index}", tool, new Dictionary<string, object?>())])
                {
                    MessageId = id,
                    ResponseId = id,
                };
                yield break;
            }

            foreach (string fragment in fragments)
            {
                if (FragmentDelayMs > 0)
                {
                    await Task.Delay(FragmentDelayMs, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, fragment) { MessageId = id, ResponseId = id };
            }
        }

        /// <inheritdoc />
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}

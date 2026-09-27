using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// A final prompt that lands while a step still streams stops the engine turn at once, as LiveKit cancels its
    /// generation on an interruption (<c>agent_activity.py:3811-3830</c>), and the relay's report of what was heard
    /// still settles the turn's text before the next turn starts (<c>generation.py:748-768</c>).
    /// </summary>
    public sealed class TelnyxRelayFinalPromptCutTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        // Less than VoiceOptions.DefaultHeardTextWait: a turn left running through the wait resumes inside it.
        private static readonly TimeSpan ResumeAfter = TimeSpan.FromSeconds(1);

        [Fact(Timeout = 30_000)]
        public async Task AFinalPromptMidStep_StopsTheTurnAtOnce_SoItsToolNeverRuns()
        {
            GatedStepChatClient model = new();
            int toolCalls = 0;
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TwoStepChatClient.Yaml,
                model,
                options => options.Bind(TwoStepChatClient.ToolBinding, (_, _) =>
                {
                    _ = Interlocked.Increment(ref toolCalls);
                    return ValueTask.FromResult<object?>("42");
                }));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "final-prompt-stop"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReadTokensUntilAsync(relay, "Hello there, ");
            await model.Held.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

            await relay.SendAsync(RelayFrames.Prompt("wait", last: true));
            _ = await Task.WhenAny(model.Stopped.Task, Task.Delay(ResumeAfter, TestContext.Current.CancellationToken));
            model.Resume();

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "final-prompt-stop", 4);
            Assert.Equal(0, Volatile.Read(ref toolCalls));
            Assert.DoesNotContain(model.Requests, request => request[^1].Role == ChatRole.Tool);
            StoredRows.AssertTurn(
                rows,
                ("user", "hi", false, false),
                ("assistant", "Hello there,", false, false),
                ("user", "wait", false, false),
                ("assistant", "Sure.", false, false));
        }

        [Fact(Timeout = 30_000)]
        public async Task AFinalPromptMidStep_ThenTheRelaysReport_KeepsTheHeardText()
        {
            GatedStepChatClient model = new();
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool);
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "final-prompt-heard"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReadTokensUntilAsync(relay, "Hello there, ");
            await model.Held.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

            await relay.SendAsync(RelayFrames.Prompt("wait", last: true));
            await model.Stopped.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await relay.SendAsync(RelayFrames.Interrupt("Hello", durationMs: 400));

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "final-prompt-heard", 4);
            StoredRows.AssertTurn(
                rows,
                ("user", "hi", false, false),
                ("assistant", "Hello", false, false),
                ("user", "wait", false, false),
                ("assistant", "Sure.", false, false));
            Assert.Equal(["Hello"], model.Requests.Last().Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text));
        }

        private static async Task ReadTokensUntilAsync(FakeRelayClient relay, string expected)
        {
            StringBuilder text = new();
            using CancellationTokenSource deadline = new(Bound);
            while (text.ToString() != expected)
            {
                JsonNode frame = await relay.ReadFrameAsync().WaitAsync(deadline.Token);
                if (frame["type"]?.GetValue<string>() == "text")
                {
                    _ = text.Append(frame["token"]!.GetValue<string>());
                }
            }
        }

        /// <summary>
        /// Step one says "Hello there, ", holds until <see cref="Resume"/> or until its request is cancelled, then
        /// says more and calls the tool. A request after a tool result, or for any later words, answers at once.
        /// </summary>
        private sealed class GatedStepChatClient : IChatClient
        {
            private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ConcurrentQueue<IReadOnlyList<ChatMessage>> Requests { get; } = new();

            public void Resume()
            {
                _ = _resume.TrySetResult();
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                List<ChatMessage> sent = [.. messages.Where(message => message.Role != ChatRole.System)];
                Requests.Enqueue(sent);
                string id = Guid.NewGuid().ToString("N");
                ChatMessage last = sent[^1];

                if (last.Role == ChatRole.Tool)
                {
                    yield return Text("Done.", id);
                    yield break;
                }

                if (last.Text != "hi")
                {
                    yield return Text("Sure.", id);
                    yield break;
                }

                yield return Text("Hello there, ", id);
                _ = Held.TrySetResult();
                try
                {
                    await _resume.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _ = Stopped.TrySetResult();
                    throw;
                }

                yield return Text("and more.", id);
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call_1", options!.Tools!.OfType<AIFunction>().First().Name, new Dictionary<string, object?>())])
                {
                    ResponseId = id,
                    MessageId = id,
                };
            }

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

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
            }

            private static ChatResponseUpdate Text(string text, string id)
            {
                return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = id, MessageId = id };
            }
        }
    }
}

using System.Runtime.CompilerServices;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A model whose reply runs in two steps: step one speaks and calls the document's one tool, step two speaks
    /// after the tool result. Either step can stop at <see cref="Pause"/> until the request is cancelled. Every
    /// request after a tool result runs step two again, so a step two that calls a failing tool calls it until
    /// the tool budget ends the run.
    /// </summary>
    /// <param name="stepOne">The fragments of step one; <see cref="Call"/> marks where the tool call goes.</param>
    /// <param name="stepTwo">The fragments of step two.</param>
    internal sealed class TwoStepChatClient(object[] stepOne, object[] stepTwo) : IChatClient
    {
        /// <summary>The binding name the tool of <see cref="Yaml"/> binds to.</summary>
        public const string ToolBinding = "LookItUp";

        /// <summary>A document with one agent and one tool, bound to <see cref="ToolBinding"/>.</summary>
        public const string Yaml =
            """
            apiVersion: agentcore/v1
            tools:
              - id: look_it_up
                kind: binding
                binds: LookItUp
                description: Look something up for the caller.
                parameters:
                  type: object
                  properties: { what: { type: string } }
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller", tools: [ look_it_up ] }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            entries:
              main:
                agent: greeter
            """;

        /// <summary>Marks where a step stops until its request is cancelled.</summary>
        public static readonly object Pause = new();

        /// <summary>Marks where step one calls the tool.</summary>
        public static readonly object Call = new();

        private int _calls;

        /// <summary>Gets a task that completes when a step reaches <see cref="Pause"/>.</summary>
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Binds the tool of <see cref="Yaml"/> to one that answers at once.</summary>
        public static void BindTool(AgentCoreOptions options)
        {
            _ = options.Bind(ToolBinding, (_, _) => ValueTask.FromResult<object?>("42"));
        }

        /// <summary>
        /// Binds the tool of <see cref="Yaml"/> to one that answers its first call and then fails as an unreachable
        /// endpoint does, which counts toward the budget that ends the run on the fourth failure in a row.
        /// </summary>
        public static void BindToolThatFailsAfterItsFirstCall(AgentCoreOptions options)
        {
            int calls = 0;
            _ = options.Bind(ToolBinding, (_, _) => Interlocked.Increment(ref calls) == 1
                ? ValueTask.FromResult<object?>("42")
                : throw new HttpRequestException("the order service is down"));
        }

        /// <summary>Gets <see cref="Yaml"/> with its own fallback line.</summary>
        public static string YamlWithFallback(string fallbackReply)
        {
            return Yaml.Replace("apiVersion: agentcore/v1\n", $"apiVersion: agentcore/v1\nfallbackReply: \"{fallbackReply}\"\n", StringComparison.Ordinal);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            bool answered = messages.LastOrDefault()?.Role == ChatRole.Tool;
            string id = Guid.NewGuid().ToString("N");

            foreach (object item in answered ? stepTwo : stepOne)
            {
                await Task.Yield();
                if (ReferenceEquals(item, Pause))
                {
                    _ = Paused.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    continue;
                }

                AIContent content = ReferenceEquals(item, Call)
                    ? new FunctionCallContent("call_" + Interlocked.Increment(ref _calls), options!.Tools!.OfType<AIFunction>().First().Name, new Dictionary<string, object?>())
                    : new TextContent((string)item);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { ResponseId = id, MessageId = id };
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
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
    }
}

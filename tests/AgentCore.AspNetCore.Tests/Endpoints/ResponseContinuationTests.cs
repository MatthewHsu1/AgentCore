using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// Response continuation after the 2026-09-26 redesign (D1-D4): a response id stores no snapshot of
    /// its own, an old <c>previous_response_id</c> continues from the conversation's current state rather
    /// than branching, and response ids — never conversations — are what the retention sweep ages out.
    /// </summary>
    public sealed class ResponseContinuationTests
    {
        private const string SimpleYaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller" }
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

        private const string TestStoreYaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller" }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
              conversations: { kind: test }
            entries:
              main:
                agent: greeter
            """;

        private const string TodoYaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: solo, instructions: "answer one", todos: true }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
              conversations: { kind: test }
            entries:
              main:
                agent: solo
            """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Promoted from the response-continuation probe (Q3): an old previous_response_id is not a
        // branch (D2). It runs at the conversation's current turn index and sees the current todos, live
        // and after the session has unloaded and rebuilt from store 0.
        [Theory(Timeout = 60_000)]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnOldResponseId_ContinuesAtTheCurrentTurnIndex_AndSeesTheCurrentTodos(bool unloadFirst)
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            CountingConversationStore store = new();
            ScriptedClient client = new(todoOnTurns: [1, 2]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TodoYaml,
                client,
                o =>
                {
                    _ = o.UseConversationStores(store);
                    o.TimeProvider = clock;
                });

            JsonNode first = await PostAsync(host, null, "t1 first question");
            string r1 = first["id"]!.GetValue<string>();
            string conversation = first["conversation"]!["id"]!.GetValue<string>();
            _ = await PostAsync(host, r1, "t2 second question");

            if (unloadFirst)
            {
                clock.Advance(TimeSpan.FromMinutes(11));
            }

            JsonNode third = await PostAsync(host, r1, "t3 branch question");

            // A real branch off r1 would run as turn 1. It runs as turn 2, after t2.
            Assert.Equal("2", third["metadata"]!["turn_index"]!.GetValue<string>());
            Assert.Equal(conversation, third["conversation"]!["id"]!.GetValue<string>());

            IReadOnlyList<ChatMessage> seen = client.Requests[^1];
            Assert.Contains(seen, m => m.Text.Contains("t2 second question", StringComparison.Ordinal));
            Assert.Contains(seen, m => m.Text.Contains("item-2", StringComparison.Ordinal));
        }

        // B1: a chained turn carries no conversation id of its own (the request only names
        // previous_response_id), so the answer must resolve the real owner rather than echo a made-up one.
        [Fact(Timeout = 60_000)]
        public async Task AChainedAnswer_ConversationId_EqualsTheConversationTheFirstTurnCreated()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(SimpleYaml, reply);

            JsonNode first = await PostAsync(host, null, "first question");
            string conversation = first["conversation"]!["id"]!.GetValue<string>();
            string r1 = first["id"]!.GetValue<string>();

            JsonNode second = await PostAsync(host, r1, "second question");

            Assert.Equal(conversation, second["conversation"]!["id"]!.GetValue<string>());
        }

        // Drives the real hosted ConversationSweeper, not a direct SweepAsync call: the same clock backs
        // its PeriodicTimer and the store's own created_at, so one Advance past both the hourly tick and
        // the retention window lets production code do the sweeping.
        [Fact(Timeout = 60_000)]
        public async Task AResponseIdOlderThanResponseRetention_AfterTheHostedSweeperRuns_Returns404()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            SweepSignalingConversationStore store = new(clock);
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TestStoreYaml,
                reply,
                o =>
                {
                    _ = o.UseConversationStores(store);
                    o.TimeProvider = clock;
                    o.ResponseRetention = TimeSpan.FromDays(30);
                });

            JsonNode first = await PostAsync(host, null, "hello");
            string r1 = first["id"]!.GetValue<string>();

            Task<int> sweep = store.ArmNextSweep();
            clock.Advance(TimeSpan.FromDays(31));
            Assert.Equal(1, await sweep.WaitAsync(TimeSpan.FromSeconds(5), Ct));

            using HttpResponseMessage response = await host.PostAsync(
                $$"""{ "stream": false, "previous_response_id": "{{r1}}", "input": "again" }""");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            JsonNode body = await ResponsesHost.ReadJsonAsync(response);
            Assert.Equal("continuation_not_found", body["error"]!["code"]!.GetValue<string>());
        }

        // Same setup and the same time advance as the test above, so a flip of ResponseRetention alone
        // is what tells them apart: here the hosted sweeper's ExecuteAsync returns before it ever builds
        // a timer, so there is no pass to wait for.
        [Fact(Timeout = 60_000)]
        public async Task ResponseRetentionNull_TheHostedSweeperNeverRuns_SoTheSameIdStillResolves()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TestStoreYaml,
                reply,
                o =>
                {
                    _ = o.UseConversationStores(new TestConversationStoreAdapter(new InMemoryConversationStore(clock)));
                    o.TimeProvider = clock;
                    o.ResponseRetention = null;
                });

            JsonNode first = await PostAsync(host, null, "hello");
            string r1 = first["id"]!.GetValue<string>();

            clock.Advance(TimeSpan.FromDays(31));

            using HttpResponseMessage response = await host.PostAsync(
                $$"""{ "stream": false, "previous_response_id": "{{r1}}", "input": "again" }""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact(Timeout = 60_000)]
        public async Task AConversationId_KeepsResolving_AfterTheHostedSweeperTakesItsResponseIds()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            SweepSignalingConversationStore store = new(clock);
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TestStoreYaml,
                reply,
                o =>
                {
                    _ = o.UseConversationStores(store);
                    o.TimeProvider = clock;
                });

            JsonNode first = await PostAsync(host, null, "hello");
            string conversation = first["conversation"]!["id"]!.GetValue<string>();
            string r1 = first["id"]!.GetValue<string>();

            Task<int> sweep = store.ArmNextSweep();
            clock.Advance(TimeSpan.FromDays(31));
            Assert.Equal(1, await sweep.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.Null(await store.FindContinuationAsync(r1, Ct));

            // D4: a conversation id is never itself a response_continuation row, so it survives the sweep.
            using HttpResponseMessage response = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "again" }""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private static async Task<JsonNode> PostAsync(ResponsesHost host, string? previous, string input)
        {
            string json = previous is null
                ? $$"""{ "stream": false, "input": "{{input}}" }"""
                : $$"""{ "stream": false, "previous_response_id": "{{previous}}", "input": "{{input}}" }""";
            using HttpResponseMessage response = await host.PostAsync(json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await ResponsesHost.ReadJsonAsync(response);
        }

        /// <summary>Hands the host a store the test already holds, as the document's <c>test</c> kind.</summary>
        private sealed class TestConversationStoreAdapter(IConversationStore store) : IConversationStoreAdapter
        {
            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(store);
            }
        }

        /// <summary>
        /// An in-memory store, on the clock a test also drives, that lets the test await the hosted
        /// <c>ConversationSweeper</c>'s own next sweep pass rather than calling <c>SweepAsync</c> itself.
        /// </summary>
        private sealed class SweepSignalingConversationStore(TimeProvider clock)
            : DelegatingConversationStore(new InMemoryConversationStore(clock)), IConversationStoreAdapter
        {
            private TaskCompletionSource<int> _swept = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IConversationStore>(this);
            }

            /// <summary>Arms a fresh waiter, so a sweep already in flight cannot be mistaken for the next one.</summary>
            public Task<int> ArmNextSweep()
            {
                TaskCompletionSource<int> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _swept = waiter;
                return waiter.Task;
            }

            public override async ValueTask<int> SweepAsync(
                TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
            {
                int result = await base.SweepAsync(retention, batchSize, cancellationToken).ConfigureAwait(false);
                _ = _swept.TrySetResult(result);
                return result;
            }
        }

        /// <summary>
        /// Answers "reply to turn N". On the turns named, the first model call adds todo "item-N" and the
        /// call after the tool result answers text.
        /// </summary>
        private sealed class ScriptedClient(int[] todoOnTurns) : IChatClient
        {
            private readonly HashSet<int> _todoOnTurns = [.. todoOnTurns];

            public List<List<ChatMessage>> Requests { get; } = [];

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                List<ChatMessage> transcript = [.. messages];
                lock (Requests)
                {
                    Requests.Add(transcript);
                }

                await Task.Yield();
                ChatMessage lastUser = transcript.Last(m => m.Role == ChatRole.User && m.Text.StartsWith('t'));
                int turn = int.Parse(
                    lastUser.Text[1..lastUser.Text.IndexOf(' ', StringComparison.Ordinal)],
                    CultureInfo.InvariantCulture);
                bool answeringTool = transcript[^1].Role == ChatRole.Tool;
                string id = Guid.NewGuid().ToString("N");

                AIFunction? add = options?.Tools?.OfType<AIFunction>()
                    .FirstOrDefault(t => t.Name.Contains("add", StringComparison.OrdinalIgnoreCase));

                if (!answeringTool && _todoOnTurns.Contains(turn) && add is not null)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "call_" + id,
                            add.Name,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["todos"] = new List<object>
                                {
                                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = $"item-{turn}" },
                                },
                            })])
                    { ResponseId = id, MessageId = id };
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, $"reply to turn {turn}")
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
        }
    }
}

using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A store that answers a turn's append later than <see cref="TurnFailureReasons.CompletionTimeout"/>. The
    /// turn publishes only what the store's answer says, however late it comes.
    /// </summary>
    public sealed class ResponsesSlowStoreTests
    {
        private const string Yaml =
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

        private static readonly TimeSpan PastTheBound = TurnFailureReasons.CompletionTimeout + TimeSpan.FromSeconds(1);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 60_000)]
        public async Task ARefusalThatComesAfterTheBound_FilesNothing_AndPublishesNoCommit()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            HoldingStore store = new(holdAppendOfTurn: 1, holdAppendNumber: 2);
            RendezvousChatClient model = new(parties: 2);

            // One host shares one live session, so the two racing sessions need two hosts over the one store.
            await using ResponsesHost host = await StartAsync(model, store, time);
            await using ResponsesHost other = await StartAsync(model, store, time);
            string conversation = await OpenAsync(host);

            Task<HttpResponseMessage> first = host.PostAsync(Race(conversation, "race A", "user-a"));
            Task<HttpResponseMessage> second = other.PostAsync(Race(conversation, "race B", "user-b"));
            await store.Held.WaitAsync(Ct);
            await PassTheBoundAsync(time);
            store.Release();
            using HttpResponseMessage a = await first;
            using HttpResponseMessage b = await second;
            List<JsonObject> eventsA = Parse(await ResponsesHost.ReadEventsAsync(a));
            List<JsonObject> eventsB = Parse(await ResponsesHost.ReadEventsAsync(b));

            List<JsonObject> won = Assert.Single([eventsA, eventsB], events => Type(events[^1]) == "response.completed");
            List<JsonObject> lost = Assert.Single([eventsA, eventsB], events => Type(events[^1]) == "error");
            Assert.Equal(ResponsesTurnConflict.Code, lost[^1]["code"]!.GetValue<string>());
            Assert.DoesNotContain(lost, frame => frame.ContainsKey(TurnStreamPart.MessageCommitted));
            Assert.Contains(won, frame => frame.ContainsKey(TurnStreamPart.MessageCommitted));

            IConversationStore stored = host.Services.GetRequiredService<IConversationStore>();
            Assert.Null(await stored.FindContinuationAsync(ResponseId(lost), Ct));
            Assert.Equal(conversation, await stored.FindContinuationAsync(ResponseId(won), Ct));

            Assert.Equal([0, 1], [.. (await TurnCompletedIndexesAsync(host, conversation)).Concat(await TurnCompletedIndexesAsync(other, conversation)).Order()]);
        }

        // The other half of the same rule: a slow store that keeps the turn still ends it as saved.
        [Fact(Timeout = 60_000)]
        public async Task ASaveThatLandsAfterTheBound_EndsTheTurnAsSaved_AndFilesIt()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            HoldingStore store = new(holdAppendOfTurn: 1, holdAppendNumber: 1);
            RendezvousChatClient model = new(parties: 1);
            await using ResponsesHost host = await StartAsync(model, store, time);
            string conversation = await OpenAsync(host);

            Task<HttpResponseMessage> slow = host.PostAsync(Race(conversation, "race A", "user-a"));
            await store.Held.WaitAsync(Ct);
            await PassTheBoundAsync(time);
            store.Release();
            using HttpResponseMessage response = await slow;
            List<JsonObject> events = Parse(await ResponsesHost.ReadEventsAsync(response));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("response.completed", Type(events[^1]));
            Assert.Contains(events, frame => frame.ContainsKey(TurnStreamPart.MessageCommitted));
            IConversationStore stored = host.Services.GetRequiredService<IConversationStore>();
            Assert.Equal(conversation, await stored.FindContinuationAsync(ResponseId(events), Ct));
            Assert.Equal([0, 1], await TurnCompletedIndexesAsync(host, conversation));
        }

        private static async Task<ResponsesHost> StartAsync(RendezvousChatClient model, HoldingStore store, FakeTimeProvider time)
        {
            return await ResponsesHost.StartAsync(Yaml, model, options =>
            {
                options.TimeProvider = time;
                _ = options.UseConversationStores(new HoldingStoreAdapter(store));
            });
        }

        private static async Task<string> OpenAsync(ResponsesHost host)
        {
            string conversation = "conv_" + Guid.NewGuid().ToString("N");
            using HttpResponseMessage open = await host.PostAsync(
                /*lang=json,strict*/ $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "open" }""");
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
            return conversation;
        }

        private static string Race(string conversation, string input, string messageId)
        {
            return /*lang=json,strict*/ $$"""{ "stream": true, "conversation": "{{conversation}}", "input": "{{input}}", "agentcore": { "message_id": "{{messageId}}" } }""";
        }

        /// <summary>Moves the clock past the bound, once a bound the turn armed on it, if any, is armed.</summary>
        private static async Task PassTheBoundAsync(FakeTimeProvider time)
        {
            Task armed = time.WaitForTimersAsync(time.GetUtcNow() + TurnFailureReasons.CompletionTimeout, 1);
            _ = await Task.WhenAny(armed, Task.Delay(TimeSpan.FromSeconds(1), Ct));
            time.Advance(PastTheBound);
        }

        private static async Task<List<int?>> TurnCompletedIndexesAsync(ResponsesHost host, string conversation)
        {
            QueuedAuditSink queue = host.Services.GetRequiredService<QueuedAuditSink>();
            await queue.FlushAsync(Ct);
            InMemoryAuditSink audit = Assert.IsType<InMemoryAuditSink>(queue.Store);
            return [.. audit.EventsOf(conversation).Where(item => item.Kind == AuditEventKind.TurnCompleted).Select(item => item.TurnIndex)];
        }

        private static string ResponseId(List<JsonObject> events)
        {
            return events[0]["response"]!["id"]!.GetValue<string>();
        }

        private static List<JsonObject> Parse(List<string> events)
        {
            return [.. events.Select(raw => JsonNode.Parse(raw)!.AsObject())];
        }

        private static string? Type(JsonObject frame)
        {
            return frame["type"]?.GetValue<string>();
        }

        /// <summary>Hands the host the store the test holds.</summary>
        private sealed class HoldingStoreAdapter(HoldingStore store) : IConversationStoreAdapter
        {
            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IConversationStore>(store);
            }
        }

        /// <summary>Holds one append naming a given turn, by its order among that turn's appends, until released.</summary>
        private sealed class HoldingStore(int holdAppendOfTurn, int holdAppendNumber) : DelegatingConversationStore(new InMemoryConversationStore())
        {
            private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _appends;

            public Task Held => _held.Task;

            public void Release()
            {
                _ = _release.TrySetResult();
            }

            // Grants every mark, as a lapsed lease would, so two racing turns both reach the store's turn check.
            public override ValueTask<bool> TryMarkBusyAsync(
                string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(true);
            }

            public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                if (messages.Any(message => message.TurnIndex == holdAppendOfTurn)
                    && Interlocked.Increment(ref _appends) == holdAppendNumber)
                {
                    _ = _held.TrySetResult();
                    await _release.Task.WaitAsync(cancellationToken);
                }

                return await base.AppendAsync(conversationId, messages, state, cancellationToken);
            }
        }
    }
}

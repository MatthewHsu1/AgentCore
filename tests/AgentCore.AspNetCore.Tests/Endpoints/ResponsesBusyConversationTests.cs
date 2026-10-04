using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A request on a conversation another request still holds waits for the busy mark, then reads the conversation
    /// afresh. A client that sends one message at a time is never refused; a wait past the
    /// limit is.
    /// </summary>
    public sealed class ResponsesBusyConversationTests
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

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A stopped turn files its words after the abort. A next message that read the conversation before
        // they landed would take the same turn index, and the store would keep only one of the two.
        [Fact(Timeout = 60_000)]
        public async Task AStreamTheClientStopped_ThenTheNextMessageAtOnce_KeepsBothTurnsInOrder()
        {
            TurnHoldingStore store = new(heldTurn: 1);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml, new StallOnCueChatClient(), options => options.UseConversationStores(store));
            string conversation = await OpenAsync(host);

            // The client reads the first piece of a streamed reply and drops the connection, as a browser does.
            using (HttpClient browser = new(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = host.Client.BaseAddress })
            {
                using HttpResponseMessage stopped = await PostAsync(browser, conversation, "stall now", stream: true);
                await ReadUntilAsync(stopped, StallOnCueChatClient.Piece);
            }

            await store.Held.WaitAsync(Ct);
            Task<HttpResponseMessage> sending = PostAsync(host.Client, conversation, "next", stream: false);

            // One host shares one live session, so the wait is in process and the store never refuses a mark.
            _ = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(1), Ct));
            store.Release();
            using HttpResponseMessage next = await sending;

            await AssertBothTurnsKeptInOrderAsync(host, conversation, next);
        }

        // The same stopped stream, with the next message sent to another host over the same store: there the busy
        // mark in the store is what makes it wait.
        [Fact(Timeout = 60_000)]
        public async Task AStreamTheClientStopped_ThenTheNextMessageAtOnceOnAnotherHost_KeepsBothTurnsInOrder()
        {
            TurnHoldingStore store = new(heldTurn: 1);
            StallOnCueChatClient model = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(store));
            await using ResponsesHost other = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(store));
            string conversation = await OpenAsync(host);

            using (HttpClient browser = new(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = host.Client.BaseAddress })
            {
                using HttpResponseMessage stopped = await PostAsync(browser, conversation, "stall now", stream: true);
                await ReadUntilAsync(stopped, StallOnCueChatClient.Piece);
            }

            await store.Held.WaitAsync(Ct);
            Task<HttpResponseMessage> sending = PostAsync(other.Client, conversation, "next", stream: false);
            _ = await Task.WhenAny(store.MarkRefused, sending);
            store.Release();
            using HttpResponseMessage next = await sending;

            await AssertBothTurnsKeptInOrderAsync(other, conversation, next);
        }

        [Fact(Timeout = 60_000)]
        public async Task ATurnStillRunningOnThisHostPastTheWaitLimit_Answers409_AndLeavesATurnRefused()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml,
                new StallOnCueChatClient(),
                options =>
                {
                    options.TimeProvider = time;
                    _ = options.UseConversationStores(new TurnHoldingStore(heldTurn: -1));
                });
            string conversation = await OpenAsync(host);
            using HttpResponseMessage running = await PostAsync(host.Client, conversation, "stall now", stream: true);
            await ReadUntilAsync(running, StallOnCueChatClient.Piece);

            Task<HttpResponseMessage> sending = PostAsync(host.Client, conversation, "next", stream: false);
            _ = await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.WaitLimit, 1), sending);
            time.Advance(ConversationBusyMark.WaitLimit);
            using HttpResponseMessage refused = await sending;

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            JsonNode error = (await ResponsesHost.ReadJsonAsync(refused))["error"]!;
            Assert.Equal(ResponsesTurnConflict.Code, error["code"]!.GetValue<string>());
            Assert.Equal(ResponsesTurnConflict.BusyMessage, error["message"]!.GetValue<string>());

            QueuedAuditSink queue = host.Services.GetRequiredService<QueuedAuditSink>();
            await queue.FlushAsync(Ct);
            AuditEvent refusal = Assert.Single(
                Assert.IsType<InMemoryAuditSink>(queue.Store).EventsOf(conversation), item => item.Kind == AuditEventKind.TurnRefused);
            Assert.Equal(("busy", null), (refusal.Payload[AuditPayloadKeys.RefusedReason], refusal.TurnIndex));
        }

        [Fact(Timeout = 60_000)]
        public async Task AConversationHeldPastTheWaitLimit_Answers409_AndLeavesAWarningAndATurnRefused()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            using RecordingLoggerFactory logs = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml,
                new StallOnCueChatClient(),
                options =>
                {
                    options.TimeProvider = time;
                    options.LoggerFactory = logs;
                    _ = options.UseConversationStores(new TurnHoldingStore(heldTurn: -1));
                });
            string conversation = await OpenAsync(host);
            IConversationStore store = host.Services.GetRequiredService<IConversationStore>();
            _ = await store.TryMarkBusyAsync(conversation, "other-host", TimeSpan.FromHours(1), Ct);

            Task<HttpResponseMessage> sending = PostAsync(host.Client, conversation, "next", stream: false);
            _ = await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.FirstPoll, 1), sending);
            time.Advance(ConversationBusyMark.WaitLimit);
            using HttpResponseMessage refused = await sending;

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            JsonNode error = (await ResponsesHost.ReadJsonAsync(refused))["error"]!;
            Assert.Equal(ResponsesTurnConflict.Code, error["code"]!.GetValue<string>());
            Assert.Equal(ResponsesTurnConflict.BusyMessage, error["message"]!.GetValue<string>());

            QueuedAuditSink queue = host.Services.GetRequiredService<QueuedAuditSink>();
            await queue.FlushAsync(Ct);
            AuditEvent refusal = Assert.Single(
                Assert.IsType<InMemoryAuditSink>(queue.Store).EventsOf(conversation), item => item.Kind == AuditEventKind.TurnRefused);
            Assert.Equal(("busy", null), (refusal.Payload[AuditPayloadKeys.RefusedReason], refusal.TurnIndex));
            CapturedLine line = Assert.Single(logs.Of(40));
            Assert.Equal((LogLevel.Warning, "busy"), (line.Level, line.Field<string>("Reason")));
        }

        private static async Task AssertBothTurnsKeptInOrderAsync(ResponsesHost host, string conversation, HttpResponseMessage next)
        {
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            JsonNode answer = await ResponsesHost.ReadJsonAsync(next);
            Assert.Equal("2", answer["metadata"]!["turn_index"]!.GetValue<string>());
            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, conversation, 6);
            Assert.Equal(
                [(0, "open"), (1, "stall now"), (2, "next")],
                rows.Where(row => row.Content.Role.Value == "user").Select(row => (row.TurnIndex, row.Content.Text)));
        }

        private static async Task<string> OpenAsync(ResponsesHost host)
        {
            string conversation = "conv_" + Guid.NewGuid().ToString("N");
            using HttpResponseMessage open = await PostAsync(host.Client, conversation, "open", stream: false);
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
            return conversation;
        }

        private static Task<HttpResponseMessage> PostAsync(HttpClient client, string conversation, string input, bool stream)
        {
            string flag = stream ? "true" : "false";
            HttpRequestMessage request = new(HttpMethod.Post, "/v1/main/responses")
            {
                Content = new StringContent(
                    /*lang=json,strict*/ $$"""{ "stream": {{flag}}, "conversation": "{{conversation}}", "input": "{{input}}" }""",
                    Encoding.UTF8,
                    "application/json"),
            };

            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }

        private static async Task ReadUntilAsync(HttpResponseMessage response, string text)
        {
            Stream body = await response.Content.ReadAsStreamAsync(Ct);
            using StreamReader reader = new(body, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(Ct) is { } line && !line.Contains(text, StringComparison.Ordinal))
            {
            }
        }
    }
}

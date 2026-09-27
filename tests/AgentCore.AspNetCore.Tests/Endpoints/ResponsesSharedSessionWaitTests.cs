using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A Responses request runs on the one live session its conversation has on this host. It waits for a turn that
    /// another door started on that session, and it follows the conversation to a new session when the one it waited
    /// on was closed meanwhile.
    /// </summary>
    public sealed class ResponsesSharedSessionWaitTests
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

        // A voice turn, or AgentCoreAgent.RunAsync on another AgentSession of the id, runs on the same live session.
        [Fact(Timeout = 60_000)]
        public async Task AMessageSentWhileATurnStartedAnotherWayRuns_WaitsForIt_ThenRunsAsTheNextTurn()
        {
            // Arrange
            await using ResponsesHost host = await StartAsync();
            string conversation = await OpenAsync(host);
            IConversationSessions sessions = host.Services.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession live = await sessions.GetOrOpenAsync("main", conversation, null, Ct);

            using CancellationTokenSource stop = new();
            IAsyncEnumerator<ChatResponseUpdate> other = live
                .RunTurnMessageStreamingAtOriginAsync(new ChatMessage(ChatRole.User, "stall now"), null, stop.Token)
                .GetAsyncEnumerator(stop.Token);
            Assert.True(await other.MoveNextAsync());

            // Act
            Task<HttpResponseMessage> sending = PostAsync(host.Client, conversation, "next", stream: false);
            _ = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(1), Ct));
            await stop.CancelAsync();
            await DrainAsync(other);
            using HttpResponseMessage next = await sending;

            // Assert
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            JsonNode answer = await ResponsesHost.ReadJsonAsync(next);
            Assert.Equal("2", answer["metadata"]!["turn_index"]!.GetValue<string>());
            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, conversation, 6);
            Assert.Equal(
                [(0, "open"), (1, "stall now"), (2, "next")],
                rows.Where(row => row.Content.Role.Value == "user").Select(row => (row.TurnIndex, row.Content.Text)));
        }

        [Fact(Timeout = 60_000)]
        public async Task AMessageWaitingOnARequest_WhenTheSessionIsClosedMeanwhile_RunsOnTheReopenedConversation()
        {
            // Arrange
            await using ResponsesHost host = await StartAsync();
            string conversation = await OpenAsync(host);
            IConversationSessions sessions = host.Services.GetRequiredService<EntryRegistry>().Sessions;
            using HttpClient browser = new(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = host.Client.BaseAddress };
            HttpResponseMessage stalled = await PostAsync(browser, conversation, "stall now", stream: true);
            await ReadUntilAsync(stalled, StallOnCueChatClient.Piece);

            // Act
            Task<HttpResponseMessage> sending = PostAsync(host.Client, conversation, "next", stream: false);
            _ = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromMilliseconds(300), Ct));
            Task closing = sessions.CloseAsync("main", conversation, Ct).AsTask();
            _ = await Task.WhenAny(closing, Task.Delay(TimeSpan.FromMilliseconds(300), Ct));
            stalled.Dispose();
            await closing;
            using HttpResponseMessage next = await sending;

            // Assert
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            JsonNode answer = await ResponsesHost.ReadJsonAsync(next);
            Assert.Equal("2", answer["metadata"]!["turn_index"]!.GetValue<string>());
            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, conversation, 6);
            Assert.Equal(
                [(0, "open"), (1, "stall now"), (2, "next")],
                rows.Where(row => row.Content.Role.Value == "user").Select(row => (row.TurnIndex, row.Content.Text)));
        }

        private static Task<ResponsesHost> StartAsync()
        {
            return ResponsesHost.StartAsync(
                Yaml, new StallOnCueChatClient(), options => options.UseConversationStores(new TurnHoldingStore(heldTurn: -1)));
        }

        private static async Task DrainAsync(IAsyncEnumerator<ChatResponseUpdate> updates)
        {
            try
            {
                while (await updates.MoveNextAsync())
                {
                }
            }
            catch (OperationCanceledException)
            {
            }

            await updates.DisposeAsync();
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

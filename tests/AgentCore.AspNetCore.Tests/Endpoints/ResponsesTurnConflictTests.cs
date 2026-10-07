using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// Two requests on two hosts over one store resume the same saved conversation at once, and the busy mark holds
    /// neither back (its lease lapsed). Each host builds its own session from the stored envelope, so both run turn 1;
    /// the store keeps the first commit and refuses the second (owner ruling 2026-09-24, "refuse at save"). The refused
    /// caller is told, and nothing of its turn is kept. One host shares one live session, so the race needs two.
    /// </summary>
    public sealed class ResponsesTurnConflictTests
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

        [Fact]
        public async Task TwoRequestsOnOneTurn_NotStreamed_OneIsConflict_AndOnlyTheOtherIsKept()
        {
            // Arrange
            RendezvousChatClient model = new(parties: 2);
            LapsedMarkStore store = new();
            await using ResponsesHost hostA = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(store));
            await using ResponsesHost hostB = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(store));
            string conversation = await OpenAsync(hostA);

            // Act
            (HttpResponseMessage first, HttpResponseMessage second) = await RaceAsync(hostA, hostB, conversation, stream: false);
            using HttpResponseMessage a = first;
            using HttpResponseMessage b = second;

            // Assert
            HttpResponseMessage won = Assert.Single([a, b], response => response.StatusCode == HttpStatusCode.OK);
            HttpResponseMessage lost = Assert.Single([a, b], response => response.StatusCode == HttpStatusCode.Conflict);
            JsonNode error = (await ResponsesHost.ReadJsonAsync(lost))["error"]!;
            Assert.Equal(ResponsesTurnConflict.Code, error["code"]!.GetValue<string>());
            Assert.Equal(ResponsesTurnConflict.ConflictMessage, error["message"]!.GetValue<string>());

            JsonNode answer = await ResponsesHost.ReadJsonAsync(won);
            string winner = answer.OutputText()["reply to ".Length..];
            ResponsesHost loserHost = ReferenceEquals(lost, a) ? hostA : hostB;
            await AssertOnlyTheWinnerIsKeptAsync([hostA, hostB], loserHost, model, conversation, winner, answer["id"]!.GetValue<string>());
        }

        [Fact]
        public async Task TwoRequestsOnOneTurn_Streamed_OneEndsWithTheErrorEvent_AndOnlyTheOtherIsKept()
        {
            // Arrange
            RendezvousChatClient model = new(parties: 2);
            LapsedMarkStore store = new();
            await using ResponsesHost hostA = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(store));
            await using ResponsesHost hostB = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(store));
            string conversation = await OpenAsync(hostA);

            // Act
            (HttpResponseMessage first, HttpResponseMessage second) = await RaceAsync(hostA, hostB, conversation, stream: true);
            using HttpResponseMessage a = first;
            using HttpResponseMessage b = second;
            List<JsonObject> eventsA = Parse(await ResponsesHost.ReadEventsAsync(a));
            List<JsonObject> eventsB = Parse(await ResponsesHost.ReadEventsAsync(b));

            // Assert
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            Assert.Equal(HttpStatusCode.OK, b.StatusCode);
            List<JsonObject> won = Assert.Single([eventsA, eventsB], events => Type(events[^1]) == "response.completed");
            List<JsonObject> lost = Assert.Single([eventsA, eventsB], events => Type(events[^1]) == "error");
            ResponsesHost loserHost = ReferenceEquals(lost, eventsA) ? hostA : hostB;

            Assert.Equal(ResponsesTurnConflict.Code, lost[^1]["code"]!.GetValue<string>());
            Assert.Equal(ResponsesTurnConflict.ConflictMessage, lost[^1]["message"]!.GetValue<string>());
            Assert.DoesNotContain(lost, frame => Type(frame) == "response.completed");
            Assert.DoesNotContain(lost, frame => frame.ContainsKey(TurnStreamPart.MessageCommitted));
            Assert.Contains(won, frame => frame.ContainsKey(TurnStreamPart.MessageCommitted));

            // The refused session is never filed, not even under the response id only its own stream named.
            string lostResponseId = lost[0]["response"]!["id"]!.GetValue<string>();
            Assert.Null(await store.FindContinuationAsync(lostResponseId, TestContext.Current.CancellationToken));

            string winner = string.Concat(ResponsesHost.TextDeltas(won.Select(frame => frame.ToJsonString())))["reply to ".Length..];
            string responseId = won[0]["response"]!["id"]!.GetValue<string>();
            await AssertOnlyTheWinnerIsKeptAsync([hostA, hostB], loserHost, model, conversation, winner, responseId);
        }

        // One host holds one live session per conversation, so two requests sent to it at once take turns.
        [Fact]
        public async Task TwoRequestsAtOnceOnOneHost_BothRun_OneAfterTheOther()
        {
            // Arrange
            RendezvousChatClient model = new(parties: 1);
            await using ResponsesHost host = await ResponsesHost.StartAsync(Yaml, model, options => options.UseConversationStores(new LapsedMarkStore()));
            string conversation = await OpenAsync(host);

            // Act
            (HttpResponseMessage first, HttpResponseMessage second) = await RaceAsync(host, host, conversation, stream: false);
            using HttpResponseMessage a = first;
            using HttpResponseMessage b = second;

            // Assert
            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (a.StatusCode, b.StatusCode));
            string[] turns =
            [
                (await ResponsesHost.ReadJsonAsync(a))["metadata"]!["turn_index"]!.GetValue<string>(),
                (await ResponsesHost.ReadJsonAsync(b))["metadata"]!["turn_index"]!.GetValue<string>(),
            ];
            Assert.Equal(["1", "2"], turns.Order());

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, conversation, 6);
            List<string?> spoken = [.. rows.Where(row => row.Content.Role.Value == "user").Select(row => row.Content.Text)];
            Assert.Equal("open", spoken[0]);
            Assert.Equal(["race A", "race B"], spoken.Skip(1).Order());
            Assert.Equal([0, 1, 2], rows.Where(row => row.Content.Role.Value == "user").Select(row => row.TurnIndex));
        }

        private static async Task<string> OpenAsync(ResponsesHost host)
        {
            string conversation = "conv_" + Guid.NewGuid().ToString("N");
            using HttpResponseMessage open = await host.PostAsync(
                /*lang=json,strict*/ $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "open" }""");
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
            return conversation;
        }

        private static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceAsync(
            ResponsesHost hostA, ResponsesHost hostB, string conversation, bool stream)
        {
            string flag = stream ? "true" : "false";
            Task<HttpResponseMessage> first = hostA.PostAsync(
                /*lang=json,strict*/ $$"""{ "stream": {{flag}}, "conversation": "{{conversation}}", "input": "race A", "agentcore": { "message_id": "user-a" } }""");
            Task<HttpResponseMessage> second = hostB.PostAsync(
                /*lang=json,strict*/ $$"""{ "stream": {{flag}}, "conversation": "{{conversation}}", "input": "race B", "agentcore": { "message_id": "user-b" } }""");

            return (await first, await second);
        }

        /// <summary>
        /// Checks the store, the continuation and the audit chain hold the winning turn and nothing of the refused
        /// one: its rows, its envelope and its <c>turn.completed</c> are all absent, and a <c>turn.refused</c> names it.
        /// The next message goes to the host that lost, whose live session must catch up on the winner's words.
        /// </summary>
        private static async Task AssertOnlyTheWinnerIsKeptAsync(
            ResponsesHost[] hosts, ResponsesHost loserHost, RendezvousChatClient model, string conversation, string winner, string winnerResponseId)
        {
            string loser = winner == "race A" ? "race B" : "race A";

            string? owner = await loserHost.Services.GetRequiredService<IConversationStore>()
                .FindContinuationAsync(winnerResponseId, TestContext.Current.CancellationToken);
            Assert.Equal(conversation, owner);

            List<ConversationMessage> rows = await StoredRows.SettleAsync(loserHost.Services, conversation, 4);
            Assert.Equal(
                [(0, "open"), (0, "reply to open"), (1, winner), (1, "reply to " + winner)],
                rows.Select(row => (row.TurnIndex, row.Content.Text)));

            using HttpResponseMessage next = await loserHost.PostAsync(
                /*lang=json,strict*/ $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "next" }""");
            JsonNode answer = await ResponsesHost.ReadJsonAsync(next);
            Assert.Equal("2", answer["metadata"]!["turn_index"]!.GetValue<string>());
            IReadOnlyList<string> heard = model.Requests.Last();
            Assert.Contains(winner, heard);
            Assert.DoesNotContain(loser, heard);

            List<AuditEvent> events = [];
            foreach (ResponsesHost host in hosts)
            {
                QueuedAuditSink queue = host.Services.GetRequiredService<QueuedAuditSink>();
                await queue.FlushAsync(TestContext.Current.CancellationToken);
                events.AddRange(Assert.IsType<InMemoryAuditSink>(queue.Store).EventsOf(conversation));
            }

            Assert.Equal(
                [0, 1, 2],
                events.Where(item => item.Kind == AuditEventKind.TurnCompleted).Select(item => item.TurnIndex).Order());

            // The refusal leaves its own row, which is all that is left of it when its client has gone.
            AuditEvent refusal = Assert.Single(events, item => item.Kind == AuditEventKind.TurnRefused);
            Assert.Equal((1, "conflict"), (refusal.TurnIndex, refusal.Payload[AuditPayloadKeys.RefusedReason]));
        }

        private static List<JsonObject> Parse(List<string> events)
        {
            return [.. events.Select(raw => JsonNode.Parse(raw)!.AsObject())];
        }

        private static string? Type(JsonObject frame)
        {
            return frame["type"]?.GetValue<string>();
        }
    }
}

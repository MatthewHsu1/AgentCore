using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The Responses path: one request runs one turn, and the continuation carries the conversation.
    /// </summary>
    /// <remarks>
    /// Every test here runs offline against a fake model. The two-stage document names the
    /// stage in each reply, so the text proves which stage spoke: there is no subtler
    /// assertion about continuity than the second turn answering with the second agent.
    /// </remarks>
    public sealed class ResponsesTests
    {
        private const string TwoStagesYaml =
            """
          apiVersion: agentcore/v1
          guards:
            second: { ">=": [ { var: turnIndex }, 1 ] }
            third: { ">=": [ { var: turnIndex }, 2 ] }
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: solo, instructions: "answer one" }
              - { id: duo, instructions: "answer two" }
          entries:
            main:
              policy:
                initial: talking
                stages:
                  - { id: talking, agent: solo, to: [ { stage: followup, when: second } ] }
                  - { id: followup, agent: duo, to: [ { stage: talking, when: third } ] }
          providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
          """;

        [Fact]
        public async Task FirstTurn_MintsConversationAndAnswersStageOne()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonNode body = await ResponsesHost.ReadJsonAsync(response);
            Assert.Contains("answer one", body.OutputText(), StringComparison.Ordinal);
            Assert.StartsWith("conv_", body.ContinuationId(), StringComparison.Ordinal);
            Assert.StartsWith("resp_", body["id"]?.GetValue<string>(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task SecondTurnOnConversation_ContinuesStageAndTranscript()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            string convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();
            IReadOnlyList<ChatMessage>? firstRequest = reply.LastRequest;

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            JsonNode body = await ResponsesHost.ReadJsonAsync(second);
            Assert.Contains("answer two", body.OutputText(), StringComparison.Ordinal);

            IReadOnlyList<ChatMessage>? secondRequest = reply.LastRequest;
            Assert.NotNull(secondRequest);
            Assert.Contains(secondRequest, m => m.Text.Contains("first question", StringComparison.Ordinal));
            Assert.Contains(secondRequest, m => m.Text.Contains("answer one", StringComparison.Ordinal));
            Assert.NotSame(firstRequest, secondRequest);
        }

        [Fact]
        public async Task SecondTurnOnResponseChain_ContinuesStage()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            string? respId = (await ResponsesHost.ReadJsonAsync(first))["id"]?.GetValue<string>();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "previous_response_id": "{{respId}}", "input": "chained question" }""");

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            JsonNode body = await ResponsesHost.ReadJsonAsync(second);
            Assert.Contains("answer two", body.OutputText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task SecondTurnStreams_StillContinuesStage()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            string convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": true, "conversation": "{{convId}}", "input": "second question" }""");

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal("text/event-stream", second.Content.Headers.ContentType?.MediaType);
            string text = await ResponsesHost.ReadTextAsync(second);
            Assert.Contains("answer two", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SecondTurnStreams_NamesTheStageItSpeaksInOnTheStageHeader()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            string convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": true, "conversation": "{{convId}}", "input": "second question" }""");

            Assert.Equal("followup", Assert.Single(second.Headers.GetValues("X-AgentCore-Stage")));
            Assert.Contains("answer two", await ResponsesHost.ReadTextAsync(second), StringComparison.Ordinal);
        }

        [Fact]
        public async Task StreamedReply_CarriesTheCommittedReplyId_EqualToTheStoredRow()
        {
            // Design section 6, step E5: TurnCommittedContent passes the seam filter, and the text
            // endpoint reads the reply id off it rather than off a read-after-stream convenience copy.
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync(
                /*lang=json,strict*/
                """{ "stream": true, "conversation": "conv-e5", "input": "first question", "agentcore": { "message_id": "u1" } }""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            JsonObject committed = events
                .Select(raw => JsonNode.Parse(raw)!.AsObject())
                .Single(frame => frame.ContainsKey("agentcore_message_committed"))["agentcore_message_committed"]!
                .AsObject();

            string replyId = committed["reply_message_id"]!.GetValue<string>();

            IConversationStore store = host.Services.GetRequiredService<IConversationStore>();
            IReadOnlyList<Application.Transcript.ConversationMessage> rows = await store
                .ReadForSessionAsync("conv-e5", TestContext.Current.CancellationToken);

            Assert.Equal(replyId, rows[^1].MessageId);
        }

        [Fact]
        public async Task StreamAborted_ByTheHost_StillFilesTheSession()
        {
            // Design section 6, step E5: the layer commits the words on a host cancel (E4). A resumed conversation
            // finds its continuation only if ResponsesTurn.FileAsync runs too, so filing runs from a finally, on a
            // token that survives the abort.
            StallingChatClient reply = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync(
                /*lang=json,strict*/
                """{ "stream": true, "conversation": "conv-e5-abort", "input": "first question" }""");
            await reply.WaitUntilStreamingAsync();

            // Tears down the connection mid-turn: the model is still stalled, so this is the host
            // cancelling the request, not the reply finishing.
            response.Dispose();

            AgentCoreAgentSessionStore sessions = host.Services.GetRequiredService<AgentCoreAgentSessionStore>();
            await Poll.UntilAsync(() => sessions
                .ContainsAsync("conv-e5-abort", TestContext.Current.CancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult());

            IConversationStore store = host.Services.GetRequiredService<IConversationStore>();
            IReadOnlyList<Application.Transcript.ConversationMessage> rows = await store
                .ReadForSessionAsync("conv-e5-abort", TestContext.Current.CancellationToken);

            Assert.Contains(rows, row => row.Content.Role == ChatRole.User && row.Content.Text == "first question");
        }

        [Fact]
        public async Task UnknownConversation_StartsTheConversationUnderThatId()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(
                                     /*lang=json,strict*/
                                     """{ "stream": false, "conversation": "thread-7", "input": "first question" }""");

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            JsonNode firstBody = await ResponsesHost.ReadJsonAsync(first);
            Assert.Equal("thread-7", firstBody.ContinuationId());
            Assert.Equal("thread-7", firstBody["metadata"]!["call_id"]!.GetValue<string>());

            using HttpResponseMessage second = await host.PostAsync(
                                     /*lang=json,strict*/
                                     """{ "stream": false, "conversation": "thread-7", "input": "second question" }""");

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("answer two", (await ResponsesHost.ReadJsonAsync(second)).OutputText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task UnknownResponseId_ReturnsNotFound()
        {
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync(
                                     /*lang=json,strict*/
                                     """{ "stream": false, "previous_response_id": "resp_doesnotexist", "input": "hello" }""");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task MalformedBody_ReturnsBadRequest()
        {
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync("""{ "stream": false, "input": """);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task MissingInput_ReturnsBadRequest()
        {
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false }""");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        [Fact]
        public async Task FirstTurn_MetadataCarriesTurnFacts()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage response = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonNode body = await ResponsesHost.ReadJsonAsync(response);
            JsonObject metadata = body["metadata"]!.AsObject();
            Assert.Equal("0", metadata["turn_index"]!.GetValue<string>());
            // The commit advances on the post-increment index, so the first turn already leaves talking.
            Assert.Equal("talking", metadata["stage_before"]!.GetValue<string>());
            Assert.Equal("followup", metadata["stage_after"]!.GetValue<string>());
            Assert.Equal("false", metadata["is_terminal"]!.GetValue<string>());
            Assert.False(string.IsNullOrEmpty(metadata["call_id"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(metadata["message_id"]!.GetValue<string>()));
            Assert.Null(body["metadata"]!["approvals"]);
            Assert.Equal("followup", Assert.Single(response.Headers.GetValues("X-AgentCore-Stage")));
        }

        [Fact]
        public async Task SecondTurn_MetadataAdvancesWithTheStage()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            string convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");

            JsonObject metadata = (await ResponsesHost.ReadJsonAsync(second))["metadata"]!.AsObject();
            Assert.Equal("1", metadata["turn_index"]!.GetValue<string>());
            // The second turn speaks in followup and its post-increment index trips the third guard back.
            Assert.Equal("followup", metadata["stage_before"]!.GetValue<string>());
            Assert.Equal("talking", metadata["stage_after"]!.GetValue<string>());
        }

        [Fact]
        public async Task TerminalTurn_SignalsTerminalAndRefusesTheNext()
        {
            using FragmentingChatClient reply = new("answer one");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TerminalYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            JsonNode body = await ResponsesHost.ReadJsonAsync(first);
            Assert.Equal("true", body["metadata"]!["is_terminal"]!.GetValue<string>());
            string convId = body.ContinuationId();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        [Fact]
        public async Task EditAtAnEarlierMessage_WithdrawsTheWordsAfterIt()
        {
            using FragmentingChatClient reply = new("answer one", "answer two");
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first question" }""");
            JsonNode firstBody = await ResponsesHost.ReadJsonAsync(first);
            string convId = firstBody.ContinuationId();
            string anchor = firstBody["metadata"]!["message_id"]!.GetValue<string>();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            // Hanging the third turn off the first reply takes back everything after it: the model
            // sees the first exchange and the corrected words, but never the withdrawn question.
            using HttpResponseMessage third = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": "corrected question", "agentcore": { "message_id": "q3", "parent_id": "{{anchor}}" } }""");
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
            IReadOnlyList<ChatMessage>? thirdRequest = reply.LastRequest;
            Assert.NotNull(thirdRequest);
            Assert.Contains(thirdRequest, m => m.Text.Contains("first question", StringComparison.Ordinal));
            Assert.DoesNotContain(thirdRequest, m => m.Text.Contains("second question", StringComparison.Ordinal));
            Assert.Contains(thirdRequest, m => m.Text.Contains("corrected question", StringComparison.Ordinal));
        }

        private const string TerminalYaml =
            """
          apiVersion: agentcore/v1
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: solo, instructions: "answer one" }
          entries:
            main:
              policy:
                initial: done
                stages:
                  - { id: done, agent: solo, terminal: true }
          providers:
            conversation:   { kind: telnyx-relay }
            speech:
              stt: { kind: telnyx-relay }
              tts: { kind: telnyx-relay }
            llm:
              - { kind: openai, model: gpt-4.1-mini, as: reply }
          """;
    }
}

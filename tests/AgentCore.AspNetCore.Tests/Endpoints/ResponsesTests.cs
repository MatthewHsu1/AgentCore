using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
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
        public async Task GatedToolCall_AnswersWholeWithTheRequestInMetadata()
        {
            int sent = 0;
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

            using HttpResponseMessage response = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "send it" }""");
            JsonNode body = await ResponsesHost.ReadJsonAsync(response);

            Assert.Equal(string.Empty, body.OutputText());
            string approvals = body["metadata"]!["approvals"]!.GetValue<string>();
            Assert.Contains("send_email", approvals, StringComparison.Ordinal);
            Assert.Equal(0, sent);
        }

        [Fact]
        public async Task ApprovalAnswer_RunsTheToolAndReplies()
        {
            int sent = 0;
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "send it" }""");
            JsonNode asked = await ResponsesHost.ReadJsonAsync(first);
            string convId = asked.ContinuationId();
            string? requestId = JsonDocument.Parse(
                asked["metadata"]!["approvals"]!.GetValue<string>())
                .RootElement[0].GetProperty("request_id").GetString();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": [], "agentcore": { "approval": { "request_id": "{{requestId}}", "approved": true } } }""");
            JsonNode replied = await ResponsesHost.ReadJsonAsync(second);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(1, sent);
            Assert.Contains("done.", replied.OutputText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task ApprovalAnswerForARequestNothingAsked_IsConflict()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "send it" }""");
            string convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": [], "agentcore": { "approval": { "request_id": "req-nothing-asked", "approved": true } } }""");

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        [Fact]
        public async Task ApprovalAnswerWithWords_IsBadRequest()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "send it" }""");
            string convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{convId}}", "input": "send it", "agentcore": { "approval": { "request_id": "req-1", "approved": true } } }""");

            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
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

        [Fact]
        public async Task DialectStream_WritesRendersAsTheirOwnEvents()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                RenderYaml,
                new RenderChatClient(),
                configure: options => options.Bind("DrawIt", (Application.Runtime.TurnInvocation? turn) =>
                {
                    turn?.Screen?.Publish("generative-ui", "chart-1", new { title = "Q3 revenue" });
                    return ValueTask.FromResult<object?>("drew a Card; buttons: none");
                }));

            using HttpResponseMessage response = await host.PostAsync(
                                     /*lang=json,strict*/
                                     """{ "stream": true, "input": "show me revenue", "agentcore": { "message_id": "m1" } }""");
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            List<JsonElement> chunks = [.. events.Select(text => JsonDocument.Parse(text).RootElement)];
            JsonElement drawing = Assert.Single(chunks, static chunk => chunk.TryGetProperty("agentcore_data", out JsonElement data)
                && data.ValueKind != JsonValueKind.Null);
            Assert.Equal("generative-ui", drawing.GetProperty("agentcore_data").GetProperty("name").GetString());
            Assert.Equal(
                "Q3 revenue",
                drawing.GetProperty("agentcore_data").GetProperty("data").GetProperty("title").GetString());
        }

        [Fact]
        public async Task PureStream_WritesNoDialectEvents()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                RenderYaml,
                new RenderChatClient(),
                configure: options => options.Bind("DrawIt", (Application.Runtime.TurnInvocation? turn) =>
                {
                    turn?.Screen?.Publish("generative-ui", "chart-1", new { title = "Q3 revenue" });
                    return ValueTask.FromResult<object?>("drew a Card; buttons: none");
                }));

            using HttpResponseMessage response = await host.PostAsync(/*lang=json,strict*/ """{ "stream": true, "input": "show me revenue" }""");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            Assert.DoesNotContain(events
                .Select(text => JsonDocument.Parse(text).RootElement),
                static chunk => chunk.TryGetProperty("agentcore_data", out JsonElement data)
                    && data.ValueKind != JsonValueKind.Null);
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

        private const string ApprovalYaml =
            """
        apiVersion: agentcore/v1
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "send the mail", tools: [ send_email ] }
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

        private const string RenderYaml =
            """
          apiVersion: agentcore/v1
          tools:
            - id: draw_it
              kind: binding
              binds: DrawIt
              description: Draw something for the caller.
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller", tools: [ draw_it ] }
              - { id: closer,  instructions: "close the conversation",   tools: [ draw_it ] }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close } ] }
                  - { id: close,    agent: closer,  to: [ { stage: greeting } ] }
          providers:
            conversation:   { kind: telnyx-relay }
            speech:
              stt: { kind: telnyx-relay }
              tts: { kind: telnyx-relay }
            llm:
              - { kind: openai, model: gpt-4.1-mini, as: reply }
          """;

        private static ApprovalRequiredAIFunction GatedSendEmail(Action onSend)
        {
            return new(AIFunctionFactory.Create(
                (string to) =>
                {
                    onSend();
                    return "sent";
                },
                "send_email",
                "Send an email."));
        }

        /// <summary>A tool source that serves one gated id, standing in for an approval-required host tool.</summary>
        private sealed class GatedSource(Func<AITool> build)
            : Application.Ports.IToolSource
        {
            public ValueTask<IReadOnlyList<Application.Tools.Registry.ToolRegistration>> ProvideAsync(
                Application.Tools.Registry.ToolSourceContext context, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<Application.Tools.Registry.ToolRegistration>>(
                                [new Application.Tools.Registry.ToolRegistration("send_email", "Send an email.", build)]);
            }
        }

        /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
        private sealed class GatedToolCallingChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                bool alreadyCalled = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

                if (!alreadyCalled && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                              "conversation_1",
                              tool.Name,
                              new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" })]);
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
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

        /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
        private sealed class RenderChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                bool alreadyDrew = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

                if (!alreadyDrew && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                              "conversation_1",
                              tool.Name,
                              new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "a card" })]);
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "here it is.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
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

    /// <summary>Reads what one Responses answer carries.</summary>
    internal static class ResponsesAnswer
    {
        /// <summary>Reads the assistant text of one answer.</summary>
        /// <param name="body">The answer.</param>
        /// <returns>Every output text, joined.</returns>
        internal static string OutputText(this JsonNode body)
        {
            return string.Join("", body["output"]?.AsArray()
                        .SelectMany(o => o?["content"]?.AsArray() ?? [])
                        .Select(c => c?["text"]?.GetValue<string>() ?? "") ?? []);
        }

        /// <summary>Reads the id a later turn hangs off: the conversation, or the response.</summary>
        /// <param name="body">The answer.</param>
        /// <returns>The continuation id.</returns>
        internal static string ContinuationId(this JsonNode body)
        {
            return body["conversation"] is JsonObject conversation
                && conversation["id"]?.GetValue<string>() is { Length: > 0 } conversationId
                ? conversationId
                : body["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Answer carries no continuation id.");
        }
    }
}

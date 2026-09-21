using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Registry;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The wire an approval travels: one SSE field that asks, and one request field that answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like the tool loop facts, an approval is host business the caller must see: the model asked to
    /// run something it may not run alone, so the request rides its own <c>agentcore_approval</c>
    /// field, and the answer comes back on <c>agentcore.approval</c> naming the same request id on the
    /// same conversation. A client that does not speak the dialect reads text frames alone.
    /// </para>
    /// <para>
    /// Every test here runs offline against a fake model, on a real socket.
    /// </para>
    /// </remarks>
    public sealed class ApprovalWireTests
    {
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

        [Fact]
        public async Task AGatedToolCall_StreamsAnApprovalEventWithTheConversationFacts()
        {
            int sent = 0;
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

            using HttpResponseMessage response = await PostStreamAsync(host, "send it");
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            JsonElement approval = Assert.Single(ApprovalEventsOf(events));
            Assert.False(string.IsNullOrEmpty(approval.GetProperty("request_id").GetString()));
            Assert.Equal("send_email", approval.GetProperty("tool").GetString());
            Assert.Equal("a@b.com", approval.GetProperty("arguments").GetProperty("to").GetString());

            Assert.Empty(ResponsesHost.TextDeltas(events));
            Assert.Equal(0, sent);
        }

        [Fact]
        public async Task AGatedToolCall_AnswersWholeWithTheRequestOnTheTurn()
        {
            int sent = 0;
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

            using HttpResponseMessage response = await PostTextAsync(host, "send it");
            JsonNode body = await ResponsesHost.ReadJsonAsync(response);

            Assert.Equal(string.Empty, body.OutputText());
            JsonElement approvals = JsonDocument.Parse(body["metadata"]!["approvals"]!.GetValue<string>()).RootElement;
            JsonElement approval = Assert.Single(approvals.EnumerateArray());
            Assert.Equal("send_email", approval.GetProperty("tool").GetString());
            Assert.Equal(0, sent);
        }

        [Fact]
        public async Task AnApprovalAnswer_RunsTheToolAndReplies()
        {
            int sent = 0;
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

            using HttpResponseMessage first = await PostTextAsync(host, "send it");
            JsonNode asked = await ResponsesHost.ReadJsonAsync(first);
            string conversation = asked.ContinuationId();
            string requestId = JsonDocument.Parse(
                asked["metadata"]!["approvals"]!.GetValue<string>())
                .RootElement[0].GetProperty("request_id").GetString() ?? string.Empty;

            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requestId, approved: true);
            JsonNode replied = await ResponsesHost.ReadJsonAsync(second);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(1, sent);
            Assert.Equal("done.", replied.OutputText());
        }

        [Fact]
        public async Task ARejection_LeavesTheToolUnrun()
        {
            int sent = 0;
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

            using HttpResponseMessage first = await PostTextAsync(host, "send it");
            JsonNode asked = await ResponsesHost.ReadJsonAsync(first);
            string conversation = asked.ContinuationId();
            string requestId = JsonDocument.Parse(
                asked["metadata"]!["approvals"]!.GetValue<string>())
                .RootElement[0].GetProperty("request_id").GetString() ?? string.Empty;

            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requestId, approved: false);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(0, sent);
        }

        [Fact]
        public async Task AnApprovalAnswerForAnUnknownConversation_IsNotFound()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

            using HttpResponseMessage response = await PostApprovalAsync(host, "conv-that-never-existed", "req-1", approved: true);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task AnApprovalAnswerWithNoConversation_IsBadRequest()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

            using HttpResponseMessage response = await PostApprovalAsync(host, null, "req-1", approved: true);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AnApprovalAnswerForARequestNothingAsked_IsConflict()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

            using HttpResponseMessage first = await PostTextAsync(host, "send it");
            JsonNode asked = await ResponsesHost.ReadJsonAsync(first);
            string conversation = asked.ContinuationId();

            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, "req-nothing-asked", approved: true);

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        /// <summary>Sends one turn of words.</summary>
        private static Task<HttpResponseMessage> PostTextAsync(ResponsesHost host, string text, string? conversation = null)
        {
            return host.PostAsync(conversation is { Length: > 0 }
                        ? $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "{{text}}" }"""
                        : $$"""{ "stream": false, "input": "{{text}}" }""");
        }

        /// <summary>Sends one turn of words and reads the answer as it arrives.</summary>
        /// <remarks>
        /// The dialect member opts the stream into the browser parts: without it the frames carry
        /// text alone and an approval ask would suspend the turn in silence.
        /// </remarks>
        private static Task<HttpResponseMessage> PostStreamAsync(ResponsesHost host, string text, string? conversation = null)
        {
            return host.PostAsync(conversation is { Length: > 0 }
                        ? $$"""{ "stream": true, "conversation": "{{conversation}}", "input": "{{text}}", "agentcore": { "message_id": "m1" } }"""
                        : $$"""{ "stream": true, "input": "{{text}}", "agentcore": { "message_id": "m1" } }""");
        }

        /// <summary>Answers one pending approval.</summary>
        private static Task<HttpResponseMessage> PostApprovalAsync(
            ResponsesHost host, string? conversation, string requestId, bool approved)
        {
            string answer = $$"""{ "approval": { "request_id": "{{requestId}}", "approved": {{(approved ? "true" : "false")}} } }""";
            return host.PostAsync(conversation is { Length: > 0 }
                ? $$"""{ "stream": false, "conversation": "{{conversation}}", "input": [], "agentcore": {{answer}} }"""
                : $$"""{ "stream": false, "input": [], "agentcore": {{answer}} }""");
        }

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

        /// <summary>Picks the <c>agentcore_approval</c> payloads out of events already read.</summary>
        private static List<JsonElement> ApprovalEventsOf(IEnumerable<string> events)
        {
            return [.. events
                .Select(text => JsonDocument.Parse(text).RootElement)
                .Where(chunk => chunk.TryGetProperty("agentcore_approval", out JsonElement approval)
                                && approval.ValueKind != JsonValueKind.Null)
                .Select(chunk => chunk.GetProperty("agentcore_approval"))];
        }

        /// <summary>A tool source that serves one gated id, standing in for an approval-required host tool.</summary>
        private sealed class GatedSource(Func<AITool> build) : IToolSource
        {
            public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
                ToolSourceContext context, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                                [new ToolRegistration("send_email", "Send an email.", build)]);
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
    }
}

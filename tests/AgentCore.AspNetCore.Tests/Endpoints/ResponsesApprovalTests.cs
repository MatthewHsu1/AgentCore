using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The Responses path when a tool call needs approval: the turn stops with the request in metadata,
    /// and only a matching approval answer runs the tool and continues.
    /// </summary>
    public sealed class ResponsesApprovalTests
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

        // The answer is raised when the turn takes it in, under the function call id the gated fake gives its call.
        [Fact]
        public async Task ApprovalAnswer_IsRaisedOnceAsAHumansApproval()
        {
            RecordingHook hook = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                ApprovalYaml,
                new GatedToolCallingChatClient(),
                configure: options =>
                {
                    options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { })));
                    _ = options.UseHooks(hook);
                });

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "send it" }""");
            JsonNode asked = await ResponsesHost.ReadJsonAsync(first);
            string? requestId = JsonDocument.Parse(
                asked["metadata"]!["approvals"]!.GetValue<string>())
                .RootElement[0].GetProperty("request_id").GetString();
            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{asked.ContinuationId()}}", "input": [], "agentcore": { "approval": { "request_id": "{{requestId}}", "approved": true } } }""");
            _ = await hook.WaitForAsync<TurnCompleted>(completed => completed.Scope.TurnIndex == 1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(
                [(ApprovalState.Asked, ApprovalBy.Human, "conversation_1"), (ApprovalState.Approved, ApprovalBy.Human, "conversation_1")],
                hook.Of<ApprovalChanged>().Select(changed => (changed.State, changed.By, changed.CallId)));
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
    }
}

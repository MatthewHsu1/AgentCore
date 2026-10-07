using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Registry;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// One model round that calls two approval-required tools at once: the caller sees both requests,
    /// answers them one by one, and each tool runs only on its own approval.
    /// </summary>
    public sealed class ResponsesParallelApprovalTests
    {
        /// <summary>What Microsoft.Extensions.AI tells the model about a call a person refused.</summary>
        private const string Rejected = "Tool call invocation rejected.";

        /// <summary>The reason a request carries when the caller sent words instead of an answer.</summary>
        private const string MovedOnReason = "the user moved on without answering.";

        private const string TwoToolsYaml =
            """
        apiVersion: agentcore/v1
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "send both", tools: [ send_email, send_sms ] }
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

        private static readonly string AutoRuleYaml = TwoToolsYaml.Replace(
            "tools: [ send_email, send_sms ] }", "tools: [ send_email, send_sms ], approval: { auto: [ nothing_matches ] } }", StringComparison.Ordinal);

        [Fact]
        public async Task TwoGatedCallsInOneRound_AreBothAskedAndAnsweredOneByOne()
        {
            Dictionary<string, int> ran = Ran();
            await using ResponsesHost host = await StartAsync(TwoToolsYaml, ran);

            (string conversation, Dictionary<string, string> requests) = await AskAsync(host);
            Assert.Equal(["send_email", "send_sms"], requests.Keys.Order(StringComparer.Ordinal));

            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requests["send_email"], approved: true);
            JsonNode stillAsking = await ResponsesHost.ReadJsonAsync(second);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(string.Empty, stillAsking.OutputText());
            Assert.Equal(["send_sms"], RequestIdsByTool(stillAsking).Keys);
            Assert.Equal(0, ran["send_email"] + ran["send_sms"]);

            using HttpResponseMessage third = await PostApprovalAsync(host, conversation, requests["send_sms"], approved: false);
            JsonNode replied = await ResponsesHost.ReadJsonAsync(third);

            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
            Assert.Equal(1, ran["send_email"]);
            Assert.Equal(0, ran["send_sms"]);
            Assert.Equal(["sent:send_email", Rejected], ToldTheModel(replied));
        }

        [Fact]
        public async Task AHeldDenial_IsRaisedOnceAndBlocksItsToolOnTheFinalTurn()
        {
            RecordingHook hook = new();
            await using ResponsesHost host = await StartAsync(TwoToolsYaml, Ran(), hook);

            (string conversation, Dictionary<string, string> requests) = await AskAsync(host);
            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requests["send_email"], approved: false);
            using HttpResponseMessage third = await PostApprovalAsync(host, conversation, requests["send_sms"], approved: true);
            _ = await hook.WaitForAsync<TurnCompleted>(completed => completed.Scope.TurnIndex == 2)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(
                [
                    (ApprovalState.Asked, "send_email"), (ApprovalState.Asked, "send_sms"),
                    (ApprovalState.Denied, "send_email"), (ApprovalState.Approved, "send_sms"),
                ],
                hook.Of<ApprovalChanged>().Select(changed => (changed.State, changed.ToolName)));
            Assert.Equal(
                [("send_email", ToolOutcome.Blocked), ("send_sms", ToolOutcome.Ok)],
                hook.Of<ToolCalled>().Select(called => (called.ToolName, called.Outcome)).Order());
        }

        // The given approval still counts; the open request is refused for the caller, and the words run in the same request.
        [Fact]
        public async Task NewWordsInsteadOfTheLastAnswer_RefuseItAndReplyToTheWords()
        {
            RecordingHook hook = new();
            Dictionary<string, int> ran = Ran();
            await using ResponsesHost host = await StartAsync(TwoToolsYaml, ran, hook);

            (string conversation, Dictionary<string, string> requests) = await AskAsync(host);
            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requests["send_email"], approved: true);
            using HttpResponseMessage words = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "never mind" }""");
            JsonNode replied = await ResponsesHost.ReadJsonAsync(words);
            _ = await hook.WaitForAsync<TurnCompleted>(completed => completed.Scope.TurnIndex == 2)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, words.StatusCode);
            Assert.Equal(["sent:send_email", Rejected + " " + MovedOnReason], ToldTheModel(replied));
            Assert.Equal(1, ran["send_email"]);
            Assert.Equal(0, ran["send_sms"]);
            Assert.Equal(
                [
                    (ApprovalState.Asked, ApprovalBy.Human, "send_email", null), (ApprovalState.Asked, ApprovalBy.Human, "send_sms", null),
                    (ApprovalState.Approved, ApprovalBy.Human, "send_email", null), (ApprovalState.Denied, ApprovalBy.Human, "send_sms", MovedOnReason),
                ],
                hook.Of<ApprovalChanged>().Select(changed => (changed.State, changed.By, changed.ToolName, changed.Reason)));
            Assert.Equal(
                [("send_email", ToolOutcome.Ok), ("send_sms", ToolOutcome.Blocked)],
                hook.Of<ToolCalled>().Select(called => (called.ToolName, called.Outcome)).Order());
        }

        [Fact]
        public async Task AnAnswerAlreadyHeld_IsConflict()
        {
            await using ResponsesHost host = await StartAsync(TwoToolsYaml, Ran());

            (string conversation, Dictionary<string, string> requests) = await AskAsync(host);
            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requests["send_email"], approved: true);
            using HttpResponseMessage again = await PostApprovalAsync(host, conversation, requests["send_email"], approved: false);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        [Fact]
        public async Task TheStoredConversation_KeepsEachRequestAndAnswerOnce_AndRunsALaterTurn()
        {
            await using ResponsesHost host = await StartAsync(TwoToolsYaml, Ran());

            (string conversation, Dictionary<string, string> requests) = await AskAsync(host);
            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, requests["send_email"], approved: true);
            using HttpResponseMessage third = await PostApprovalAsync(host, conversation, requests["send_sms"], approved: true);
            using HttpResponseMessage later = await host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "thanks" }""");
            JsonNode thanked = await ResponsesHost.ReadJsonAsync(later);

            Assert.Equal(["sent:send_email", "sent:send_sms"], ToldTheModel(thanked));
            List<AIContent> stored = [.. (await StoredRows.SettleAsync(host.Services, conversation, atLeast: 6))
                .SelectMany(row => row.Content.Contents)];
            Assert.Equal(
                requests.Values.Order(StringComparer.Ordinal),
                stored.OfType<ToolApprovalRequestContent>().Select(request => request.RequestId).Order(StringComparer.Ordinal));
            Assert.Equal(
                requests.Values.Order(StringComparer.Ordinal),
                stored.OfType<ToolApprovalResponseContent>().Select(answer => answer.RequestId).Order(StringComparer.Ordinal));
        }

        // With an auto: block MAF's approval layer shows the requests one at a time; the second one is shown again
        // from its queue and must not be stored a second time.
        [Fact]
        public async Task UnderAnAutoRule_TwoGatedCallsAreAskedInTurnAndReply()
        {
            Dictionary<string, int> ran = Ran();
            await using ResponsesHost host = await StartAsync(
                AutoRuleYaml,
                ran);

            (string conversation, Dictionary<string, string> first) = await AskAsync(host);
            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, Assert.Single(first).Value, approved: true);
            KeyValuePair<string, string> next = Assert.Single(RequestIdsByTool(await ResponsesHost.ReadJsonAsync(second)));
            using HttpResponseMessage third = await PostApprovalAsync(host, conversation, next.Value, approved: true);
            JsonNode replied = await ResponsesHost.ReadJsonAsync(third);

            Assert.NotEqual(first.Single().Key, next.Key);
            Assert.Equal(1, ran["send_email"]);
            Assert.Equal(1, ran["send_sms"]);
            Assert.Equal(["sent:send_email", "sent:send_sms"], ToldTheModel(replied));
        }

        // MAF's approval layer keeps the first answer and sends it on with the last, so the refusal reaches the agent
        // only on the final turn and must still read as Blocked, not as an undeclared tool.
        [Fact]
        public async Task UnderAnAutoRule_ARefusalAnsweredFirstIsBlockedOnTheFinalTurn()
        {
            RecordingHook hook = new();
            await using ResponsesHost host = await StartAsync(AutoRuleYaml, Ran(), hook);

            (string conversation, Dictionary<string, string> first) = await AskAsync(host);
            using HttpResponseMessage second = await PostApprovalAsync(host, conversation, Assert.Single(first).Value, approved: false);
            KeyValuePair<string, string> next = Assert.Single(RequestIdsByTool(await ResponsesHost.ReadJsonAsync(second)));
            using HttpResponseMessage third = await PostApprovalAsync(host, conversation, next.Value, approved: true);
            _ = await hook.WaitForAsync<TurnCompleted>(completed => completed.Scope.TurnIndex == 2)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(
                new[] { (first.Single().Key, ToolOutcome.Blocked), (next.Key, ToolOutcome.Ok) }.Order(),
                hook.Of<ToolCalled>().Select(called => (called.ToolName, called.Outcome)).Order());
        }

        private static Dictionary<string, int> Ran()
        {
            return new(StringComparer.Ordinal) { ["send_email"] = 0, ["send_sms"] = 0 };
        }

        private static Task<ResponsesHost> StartAsync(string yaml, Dictionary<string, int> ran, AgentHook? hook = null)
        {
            return ResponsesHost.StartAsync(
                yaml,
                new TwoGatedCallsChatClient(),
                configure: options =>
                {
                    _ = options.AddToolSource(_ => new TwoGatedSource(name => ran[name]++));
                    if (hook is not null)
                    {
                        _ = options.UseHooks(hook);
                    }
                });
        }

        private static async Task<(string Conversation, Dictionary<string, string> Requests)> AskAsync(ResponsesHost host)
        {
            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "send both" }""");
            JsonNode asked = await ResponsesHost.ReadJsonAsync(first);
            return (asked.ContinuationId(), RequestIdsByTool(asked));
        }

        /// <summary>What the fake model said each call returned, in call order.</summary>
        private static string[] ToldTheModel(JsonNode replied)
        {
            return [.. replied.OutputText().Split('\n').Select(line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..])];
        }

        private static Dictionary<string, string> RequestIdsByTool(JsonNode body)
        {
            return body["metadata"]?["approvals"]?.GetValue<string>() is { } approvals
                ? JsonDocument.Parse(approvals).RootElement.EnumerateArray().ToDictionary(
                    approval => approval.GetProperty("tool").GetString()!,
                    approval => approval.GetProperty("request_id").GetString()!,
                    StringComparer.Ordinal)
                : [];
        }

        private static Task<HttpResponseMessage> PostApprovalAsync(ResponsesHost host, string conversation, string requestId, bool approved)
        {
            return host.PostAsync(
                $$"""{ "stream": false, "conversation": "{{conversation}}", "input": [], "agentcore": { "approval": { "request_id": "{{requestId}}", "approved": {{(approved ? "true" : "false")}} } } }""");
        }

        /// <summary>Serves two approval-required tools; each returns <c>sent:name</c> when it runs.</summary>
        private sealed class TwoGatedSource(Action<string> onRun) : IToolSource
        {
            public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
                ToolSourceContext context, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                    [Gated("send_email", "Send an email."), Gated("send_sms", "Send a text message.")]);
            }

            private ToolRegistration Gated(string name, string description)
            {
                return new ToolRegistration(name, description, () => new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                    (string to) =>
                    {
                        onRun(name);
                        return $"sent:{name}";
                    },
                    name,
                    description)));
            }
        }
    }
}

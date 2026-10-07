using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// U9 (owner ruling, #23): while one entry holds a conversation live, a request from another entry on
    /// that same id is blocked rather than switched. The Responses path maps the owner's refusal to its
    /// own 409, distinct from the busy/turn-conflict 409s <see cref="ResponsesTurnConflict"/> answers.
    /// </summary>
    public sealed class ResponsesConversationInUseTests
    {
        private const string TwoEntryYaml =
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
              phone:
                agent: greeter
              chat:
                agent: greeter
            """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 60_000)]
        public async Task ARequestFromAnotherEntryOnALiveConversation_Answers409WithItsOwnMessage()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml, new FragmentingChatClient("phone reply"));
            string conversation = "conv_" + Guid.NewGuid().ToString("N");

            using HttpResponseMessage first = await PostAsync(host.Client, "phone", conversation, "hi");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using HttpResponseMessage second = await PostAsync(host.Client, "chat", conversation, "hi");

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            JsonNode error = (await ResponsesHost.ReadJsonAsync(second))["error"]!;
            string message = error["message"]!.GetValue<string>();

            Assert.Equal("conversation_in_use", error["code"]!.GetValue<string>());
            Assert.NotEqual(ResponsesTurnConflict.Code, error["code"]!.GetValue<string>());
            Assert.Contains("phone", message, StringComparison.Ordinal);
            Assert.NotEqual(ResponsesTurnConflict.BusyMessage, message);
            Assert.NotEqual(ResponsesTurnConflict.ConflictMessage, message);
        }

        [Fact(Timeout = 60_000)]
        public async Task OnceThatEntrysSessionIsGone_AnotherEntryOpensTheSameIdWithoutTheRefusal()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml, new FragmentingChatClient("phone reply", "chat reply"));
            string conversation = "conv_" + Guid.NewGuid().ToString("N");

            using HttpResponseMessage first = await PostAsync(host.Client, "phone", conversation, "hi");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            await CloseAsync(host, "phone", conversation);

            using HttpResponseMessage second = await PostAsync(host.Client, "chat", conversation, "hi");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        private static async Task CloseAsync(ResponsesHost host, string entry, string conversation)
        {
            IConversationSessions sessions = host.Services.GetRequiredService<EntryRegistry>().Sessions;
            await sessions.CloseAsync(entry, conversation, Ct);
        }

        private static Task<HttpResponseMessage> PostAsync(HttpClient client, string entry, string conversation, string input)
        {
            HttpRequestMessage request = new(HttpMethod.Post, $"/v1/{entry}/responses")
            {
                Content = new StringContent(
                    /*lang=json,strict*/ $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "{{input}}" }""",
                    Encoding.UTF8,
                    "application/json"),
            };

            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }
    }
}

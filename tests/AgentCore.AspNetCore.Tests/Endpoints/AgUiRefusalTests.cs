using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The AG-UI route's refusals: every refusal before a turn starts is one RUN_ERROR
    /// in a 200 stream, or the status form under ?refusals=status.
    /// </summary>
    public sealed class AgUiRefusalTests
    {
        private const string Yaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: front, instructions: "answer visitors", model: { ref: front } }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: front }
            entries:
              main:
                agent: front
            """;

        private const string Route = "/agui/main";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Run(string threadId, IEnumerable<JsonObject> messages)
        {
            JsonObject body = new()
            {
                ["threadId"] = threadId,
                ["runId"] = "r1",
                ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)m.DeepClone())]),
                ["tools"] = new JsonArray(),
                ["context"] = new JsonArray(),
                ["forwardedProps"] = new JsonObject(),
            };
            return body.ToJsonString();
        }

        private static JsonObject User(string id, string text) => new() { ["id"] = id, ["role"] = "user", ["content"] = text };

        private static Task<ResponsesHost> StartAsync(FragmentingChatClient reply, params AgentHook[] hooks)
        {
            return ResponsesHost.StartAsync(
                Yaml,
                reply,
                options => _ = options.UseHooks(hooks),
                map: app => app.MapAgUi());
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string route = Route)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, route)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("text/event-stream");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }

        private static async Task<List<JsonElement>> ReadEventsAsync(HttpResponseMessage response)
        {
            List<JsonElement> events = [];
            await using Stream body = await response.Content.ReadAsStreamAsync(Ct);
            using StreamReader reader = new(body, Encoding.UTF8);
            while (await reader.ReadLineAsync(Ct) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    events.Add(JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone());
                }
            }

            return events;
        }

        [Fact(Timeout = 60_000)]
        public async Task UnknownThreadId_IsSingleRunErrorThreadNotFound_ModelNeverRuns_RowsUnchanged()
        {
            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);

            using HttpResponseMessage response = await PostAsync(host.Client, Run("no-such", [User("u1", "hi")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement only = Assert.Single(events);
            Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
            Assert.Equal("thread_not_found", only.GetProperty("code").GetString());
            Assert.Null(reply.LastRequest);
            IConversations conversations = host.Services.GetRequiredService<IConversations>();
            Assert.Null(await conversations.GetAsync("no-such", Ct));
        }

        [Fact(Timeout = 60_000)]
        public async Task HookRefusal_IsIdenticalCodeAndTextToUnknownThread()
        {
            using FragmentingChatClient refusingReply = new("unused");
            await using ResponsesHost refusing = await StartAsync(refusingReply, new RefuseEveryConversation());
            const string ThreadId = "t1";
            _ = await refusing.Services.GetRequiredService<IConversations>().CreateAsync(ThreadId, Ct);

            JsonElement refused;
            using (HttpResponseMessage response = await PostAsync(refusing.Client, Run(ThreadId, [User("u1", "hi")])))
            {
                refused = Assert.Single(await ReadEventsAsync(response));
            }

            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);

            JsonElement missing;
            using (HttpResponseMessage response = await PostAsync(host.Client, Run(ThreadId, [User("u1", "hi")])))
            {
                missing = Assert.Single(await ReadEventsAsync(response));
            }

            Assert.Equal("thread_not_found", refused.GetProperty("code").GetString());
            Assert.Equal(missing.GetProperty("code").GetString(), refused.GetProperty("code").GetString());
            Assert.Equal(missing.GetProperty("message").GetString(), refused.GetProperty("message").GetString());
            Assert.Null(refusingReply.LastRequest);
        }

        [Fact(Timeout = 60_000)]
        public async Task MissingThreadId_IsSingleRunErrorMissingThread()
        {
            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);

            using HttpResponseMessage response = await PostAsync(host.Client, Run(string.Empty, [User("u1", "hi")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("missing_thread", Assert.Single(events).GetProperty("code").GetString());
            Assert.Null(reply.LastRequest);
        }

        [Fact(Timeout = 60_000)]
        public async Task MalformedBody_IsSingleRunErrorMalformedBody()
        {
            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);

            using HttpResponseMessage response = await PostAsync(host.Client, "{not json");
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("malformed_body", Assert.Single(events).GetProperty("code").GetString());
            Assert.Null(reply.LastRequest);
        }

        [Fact(Timeout = 60_000)]
        public async Task UnknownEntry_IsSingleRunErrorUnknownEntry()
        {
            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);

            using HttpResponseMessage response = await PostAsync(host.Client, Run("whatever", [User("u1", "hi")]), "/agui/nobody");
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("unknown_entry", Assert.Single(events).GetProperty("code").GetString());
            Assert.Null(reply.LastRequest);
        }

        [Fact(Timeout = 60_000)]
        public async Task RefusalsAsStatus_UnknownThread_Is404WithCodeInBody()
        {
            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);

            using HttpResponseMessage response = await PostAsync(host.Client, Run("no-such", [User("u1", "hi")]), Route + "?refusals=status");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            JsonNode body = await ResponsesHost.ReadJsonAsync(response);
            Assert.Equal("thread_not_found", body["error"]!["code"]!.GetValue<string>());
            Assert.Null(reply.LastRequest);
        }

        private sealed class RefuseEveryConversation : AgentHook
        {
            public override ValueTask BeforeConversationAsync(ConversationGate gate, CancellationToken cancellationToken)
            {
                gate.Refuse();
                return default;
            }
        }
    }
}

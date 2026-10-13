using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Application.Runtime.Turn;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The AG-UI route's content mapping: sources wait for the words so the tool group stays one,
    /// a compaction pass is one activity replaced in place, a failed tool result carries
    /// <c>isError</c> plus <c>_meta</c>, reasoning streams as reasoning events, and a model fault
    /// mid-turn still streams the fallback as text.
    /// </summary>
    public sealed class AgUiContentTests
    {
        private const string SourceYaml =
            """
            apiVersion: agentcore/v1
            tools:
              - id: look_it_up
                kind: binding
                binds: LookItUp
                description: Look something up for the caller.
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller", tools: [ look_it_up ] }
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

        private const string Fallback = "Sorry, the order service did not answer.";

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

        private static async Task<ResponsesHost> StartAsync(IChatClient reply, Action<AgentCoreOptions>? configure = null)
        {
            return await ResponsesHost.StartAsync(
                TwoStepChatClient.Yaml,
                reply,
                options =>
                {
                    TwoStepChatClient.BindTool(options);
                    configure?.Invoke(options);
                },
                map: app => app.MapAgUi());
        }

        private static async Task<string> OpenThreadAsync(ResponsesHost host, string threadId)
        {
            _ = await host.Services.GetRequiredService<IConversations>().CreateAsync(threadId, Ct);
            return threadId;
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string route = "/agui/main")
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

        private static List<JsonElement> OfType(List<JsonElement> events, string type)
        {
            return events.Where(e => e.GetProperty("type").GetString() == type).ToList();
        }

        private static string ClientText(List<JsonElement> events)
        {
            return string.Concat(
                OfType(events, "TEXT_MESSAGE_CONTENT").Select(e => e.GetProperty("delta").GetString() ?? string.Empty));
        }

        private static List<JsonElement> Custom(List<JsonElement> events, string name)
        {
            return OfType(events, "CUSTOM").Where(e => e.GetProperty("name").GetString() == name).ToList();
        }
        [Fact(Timeout = 60_000)]
        public async Task Sources_WaitForTheWords_SoTheToolGroupStaysOne_AndStreamOnce()
        {
            TwoStepChatClient model = new([TwoStepChatClient.Call], ["it is an overcurrent trip."]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                SourceYaml,
                model,
                options => options.Bind(
                    TwoStepChatClient.ToolBinding,
                    (TurnInvocation? turn) =>
                    {
                        turn?.Sources?.Publish(
                            new SourceReference
                            {
                                SourceId = "card-42",
                                Kind = SourceKind.Document,
                                Title = "Spirit CT900 owner's manual",
                                Origin = "knowledge",
                                Locator = "p.27",
                            },
                            turn.OuterCallId);
                        return ValueTask.FromResult<object?>("E03 is an overcurrent trip.");
                    }),
                map: app => app.MapAgUi());
            string thread = await OpenThreadAsync(host, "t-sources");
            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "what is E03")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");

            JsonElement source = Assert.Single(Custom(events, "agentcore_source"));
            Assert.Equal("card-42", source.GetProperty("value").GetProperty("id").GetString());

            int sourceAt = events.FindIndex(e => e.GetProperty("type").GetString() == "CUSTOM"
                && e.GetProperty("name").GetString() == "agentcore_source");
            int lastTool = events.FindLastIndex(e => (e.GetProperty("type").GetString() ?? string.Empty).StartsWith("TOOL_CALL", StringComparison.Ordinal));
            int firstWords = events.FindIndex(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_START");
            Assert.True(sourceAt > lastTool);
            Assert.True(firstWords >= 0 && sourceAt < firstWords);
        }

        [Fact(Timeout = 60_000)]
        public async Task CompactionStartAndEnd_ReplaceTheSameActivityInPlace()
        {
            await using ResponsesHost host = await StartAsync(new CompactionEmittingChatClient());
            string thread = await OpenThreadAsync(host, "t-compaction");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "hello")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");

            List<JsonElement> activities = OfType(events, "ACTIVITY_SNAPSHOT");
            Assert.Equal(2, activities.Count);
            Assert.Single(activities.Select(a => a.GetProperty("messageId").GetString()).Distinct());
            Assert.Equal("turn-0.compaction", activities[0].GetProperty("messageId").GetString());
            Assert.All(activities, a => Assert.Equal("compaction", a.GetProperty("activityType").GetString()));
            Assert.All(activities, a => Assert.True(a.GetProperty("replace").GetBoolean()));
            Assert.Equal("start", activities[0].GetProperty("content").GetProperty("phase").GetString());
            Assert.Equal("end", activities[1].GetProperty("content").GetProperty("phase").GetString());
            Assert.Empty(Custom(events, "agentcore_compaction"));
        }

        [Fact(Timeout = 60_000)]
        public async Task FailedToolResult_CarriesIsErrorAndMeta_WhileTheSuccessfulOneCarriesNeither()
        {
            TwoStepChatClient model = new(["Let me check.", TwoStepChatClient.Call], [TwoStepChatClient.Call]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoStepChatClient.YamlWithFallback(Fallback),
                model,
                TwoStepChatClient.BindToolThatFailsAfterItsFirstCall,
                map: app => app.MapAgUi());
            string thread = await OpenThreadAsync(host, "t-failed");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "check the order")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");

            List<JsonElement> results = OfType(events, "TOOL_CALL_RESULT");
            Assert.True(results.Count >= 2, $"expected a success and a failure, saw {results.Count}");

            JsonElement success = Assert.Single(results, r => r.GetProperty("content").GetString() == "42");
            Assert.False(success.TryGetProperty("isError", out _));

            List<JsonElement> failed = results.Where(r => r.GetProperty("content").GetString() != "42").ToList();
            Assert.NotEmpty(failed);
            Assert.All(failed, r => Assert.True(r.GetProperty("isError").GetBoolean()));
            Assert.All(failed, r => Assert.True(
                r.GetProperty("_meta").GetProperty("agentcore").GetProperty("failed").GetBoolean()));
        }

        [Fact(Timeout = 60_000)]
        public async Task ReasoningContent_StreamsReasoningEvents_BeforeTheWords()
        {
            await using ResponsesHost host = await StartAsync(new ReasoningChatClient());
            string thread = await OpenThreadAsync(host, "t-reasoning");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "think about it")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");

            string reasoning = string.Concat(OfType(events, "REASONING_MESSAGE_CONTENT")
                .Select(e => e.GetProperty("delta").GetString() ?? string.Empty));
            Assert.Equal("Let me think about E03. It is the overcurrent code.", reasoning);
            Assert.Equal("it is an overcurrent trip.", ClientText(events));
            Assert.True(events.FindIndex(e => e.GetProperty("type").GetString() == "REASONING_START") >= 0);
            Assert.True(
                events.FindIndex(e => e.GetProperty("type").GetString() == "REASONING_END")
                < events.FindIndex(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_START"));
        }

        [Fact(Timeout = 60_000)]
        public async Task ModelFaultMidTurn_StreamsTheFallbackAsText_AndKeepsTwoStoredRows()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoStepChatClient.YamlWithFallback(Fallback),
                new FlakyChatClient(),
                TwoStepChatClient.BindTool,
                map: app => app.MapAgUi());
            string thread = await OpenThreadAsync(host, "t-fallback");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "flaky one")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            Assert.Equal(
                "success",
                OfType(events, "RUN_FINISHED").Single().GetProperty("outcome").GetProperty("type").GetString());
            Assert.Equal("partial " + Fallback, ClientText(events));

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 3);
            List<string> spoken = rows
                .Where(r => r.Content.Role == ChatRole.Assistant)
                .Select(r => r.Content.Text.Trim())
                .ToList();
            Assert.Equal(["partial", Fallback], spoken);
        }

        /// <summary>Notices a compaction pass around one line of words, the way the turn loop delivers notices.</summary>
        private sealed class CompactionEmittingChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new AgentCore.Application.Runtime.Compaction.CompactionContent("start")]);
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new AgentCore.Application.Runtime.Compaction.CompactionContent("end", "compacted")]);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "tidy words.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
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

        /// <summary>Thinks twice, then answers in words.</summary>
        private sealed class ReasoningChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent("Let me think about E03. ")]);
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent("It is the overcurrent code.")]);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "it is an overcurrent trip.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
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

        /// <summary>Speaks half a line, then fails the way a vendor outage does.</summary>
        private sealed class FlakyChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "partial ");
                throw new InvalidOperationException("the scripted model failed on purpose");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
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

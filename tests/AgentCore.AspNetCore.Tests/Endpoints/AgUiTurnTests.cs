using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Application.Runtime.Session;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The AG-UI route's turn pipeline: words run through the same turn loop Responses runs,
    /// and the reply streams back as AG-UI events with turn-k.n ids.
    /// </summary>
    public sealed class AgUiTurnTests
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
            entries:
              main:
                agent: greeter
            """;

        private const string Route = "/agui/main";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Run(string threadId, IEnumerable<JsonObject> messages, JsonObject? forwarded = null)
        {
            JsonObject body = new()
            {
                ["threadId"] = threadId,
                ["runId"] = "r1",
                ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)m.DeepClone())]),
                ["tools"] = new JsonArray(),
                ["context"] = new JsonArray(),
                ["forwardedProps"] = forwarded?.DeepClone() ?? new JsonObject(),
            };
            return body.ToJsonString();
        }

        private static JsonObject User(string id, string text) => new() { ["id"] = id, ["role"] = "user", ["content"] = text };

        private static JsonObject Anchor(string messageId, string? parentId, bool namesParent)
        {
            JsonObject agentcore = new() { ["message_id"] = messageId };
            if (namesParent)
            {
                agentcore["parent_id"] = parentId is null ? JsonValue.Create((string?)null) : parentId;
            }

            return new JsonObject { ["agentcore"] = agentcore };
        }

        private static Task<ResponsesHost> StartAsync(IChatClient reply, Action<AgentCoreOptions>? configure = null)
        {
            return ResponsesHost.StartAsync(
                Yaml,
                reply,
                options =>
                {
                    configure?.Invoke(options);
                },
                map: app => app.MapAgUi());
        }

        private static async Task<string> OpenThreadAsync(ResponsesHost host, string threadId)
        {
            _ = await host.Services.GetRequiredService<IConversations>().CreateAsync(threadId, Ct);
            return threadId;
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

        private static List<JsonElement> OfType(List<JsonElement> events, string type)
        {
            return events.Where(e => e.GetProperty("type").GetString() == type).ToList();
        }

        private static string ClientText(List<JsonElement> events)
        {
            return string.Concat(
                OfType(events, "TEXT_MESSAGE_CONTENT").Select(e => e.GetProperty("delta").GetString() ?? string.Empty));
        }

        private static async Task<List<ConversationMessage>> RowsAsync(ResponsesHost host, string threadId, int atLeast)
        {
            return await StoredRows.SettleAsync(host.Services, threadId, atLeast);
        }

        [Fact(Timeout = 60_000)]
        public async Task TextTurn_StreamsRunStartedSnapshotsTextBlocksCommittedAndFinished_IdsTurnKn()
        {
            TwoStepChatClient model = new(["first half", TwoStepChatClient.Call], ["second half"]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool, map: app => app.MapAgUi());
            string thread = await OpenThreadAsync(host, "t-text");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "explain")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-cache", string.Join(",", response.Headers.CacheControl?.ToString() ?? string.Empty).Replace(" ", string.Empty));

            JsonElement started = OfType(events, "RUN_STARTED").Single();
            Assert.Equal(thread, started.GetProperty("threadId").GetString());

            List<JsonElement> snapshots = OfType(events, "STATE_SNAPSHOT");
            Assert.Equal(2, snapshots.Count);
            JsonElement before = snapshots[0];
            Assert.Equal(events.IndexOf(started) + 1, events.IndexOf(before));
            Assert.Equal("before", before.GetProperty("snapshot").GetProperty("phase").GetString());
            string stage = before.GetProperty("snapshot").GetProperty("stage").GetString()!;
            Assert.Equal(stage, response.Headers.GetValues(ResponsesEndpointRouteBuilderExtensions.StageHeaderName).Single());
            JsonElement after = snapshots[1];
            Assert.Equal(events.IndexOf(OfType(events, "RUN_FINISHED").Single()) - 1, events.IndexOf(after));
            Assert.Equal("after", after.GetProperty("snapshot").GetProperty("phase").GetString());

            List<JsonElement> starts = OfType(events, "TEXT_MESSAGE_START");
            Assert.Equal(2, starts.Count);
            Assert.Equal("turn-0.0", starts[0].GetProperty("messageId").GetString());
            Assert.Equal("turn-0.1", starts[1].GetProperty("messageId").GetString());

            JsonElement toolStart = OfType(events, "TOOL_CALL_START").Single();
            Assert.Equal("turn-0.0", toolStart.GetProperty("parentMessageId").GetString());
            Assert.True(
                events.IndexOf(starts[0]) < events.IndexOf(toolStart)
                && events.IndexOf(toolStart) < events.IndexOf(starts[1]));

            JsonElement committed = OfType(events, "CUSTOM").Single(e => e.GetProperty("name").GetString() == "agentcore_message_committed");
            Assert.NotNull(committed.GetProperty("value").GetProperty("user_message_id").GetString());
            Assert.NotNull(committed.GetProperty("value").GetProperty("reply_message_id").GetString());

            JsonElement finished = OfType(events, "RUN_FINISHED").Single();
            Assert.Equal(events.Count - 1, events.IndexOf(finished));
            Assert.True(
                !finished.TryGetProperty("outcome", out JsonElement outcome)
                || outcome.ValueKind == JsonValueKind.Null
                || outcome.GetProperty("type").GetString() == "success");

            foreach (JsonElement evt in events)
            {
                Assert.True(
                    !evt.TryGetProperty("rawEvent", out JsonElement raw) || raw.ValueKind == JsonValueKind.Null,
                    evt.GetProperty("type").GetString());
            }
        }

        [Fact(Timeout = 60_000)]
        public async Task SecondTurn_ResumesSameThread_ModelSeesBothTurns_NoDuplication()
        {
            using FragmentingChatClient reply = new("first answer", "second answer");
            await using ResponsesHost host = await StartAsync(reply);
            string thread = await OpenThreadAsync(host, "t-two");

            using HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [User("u1", "first question")]));
            _ = await ReadEventsAsync(first);
            using HttpResponseMessage second = await PostAsync(
                host.Client, Run(thread, [User("u1", "first question"), User("u2", "second question")]));
            _ = await ReadEventsAsync(second);

            Assert.NotNull(reply.LastRequest);
            List<string> userTexts = [.. reply.LastRequest!.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];
            Assert.Contains("first question", userTexts);
            Assert.Contains("second question", userTexts);
            Assert.Equal(2, userTexts.Count(text => text == "first question" || text == "second question"));

            List<ConversationMessage> rows = await RowsAsync(host, thread, 4);
            Assert.Equal(4, rows.Count);
        }

        // The four client redo actions ride one anchor rule: the turn hangs off the parent,
        // everything after it is withdrawn, and the line runs once. An edit names a new
        // message; regenerate, retry, and continue resend the same user id with the parent
        // before the redone turn, so the withdrawn span holds that user row.
        [Theory(Timeout = 60_000)]
        [InlineData("edit", new[] { "first question" }, "u2", null, "rewritten question", new[] { "first question", "first answer" })]
        [InlineData("regenerate", new[] { "first question" }, "u1", null, "first question", new[] { "first answer" })]
        [InlineData("retry", new[] { "first question", "second question" }, "u2", "turn-0", "second question", new[] { "second answer" })]
        [InlineData("continue", new[] { "first question", "second question" }, "u2", "turn-0.0", "second question", new[] { "second answer" })]
        public async Task Anchor_TruncatesViaMessageIdAndParent_ModelSeesHistoryToParentPlusLineOnce(
            string _case, string[] seed, string messageId, string? parentId, string line, string[] withdrawn)
        {
            using FragmentingChatClient reply = new("first answer", "second answer", "third answer");
            await using ResponsesHost host = await StartAsync(reply);
            string thread = await OpenThreadAsync(host, "t-anchor-" + _case);

            for (int index = 0; index < seed.Length; index++)
            {
                using HttpResponseMessage seeded = await PostAsync(host.Client, Run(thread, [User("u" + (index + 1), seed[index])]));
                _ = await ReadEventsAsync(seeded);
            }

            JsonObject anchor = Anchor(messageId, parentId, namesParent: true);
            using HttpResponseMessage redone = await PostAsync(
                host.Client, Run(thread, [User(messageId, line)], anchor));
            List<JsonElement> events = await ReadEventsAsync(redone);

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            Assert.NotNull(reply.LastRequest);
            List<string> userTexts = [.. reply.LastRequest!.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];
            Assert.Equal(line, userTexts[^1]);
            Assert.Single(userTexts, line);
            foreach (string gone in withdrawn)
            {
                Assert.DoesNotContain(reply.LastRequest!, m => m.Text.Contains(gone, StringComparison.Ordinal));
            }
        }

        [Fact(Timeout = 60_000)]
        public async Task LiveAndReloadIdsMatch_TurnKn()
        {
            TwoStepChatClient model = new(["first half", TwoStepChatClient.Call], ["second half"]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool, map: app => app.MapAgUi());
            string thread = await OpenThreadAsync(host, "t-reload");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "explain")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            List<string> liveBlocks = OfType(events, "TEXT_MESSAGE_START")
                .Select(e => e.GetProperty("messageId").GetString()!).ToList();
            Assert.Equal(["turn-0.0", "turn-0.1"], liveBlocks);

            List<ConversationMessage> rows = await RowsAsync(host, thread, 4);
            List<string> storedTexts = [.. rows.Where(r => r.Content.Role == ChatRole.Assistant && r.Content.Text.Length > 0).Select(r => r.Content.Text)];
            Assert.Equal(["first half", "second half"], storedTexts);

            JsonElement committed = OfType(events, "CUSTOM").Single(e => e.GetProperty("name").GetString() == "agentcore_message_committed");
            Assert.Equal("u1", committed.GetProperty("value").GetProperty("user_message_id").GetString());
            Assert.Equal(rows[^1].MessageId, committed.GetProperty("value").GetProperty("reply_message_id").GetString());
            Assert.Equal(rows[0].MessageId, committed.GetProperty("value").GetProperty("user_message_id").GetString());
        }
        [Fact(Timeout = 60_000)]
        public async Task Carry_RegenerateReusesResult_EditReRuns()
        {
            int calls = 0;
            CarryChatClient model = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoStepChatClient.Yaml,
                model,
                options => _ = options.Bind(TwoStepChatClient.ToolBinding, (_, _) =>
                {
                    _ = Interlocked.Increment(ref calls);
                    return ValueTask.FromResult<object?>("42");
                }),
                map: app => app.MapAgUi());
            string regenerateThread = await OpenThreadAsync(host, "t-carry-regen");
            string editThread = await OpenThreadAsync(host, "t-carry-edit");

            using HttpResponseMessage firstRegen = await PostAsync(host.Client, Run(regenerateThread, [User("u1", "look up revenue")]));
            _ = await ReadEventsAsync(firstRegen);
            Assert.Equal(1, Volatile.Read(ref calls));

            // The same user id with the parent before the turn: the withdrawn turn held
            // that user row, so its call and result ride along and the tool never re-runs.
            using HttpResponseMessage regen = await PostAsync(
                host.Client, Run(regenerateThread, [User("u1", "look up revenue")], Anchor("u1", null, true)));
            List<JsonElement> regenEvents = await ReadEventsAsync(regen);
            Assert.DoesNotContain(regenEvents, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            Assert.Equal("sorted.", ClientText(regenEvents));
            Assert.Equal(1, Volatile.Read(ref calls));

            using HttpResponseMessage firstEdit = await PostAsync(host.Client, Run(editThread, [User("u1", "look up revenue")]));
            _ = await ReadEventsAsync(firstEdit);
            Assert.Equal(2, Volatile.Read(ref calls));

            // A new user id names a message the withdrawn span never held, so nothing
            // rides along and the tool runs again for the new question.
            using HttpResponseMessage edited = await PostAsync(
                host.Client, Run(editThread, [User("u2", "look up costs")], Anchor("u2", "u1", true)));
            _ = await ReadEventsAsync(edited);
            Assert.Equal(3, Volatile.Read(ref calls));
        }

        [Fact(Timeout = 60_000)]
        public async Task ModelErrorMidTurn_ClientTextEqualsStoredText_FinishIsSuccess()
        {
            FailAfterPartialChatClient model = new();
            await using ResponsesHost host = await StartAsync(model);
            string thread = await OpenThreadAsync(host, "t-fault");

            using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [User("u1", "go")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            JsonElement finished = OfType(events, "RUN_FINISHED").Single();
            Assert.True(
                !finished.TryGetProperty("outcome", out JsonElement outcome)
                || outcome.ValueKind == JsonValueKind.Null
                || outcome.GetProperty("type").GetString() == "success");

            string clientText = ClientText(events);
            Assert.Contains("partial", clientText, StringComparison.Ordinal);

            List<ConversationMessage> rows = await RowsAsync(host, thread, 3);
            string storedText = string.Concat(
                rows.Where(r => r.Content.Role == ChatRole.Assistant).Select(r => r.Content.Text));
            Assert.Equal(storedText, clientText);
        }

        [Fact(Timeout = 120_000)]
        public async Task BusyThread_SecondRunWaits15sThenTurnConflict()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            StallOnCueChatClient model = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml,
                model,
                options =>
                {
                    options.TimeProvider = time;
                },
                map: app => app.MapAgUi());
            string thread = await OpenThreadAsync(host, "t-busy");

            using HttpResponseMessage running = await PostAsync(host.Client, Run(thread, [User("u1", "stall now")]));
            await ReadUntilAsync(running, StallOnCueChatClient.Piece);

            Task<HttpResponseMessage> sending = PostAsync(host.Client, Run(thread, [User("u2", "second")]));
            await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.WaitLimit, 1), sending);
            time.Advance(ConversationBusyMark.WaitLimit);
            using HttpResponseMessage refused = await sending;

            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            JsonElement only = Assert.Single(await ReadEventsAsync(refused));
            Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
            Assert.Equal(ResponsesTurnConflict.Code, only.GetProperty("code").GetString());
            Assert.Equal(ResponsesTurnConflict.BusyMessage, only.GetProperty("message").GetString());
        }

        private static async Task ReadUntilAsync(HttpResponseMessage response, string text)
        {
            Stream body = await response.Content.ReadAsStreamAsync(Ct);
            using StreamReader reader = new(body, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(Ct) is { } line && !line.Contains(text, StringComparison.Ordinal))
            {
            }
        }

        [Fact(Timeout = 60_000)]
        public async Task NoUserMessage_SingleRunError()
        {
            using FragmentingChatClient reply = new("unused");
            await using ResponsesHost host = await StartAsync(reply);
            string thread = await OpenThreadAsync(host, "t-empty");

            using HttpResponseMessage empty = await PostAsync(host.Client, Run(thread, []));
            JsonElement onlyEmpty = Assert.Single(await ReadEventsAsync(empty));
            Assert.Equal("RUN_ERROR", onlyEmpty.GetProperty("type").GetString());
            Assert.Equal("no_user_message", onlyEmpty.GetProperty("code").GetString());

            JsonObject assistant = new() { ["id"] = "a1", ["role"] = "assistant", ["content"] = "old answer" };
            using HttpResponseMessage textless = await PostAsync(host.Client, Run(thread, [assistant]));
            JsonElement onlyTextless = Assert.Single(await ReadEventsAsync(textless));
            Assert.Equal("RUN_ERROR", onlyTextless.GetProperty("type").GetString());
            Assert.Equal("no_user_message", onlyTextless.GetProperty("code").GetString());

            Assert.Null(reply.LastRequest);
        }

        /// <summary>Calls the first tool it is offered until a result is already in the request, then answers in words.</summary>
        private sealed class CarryChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                bool alreadyCalled = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

                if (!alreadyCalled && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "let me check.");
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "call_" + Guid.NewGuid().ToString("N"),
                            tool.Name,
                            new Dictionary<string, object?>(StringComparer.Ordinal))]);
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "sorted.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
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

        private sealed class FailAfterPartialChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
                await Task.Yield();
                throw new InvalidOperationException("the model endpoint failed mid-stream.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
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

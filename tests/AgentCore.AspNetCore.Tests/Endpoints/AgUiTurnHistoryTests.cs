using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Registry;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The AG-UI turns history route and the live regenerate dedupe: the history hands the
/// browser absolute file-door references (never a presigned URL), an interrupt turn reads
/// as requires-action, a regenerate reuses the stored user row's files without filing new
/// bytes, and a trimmed resume answers without re-announcing the call.
/// </summary>
public sealed class AgUiTurnHistoryTests
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
          blobs: { kind: fake }
        entries:
          main:
            agent: greeter
        """;

    private const string OneToolYaml =
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

    private const string Route = "/agui/main";

    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static readonly byte[] Png = Convert.FromBase64String(PngBase64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject TextPart(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ImagePart(string base64, string mimeType = "image/png") => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "data", ["value"] = base64, ["mimeType"] = mimeType },
    };

    private static JsonObject UrlImagePart(string url, string mimeType = "image/png") => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "url", ["value"] = url, ["mimeType"] = mimeType },
    };

    private static JsonObject UserParts(string id, params JsonObject[] parts)
        => new() { ["id"] = id, ["role"] = "user", ["content"] = new JsonArray([.. parts]) };

    private static JsonObject UserText(string id, string text)
        => new() { ["id"] = id, ["role"] = "user", ["content"] = text };

    private static JsonObject AssistantCall(string id, string callId, string name, string args) => new()
    {
        ["id"] = id,
        ["role"] = "assistant",
        ["toolCalls"] = new JsonArray(new JsonObject
        {
            ["id"] = callId,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
        }),
    };

    private static string Run(string threadId, IEnumerable<JsonObject> messages, JsonObject? forwarded = null, params JsonObject[] resume)
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
        if (resume.Length > 0)
        {
            body["resume"] = new JsonArray([.. resume.Select(r => (JsonNode)r.DeepClone())]);
        }
        return body.ToJsonString();
    }

    private static JsonObject Anchor(string messageId, string? parentId, bool namesParent)
    {
        JsonObject agentcore = new() { ["message_id"] = messageId };
        if (namesParent)
        {
            agentcore["parent_id"] = parentId is null ? JsonValue.Create((string?)null) : parentId;
        }

        return new JsonObject { ["agentcore"] = agentcore };
    }

    private static JsonObject Approved(string interruptId)
    {
        return new JsonObject
        {
            ["interruptId"] = interruptId,
            ["status"] = "resolved",
            ["payload"] = new JsonObject { ["approved"] = true },
        };
    }

    private static Task<ResponsesHost> StartAsync(IChatClient reply, FakeBlobStoreAdapter blobs, Action<AgentCoreOptions>? configure = null)
    {
        return ResponsesHost.StartAsync(
            Yaml,
            reply,
            options =>
            {
                _ = options.UseBlobStores(blobs);
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

    private static async Task<JsonNode> TurnsAsync(HttpClient client, string thread)
    {
        using HttpResponseMessage response = await client.GetAsync($"/agui/threads/{thread}/turns", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    [Fact(Timeout = 60_000)]
    public async Task UploadTurn_ShowsAttachmentReference_NeverAPresignedUrl()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-turns-upload");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        _ = await StoredRows.SettleAsync(host.Services, thread, 2);
        JsonNode turns = await TurnsAsync(host.Client, thread);

        JsonNode user = turns["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "user")!;
        JsonNode attachment = user["attachments"]!.AsArray().Single()!;
        Assert.Equal("image", attachment["type"]!.GetValue<string>());
        string image = attachment["content"]![0]!["image"]!.GetValue<string>()!;
        Assert.StartsWith("http", image, StringComparison.Ordinal);
        Assert.Contains($"/agui/threads/{thread}/files/", image, StringComparison.Ordinal);
        Assert.DoesNotContain("blobs.test", image, StringComparison.Ordinal);
        Assert.DoesNotContain("ttl=", image, StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task UploadTurn_DoorlessStore_ShowsReferenceThatStreams()
    {
        NoDoorBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => _ = options.UseBlobStores(blobs),
            map: app => app.MapAgUi());
        string thread = await OpenThreadAsync(host, "t-turns-nodoor");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        _ = await StoredRows.SettleAsync(host.Services, thread, 2);
        JsonNode turns = await TurnsAsync(host.Client, thread);

        JsonNode user = turns["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "user")!;
        string image = user["attachments"]![0]!["content"]![0]!["image"]!.GetValue<string>()!;
        Assert.StartsWith("http", image, StringComparison.Ordinal);
        Assert.DoesNotContain("blobs.test", image, StringComparison.Ordinal);

        using HttpResponseMessage door = await host.Client.GetAsync(
            new Uri(image, UriKind.Absolute).AbsolutePath, Ct);
        Assert.Equal(HttpStatusCode.OK, door.StatusCode);
        Assert.Equal(Png, await door.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task AgentFile_ShowsFileReference_NeverAPresignedUrl()
    {
        string root = Path.Combine(Path.GetTempPath(), "agentcore-turns-" + Guid.NewGuid().ToString("N"));
        string conversation = "conversation-turns-file";
        _ = Directory.CreateDirectory(Path.Combine(root, conversation));
        try
        {
            File.WriteAllText(Path.Combine(root, conversation, "report.csv"), "month,sales\njan,10\n");
            FakeBlobStoreAdapter blobs = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                PublishYaml,
                new PublishingChatClient(),
                configure: options => options.UseWorkspace(root).UseBlobStores(blobs),
                map: app => app.MapAgUi());
            _ = await host.Services.GetRequiredService<IConversations>().CreateAsync(conversation, Ct);

            using (HttpResponseMessage response = await PostAsync(host.Client, Run(conversation, [UserText("u1", "send me the report")])))
            {
                _ = await ReadEventsAsync(response);
            }

            _ = await StoredRows.SettleAsync(host.Services, conversation, 2);
            JsonNode turns = await TurnsAsync(host.Client, conversation);

            JsonNode assistant = turns["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "assistant")!;
            JsonNode file = assistant["content"]!.AsArray().Single(p => p!["type"]!.GetValue<string>() == "file")!;
            string data = file["data"]!.GetValue<string>()!;
            Assert.StartsWith("http", data, StringComparison.Ordinal);
            Assert.EndsWith($"/agui/threads/{conversation}/files/report.csv", data, StringComparison.Ordinal);
            Assert.DoesNotContain("blobs.test", data, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task InterruptTurn_ShowsRequiresAction_WithOpenInterrupts()
    {
        ToolRuns runs = new();
        RecordingChatClient model = new(new SingleCallChatClient());
        FakeBlobStoreAdapter blobs = new();
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            OneToolYaml,
            model,
            configure: options => options.UseBlobStores(blobs).AddToolSource(_ => new OneGatedSource(runs)),
            map: app => app.MapAgUi());
        string thread = await OpenThreadAsync(host, "t-turns-interrupt");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserText("u1", "send it")])))
        {
            List<JsonElement> events = await ReadEventsAsync(first);
            Assert.Equal("interrupt", events[^1].GetProperty("outcome").GetProperty("type").GetString());
        }

        _ = await StoredRows.SettleAsync(host.Services, thread, 2);
        JsonNode turns = await TurnsAsync(host.Client, thread);

        JsonNode assistant = turns["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "assistant")!;
        Assert.Equal("requires-action", assistant["status"]!["type"]!.GetValue<string>());
        Assert.Equal("interrupt", assistant["status"]!["reason"]!.GetValue<string>());
        JsonArray interrupts = assistant["metadata"]!["custom"]!["agui"]!["interrupts"]!.AsArray();
        (string id, string callId) = Assert.Single(interrupts.Select(i => (i!["id"]!.GetValue<string>()!, i!["toolCallId"]!.GetValue<string>()!)));
        JsonNode call = assistant["content"]!.AsArray().Single(p => p!["type"]!.GetValue<string>() == "tool-call")!;
        Assert.Equal(callId, call["toolCallId"]!.GetValue<string>());
        Assert.Equal(id, call["approval"]!["id"]!.GetValue<string>());
        Assert.Empty(runs.All);
    }

    [Fact(Timeout = 60_000)]
    public async Task LiveRegenerate_ReusesStoredFile_BlobCountUnchanged_ModelGetsStoredBytes()
    {
        FakeBlobStoreAdapter blobs = new();
        using RecordingChatClient reply = new(new FragmentingChatClient("seen it"));
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-regen-keep");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("what is this?"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string userId = rows[0].MessageId;
        FileContent stored = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single());
        int count = blobs.Store.Blobs.Count;
        int calls = reply.Calls;

        using HttpResponseMessage response = await PostAsync(
            host.Client,
            Run(thread, [UserParts(userId, TextPart("what is this?"), ImagePart(PngBase64))], Anchor(userId, null, true)));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal(count, blobs.Store.Blobs.Count);
        Assert.Equal(calls + 1, reply.Calls);
        DataContent data = Assert.IsType<DataContent>(
            reply.Requests[^1].SelectMany(m => m.Contents).OfType<DataContent>().Single());
        Assert.Equal(Png, data.Data.ToArray());
        List<ConversationMessage> after = await StoredRows.SettleAsync(host.Services, thread, 2);
        Assert.Equal(stored.Name, Assert.IsType<FileContent>(after[0].Content.Contents.OfType<FileContent>().Single()).Name);
    }

    [Fact(Timeout = 60_000)]
    public async Task EditReplace_FilesOneNewBlob()
    {
        FakeBlobStoreAdapter blobs = new();
        using RecordingChatClient reply = new(new FragmentingChatClient("seen it"));
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-regen-replace");

        string firstBlock;
        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserText("u0", "hello")])))
        {
            firstBlock = (await ReadEventsAsync(first))
                .First(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_START")
                .GetProperty("messageId").GetString()!;
        }

        using (HttpResponseMessage second = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("what is this?"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(second);
        }

        _ = await StoredRows.SettleAsync(host.Services, thread, 4);
        int count = blobs.Store.Blobs.Count;

        // An edit hangs the new words off the previous turn's last block: turn 1's rows go,
        // the replaced blob orphans until the conversation is deleted, and the new bytes file once.
        using HttpResponseMessage response = await PostAsync(
            host.Client,
            Run(thread, [UserParts("u2", TextPart("and this?"), ImagePart(GifBase64))], Anchor("u2", firstBlock, true)));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal(count + 1, blobs.Store.Blobs.Count);
        List<DataContent> seen = [.. reply.Requests.SelectMany(m => m.SelectMany(n => n.Contents)).OfType<DataContent>()];
        Assert.Contains(seen, data => data.Data.ToArray().SequenceEqual(Gif));
        Assert.DoesNotContain(reply.Requests[^1].SelectMany(m => m.Contents).OfType<DataContent>(), data => data.Data.ToArray().SequenceEqual(Png));
    }
    [Fact(Timeout = 60_000)]
    public async Task RegenerateWithMoreFilesThanStoredRow_FilesNormally()
    {
        FakeBlobStoreAdapter blobs = new();
        using RecordingChatClient reply = new(new FragmentingChatClient("seen it"));
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-regen-mismatch");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("what is this?"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string storedId = rows[0].MessageId;
        int count = blobs.Store.Blobs.Count;

        // The anchor still names the stored row, but the body carries two files where the row
        // keeps one: the counts differ, so the body's bytes file like a new message.
        using HttpResponseMessage response = await PostAsync(
            host.Client,
            Run(
                thread,
                [UserParts("u9", TextPart("what is this?"), ImagePart(PngBase64), ImagePart(GifBase64))],
                Anchor(storedId, null, false)));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal(count + 2, blobs.Store.Blobs.Count);
        List<byte[]> seen = [.. reply.Requests[^1].SelectMany(m => m.Contents).OfType<DataContent>().Select(d => d.Data.ToArray())];
        Assert.Contains(seen, bytes => bytes.SequenceEqual(Png));
        Assert.Contains(seen, bytes => bytes.SequenceEqual(Gif));
    }

    [Fact(Timeout = 60_000)]
    public async Task RegenerateWithRawUrl_RefusesUrlNotAllowed()
    {
        FakeBlobStoreAdapter blobs = new();
        using RecordingChatClient reply = new(new FragmentingChatClient("seen it"));
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-regen-raw-url");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("what is this?"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string userId = rows[0].MessageId;
        int calls = reply.Calls;

        // After resolve the only UriContent left is a non-reference link: D25 must not stand
        // stored bytes in for it — the run refuses before the model runs.
        using HttpResponseMessage response = await PostAsync(
            host.Client,
            Run(thread, [UserParts(userId, TextPart("what is this?"), UrlImagePart("https://example.com/evil.png"))], Anchor(userId, null, true)));
        List<JsonElement> events = await ReadEventsAsync(response);

        JsonElement error = Assert.Single(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal("attachment_url_not_allowed", error.GetProperty("code").GetString());
        Assert.Equal(calls, reply.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task TrimmedResume_DoesNotReannounceTheCall_AnswersMatchTrimOff()
    {
        ToolRuns runs = new();
        RecordingChatClient model = new(new SingleCallChatClient());
        FakeBlobStoreAdapter blobs = new();
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            OneToolYaml,
            model,
            configure: options => options.UseBlobStores(blobs).AddToolSource(_ => new OneGatedSource(runs)),
            map: app => app.MapAgUi());

        List<JsonElement> trimmed = await ResumeAsync(host, "t-trim-resume", trimmed: true);
        List<JsonElement> full = await ResumeAsync(host, "t-trim-full", trimmed: false);

        // The trimmed body carries only the asking assistant message, never the full thread.
        // Either way the call is not re-announced, its result lands once, the tool runs with the
        // stored arguments, and the outcome matches.
        foreach (List<JsonElement> answered in new[] { trimmed, full })
        {
            Assert.DoesNotContain(answered, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            Assert.DoesNotContain(answered, e => e.GetProperty("type").GetString() == "TOOL_CALL_START");
            Assert.Equal("success", answered[^1].GetProperty("outcome").GetProperty("type").GetString());
        }

        Assert.Equal(
            full.Where(e => e.GetProperty("type").GetString() == "TOOL_CALL_RESULT").Select(e => e.GetProperty("toolCallId").GetString()),
            trimmed.Where(e => e.GetProperty("type").GetString() == "TOOL_CALL_RESULT").Select(e => e.GetProperty("toolCallId").GetString()));
        Assert.Equal([("send_email", "a@b.com"), ("send_email", "a@b.com")], runs.All);
    }

    private static async Task<List<JsonElement>> ResumeAsync(ResponsesHost host, string thread, bool trimmed)
    {
        _ = await OpenThreadAsync(host, thread);
        string callId;
        string interruptId;
        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserText("u1", "send it")])))
        {
            List<JsonElement> events = await ReadEventsAsync(first);
            interruptId = events[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("id").GetString()!;
            callId = events[^1].GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("toolCallId").GetString()!;
        }

        JsonObject[] messages = trimmed
            ? [AssistantCall("a1", callId, "send_email", """{"to":"a@b.com"}""")]
            : [UserText("u1", "send it"), AssistantCall("a1", callId, "send_email", """{"to":"a@b.com"}""")];
        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, messages, null, Approved(interruptId)));
        return await ReadEventsAsync(response);
    }

    private const string PublishYaml =
        """
        apiVersion: agentcore/v1
        tools:
          - { id: publish, kind: builtin, uses: file.publish }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller", tools: [ publish ] }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
          blobs: { kind: fake }
        entries:
          main:
            agent: greeter
        """;

    private const string GifBase64 = "R0lGODlhAQABAIAAAP///////yH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

    private static readonly byte[] Gif = Convert.FromBase64String(GifBase64);

    private sealed class NoDoorBlobStoreAdapter(string kind = "fake") : IBlobStoreAdapter
    {
        public string Kind => kind;

        public NoDoorBlobStore Store { get; } = new();

        public ValueTask<IBlobStore> OpenAsync(
            BlobProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IBlobStore>(Store);
        }
    }

    private sealed class NoDoorBlobStore : IBlobStore
    {
        private readonly Dictionary<(string Owner, string Name), (string MediaType, byte[] Bytes)> _blobs = [];

        public ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
        {
            using MemoryStream copy = new();
            write.Content.CopyTo(copy);
            lock (_blobs)
            {
                _blobs[(write.OwnerId, write.Name)] = (write.MediaType, copy.ToArray());
            }

            return ValueTask.FromResult(new BlobRef(write.OwnerId, write.Name, write.MediaType, write.Length));
        }

        public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                ? new BlobRead(blob.MediaType, blob.Bytes.Length, new MemoryStream(blob.Bytes))
                : null);
        }

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                ? new BlobRef(ownerId, name, blob.MediaType, blob.Bytes.Length)
                : null);
        }

        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<Uri?>(null);
        }

        public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
        {
            lock (_blobs)
            {
                foreach ((string Owner, string Name) key in _blobs.Keys.Where(key => key.Owner == ownerId).ToList())
                {
                    _ = _blobs.Remove(key);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Calls the publish tool it is offered, once, on the report, then answers in words.</summary>
    private sealed class PublishingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            bool alreadyPublished = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyPublished && options?.Tools?.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == "publish") is { } publish)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "conversation_1",
                        publish.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["path"] = "report.csv",
                            ["title"] = "Quarterly report",
                        })]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "here is the report.");
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
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

    private sealed class ToolRuns
    {
        private readonly Lock _gate = new();
        private readonly List<(string Name, string To)> _all = [];

        public IReadOnlyList<(string Name, string To)> All
        {
            get
            {
                lock (_gate)
                {
                    return [.. _all];
                }
            }
        }

        public void Add(string name, string to)
        {
            lock (_gate)
            {
                _all.Add((name, to));
            }
        }
    }

    private sealed class OneGatedSource(ToolRuns runs) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                [new ToolRegistration("send_email", "Send an email.", () => new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                    (string to) =>
                    {
                        runs.Add("send_email", to);
                        return "sent:send_email";
                    },
                    "send_email",
                    "Send an email.")))]);
        }
    }

    /// <summary>Counts the model requests that reached the wrapped client, keeping every one.</summary>
    private sealed class RecordingChatClient(IChatClient inner) : IChatClient
    {
        private readonly Lock _gate = new();
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> seen = [.. messages];
            lock (_gate)
            {
                _requests.Add(seen);
            }

            _ = Interlocked.Increment(ref _calls);
            await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(seen, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
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

    /// <summary>Calls the first tool it is offered once, then answers with each call's result.</summary>
    private sealed class SingleCallChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            List<FunctionResultContent> results = [.. messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>())];
            if (results.Count == 0 && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call_single", tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" })]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "tool said: " + string.Join('|', results.Select(r => r.Result)));
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

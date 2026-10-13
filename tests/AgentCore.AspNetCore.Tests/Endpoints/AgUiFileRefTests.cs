using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The AG-UI endpoint's stable file references: the absolute door URL a browser holds, the link
/// route that mints a fresh URL, the door that redirects or streams, the live <c>agentcore_file</c>
/// CUSTOM part carrying <c>ref</c> not <c>url</c>, and byte-identical rows after every refusal.
/// </summary>
public sealed class AgUiFileRefTests
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

    private static Task<ResponsesHost> StartAsync(IChatClient reply, FakeBlobStoreAdapter blobs, params AgentHook[] hooks)
    {
        return ResponsesHost.StartAsync(
            Yaml,
            reply,
            options =>
            {
                _ = options.UseBlobStores(blobs);
                if (hooks.Length > 0)
                {
                    _ = options.UseHooks(hooks);
                }
            },
            map: app =>
            {
                _ = app.MapAgUi();
            });
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

    private static string RowsJson(List<ConversationMessage> rows)
        => JsonSerializer.Serialize(rows.Select(r => r.Content), TranscriptJson.Options);

    [Fact]
    public void RefOf_IsAbsoluteHttpShape()
    {
        DefaultHttpContext http = new();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("client.test");
        http.Request.PathBase = new PathString("/base");

        string reference = AgUiFileRefs.RefOf(http.Request, "t1", "in-abc.png");

        Assert.Equal("https://client.test/base/agui/threads/t1/files/in-abc.png", reference);
    }

    [Fact]
    public void PathOf_EscapesThreadAndName()
    {
        Assert.Equal("/agui/threads/t%201/files/a%20b.png", AgUiFileRefs.PathOf("t 1", "a b.png"));
    }

    [Theory]
    [InlineData("https://host.test/agui/threads/t1/files/in-a.png", "t1", "in-a.png")]
    [InlineData("http://host.test/base/agui/threads/t1/files/in-a.png", "t1", "in-a.png")]
    [InlineData("/agui/threads/t1/files/in-a.png", "t1", "in-a.png")]
    public void TryRead_AcceptsHttpAndRootRelative(string value, string thread, string name)
    {
        PathString pathBase = value.Contains("/base/", StringComparison.Ordinal) ? new PathString("/base") : default;

        Assert.True(AgUiFileRefs.TryRead(new Uri(value, UriKind.RelativeOrAbsolute), pathBase, out string foundThread, out string foundName));
        Assert.Equal((thread, name), (foundThread, foundName));
    }

    [Theory]
    [InlineData("data:image/png;base64,aaaa")]
    [InlineData("ftp://host.test/agui/threads/t1/files/in-a.png")]
    [InlineData("https://host.test/other/threads/t1/files/in-a.png")]
    [InlineData("https://host.test/agui/threads/t1/files/")]
    public void TryRead_RejectsForeignShapes(string value)
    {
        Assert.False(AgUiFileRefs.TryRead(new Uri(value, UriKind.RelativeOrAbsolute), default, out _, out _));
    }

    [Fact(Timeout = 60_000)]
    public async Task OwnReference_ResolvesToStoredFile_BeforeTheModel()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-own");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string stored = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single()).Name;
        string reference = $"{host.Client.BaseAddress}agui/threads/{thread}/files/{stored}".Replace("http://", "https://", StringComparison.Ordinal);

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("again"), UrlImagePart(reference))]));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Contains(reply.LastRequest!.SelectMany(m => m.Contents).OfType<DataContent>(), data => data.Data.ToArray().SequenceEqual(Png));
    }

    [Fact(Timeout = 60_000)]
    public async Task OtherConversationReference_Is403_ModelNeverRuns_RowsIdentical()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-mine");
        string other = await OpenThreadAsync(host, "t-other");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(other, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> otherRows = await StoredRows.SettleAsync(host.Services, other, 2);
        string stored = Assert.IsType<FileContent>(otherRows[0].Content.Contents.OfType<FileContent>().Single()).Name;
        IConversationStore store = host.Services.GetRequiredService<IConversationStore>();
        string before = JsonSerializer.Serialize(await store.ReadForSessionAsync(thread, Ct), TranscriptJson.Options);
        int calls = reply.Calls;

        string reference = $"https://foreign.test/agui/threads/{other}/files/{stored}";
        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("again"), UrlImagePart(reference))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_not_owned", only.GetProperty("code").GetString());
        Assert.Equal(calls, reply.Calls);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ReadForSessionAsync(thread, Ct), TranscriptJson.Options));
    }

    [Fact(Timeout = 60_000)]
    public async Task UnknownName_Is404_RowsIdentical()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-unknown");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> before = await StoredRows.SettleAsync(host.Services, thread, 2);
        string beforeJson = RowsJson(before);

        string reference = $"https://host.test/agui/threads/{thread}/files/in-deadbeefdeadbeef.png";
        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("again"), UrlImagePart(reference))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_not_found", only.GetProperty("code").GetString());
        Assert.Equal(beforeJson, RowsJson(await StoredRows.SettleAsync(host.Services, thread, 2)));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReferenceToAFileTheStoreDidNotKeep_Is404_RowsIdentical()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-unkept");
        await blobs.Store.PutAsync(new BlobWrite(thread, "dropped.png", "image/png", new MemoryStream(Png), Png.Length), Ct);
        await host.Services.GetRequiredService<IConversationStore>().AppendAsync(thread, [new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, [new FileContent { Name = "dropped.png", FileId = "cfile_1", MediaType = "image/png", Length = Png.Length }]), "m1")], null, Ct);
        string beforeJson = RowsJson(await StoredRows.SettleAsync(host.Services, thread, 1));

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("again"), UrlImagePart($"https://host.test/agui/threads/{thread}/files/dropped.png"))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("attachment_not_found", only.GetProperty("code").GetString());
        Assert.Equal(0, reply.Calls);
        Assert.Equal(beforeJson, RowsJson(await StoredRows.SettleAsync(host.Services, thread, 1)));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReferenceToAnAgentFile_ReachesTheModelAsBytes()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-agent-file");
        await blobs.Store.PutAsync(new BlobWrite(thread, "chart.png", "image/png", new MemoryStream(Png), Png.Length), Ct);
        await host.Services.GetRequiredService<IConversationStore>().AppendAsync(thread, [new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, [new FileContent { Name = "chart.png", FileId = "cfile_1", MediaType = "image/png", Length = Png.Length, Kept = true }]), "m1")], null, Ct);

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("describe it"), UrlImagePart($"https://host.test/agui/threads/{thread}/files/chart.png"))]));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Contains(reply.LastRequest!.SelectMany(m => m.Contents).OfType<DataContent>(), data => data.Data.ToArray().SequenceEqual(Png));
    }

    [Fact(Timeout = 60_000)]
    public async Task TraversalName_Is400_RowsIdentical()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-traversal");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> before = await StoredRows.SettleAsync(host.Services, thread, 2);
        string beforeJson = RowsJson(before);

        string reference = $"https://host.test/agui/threads/{thread}/files/%2E%2E%2Fevil.png";
        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("again"), UrlImagePart(reference))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_ref_invalid", only.GetProperty("code").GetString());
        Assert.Equal(beforeJson, RowsJson(await StoredRows.SettleAsync(host.Services, thread, 2)));
    }

    [Fact(Timeout = 60_000)]
    public async Task RawUrl_IsLeftForTheAttachmentCheck()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-raw");

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), UrlImagePart("https://attacker.test/a.png"))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_url_not_allowed", only.GetProperty("code").GetString());
        Assert.Null(reply.LastRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task SizeRefusal_NamesAttachmentBytes()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-size-wording");
        byte[] big = new byte[(5 * 1024 * 1024) + 1];

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(Convert.ToBase64String(big)))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("attachment_too_large", only.GetProperty("code").GetString());
        Assert.Contains("bytes", only.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task LinkRoute_MintsFreshUrlAndExpiry_WithNoStore()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-link");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string stored = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single()).Name;

        using HttpResponseMessage link = await host.Client.GetAsync($"/agui/threads/{thread}/files/{stored}/link", Ct);

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        Assert.True(link.Headers.CacheControl?.NoStore == true);
        JsonNode body = await ResponsesHost.ReadJsonAsync(link);
        Assert.StartsWith("https://blobs.test/", body["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(body["expiresAt"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact(Timeout = 60_000)]
    public async Task LinkRoute_WithNoDoor_AnswersTheReferenceItself()
    {
        NoDoorBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => _ = options.UseBlobStores(blobs),
            map: app =>
            {
                _ = app.MapAgUi();
            });
        string thread = await OpenThreadAsync(host, "t-nodoor");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string stored = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single()).Name;

        using HttpResponseMessage link = await host.Client.GetAsync($"/agui/threads/{thread}/files/{stored}/link", Ct);

        JsonNode body = await ResponsesHost.ReadJsonAsync(link);
        Assert.EndsWith($"/agui/threads/{thread}/files/{stored}", body["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(body["expiresAt"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Door_WithWebDoor_RedirectsUnderCallerName()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-door-redir");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        string stored = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single()).Name;

        using HttpClientHandler noFollow = new() { AllowAutoRedirect = false };
        using HttpClient direct = new(noFollow) { BaseAddress = host.Client.BaseAddress };
        using HttpResponseMessage door = await direct.GetAsync($"/agui/threads/{thread}/files/{stored}", Ct);

        Assert.Equal(HttpStatusCode.Found, door.StatusCode);
        Assert.StartsWith("https://blobs.test/", door.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task Door_WithNoDoor_StreamsBytesUnderCallerName()
    {
        NoDoorBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => _ = options.UseBlobStores(blobs),
            map: app =>
            {
                _ = app.MapAgUi();
            });
        string thread = await OpenThreadAsync(host, "t-door-stream");

        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        FileContent file = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single());

        using HttpResponseMessage door = await host.Client.GetAsync($"/agui/threads/{thread}/files/{file.Name}", Ct);

        Assert.Equal(HttpStatusCode.OK, door.StatusCode);
        Assert.Equal("image/png", door.Content.Headers.ContentType?.MediaType);
        Assert.Contains(Uri.EscapeDataString(file.Name), door.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Equal(Png, await door.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task Door_WithNonAsciiName_StreamsBytesUnderEncodedName()
    {
        NoDoorBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => _ = options.UseBlobStores(blobs),
            map: app =>
            {
                _ = app.MapAgUi();
            });
        string thread = await OpenThreadAsync(host, "t-door-utf8");
        IConversationStore store = host.Services.GetRequiredService<IConversationStore>();

        await blobs.Store.PutAsync(new BlobWrite(thread, "in-cafe.png", "image/png", new MemoryStream(Png), Png.Length), Ct);
        await store.AppendAsync(thread, [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, [new FileContent { Name = "in-cafe.png", FileId = "upload:café.png", Title = "café.png", MediaType = "image/png", Length = Png.Length, Kept = true }]), "m1")], null, Ct);

        using HttpResponseMessage door = await host.Client.GetAsync($"/agui/threads/{thread}/files/in-cafe.png", Ct);

        Assert.Equal(HttpStatusCode.OK, door.StatusCode);
        Assert.Contains("caf%C3%A9.png", door.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Equal(Png, await door.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory(Timeout = 60_000)]
    [InlineData("", "a..b.png")]
    [InlineData("", "%01evil.png")]
    [InlineData("/link", "a..b.png")]
    [InlineData("/link", "%01evil.png")]
    public async Task UnsafeName_AtADoor_Is404_StoreNeverAsked(string suffix, string name)
    {
        NoDoorBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => _ = options.UseBlobStores(blobs),
            map: app =>
            {
                _ = app.MapAgUi();
            });
        string thread = await OpenThreadAsync(host, "t-door-unsafe");

        using HttpResponseMessage door = await host.Client.GetAsync($"/agui/threads/{thread}/files/{name}{suffix}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, door.StatusCode);
        Assert.Empty(blobs.Store.Asked);
    }

    [Fact(Timeout = 60_000)]
    public async Task Door_BehindOwnershipHook_RefusedCallerSeesNothing()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs, new RefuseEveryConversation());
        string thread = await OpenThreadAsync(host, "t-door-owned");

        await blobs.Store.PutAsync(new BlobWrite(thread, "in-owned.png", "image/png", new MemoryStream(Png), Png.Length), Ct);

        using HttpResponseMessage door = await host.Client.GetAsync($"/agui/threads/{thread}/files/in-owned.png", Ct);

        Assert.Equal(HttpStatusCode.NotFound, door.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task LivePublish_CarriesRefNotUrl()
    {
        string root = Path.Combine(Path.GetTempPath(), "agentcore-fileref-" + Guid.NewGuid().ToString("N"));
        string conversation = "conversation-pub-ref";
        _ = Directory.CreateDirectory(Path.Combine(root, conversation));
        try
        {
            File.WriteAllText(Path.Combine(root, conversation, "report.csv"), "month,sales\njan,10\n");
            FakeBlobStoreAdapter blobs = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                PublishYaml,
                new PublishingChatClient(),
                configure: options => options.UseWorkspace(root).UseBlobStores(blobs),
                map: app =>
                {
                    _ = app.MapAgUi();
                });
            _ = await host.Services.GetRequiredService<IConversations>().CreateAsync(conversation, Ct);

            using HttpResponseMessage response = await PostAsync(host.Client, Run(conversation, [UserText("u1", "send me the report")]));
            List<JsonElement> events = await ReadEventsAsync(response);

            JsonElement file = events
                .Where(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == "agentcore_file")
                .Select(e => e.GetProperty("value"))
                .Single();
            Assert.Equal("report.csv", file.GetProperty("name").GetString());
            string reference = file.GetProperty("ref").GetString()!;
            Assert.EndsWith($"/agui/threads/{conversation}/files/report.csv", reference, StringComparison.Ordinal);
            Assert.False(file.TryGetProperty("url", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RefuseEveryConversation : AgentHook
    {
        public override ValueTask BeforeConversationAsync(global::AgentCore.Application.Hooks.Gates.ConversationGate gate, CancellationToken cancellationToken)
        {
            gate.Refuse();
            return default;
        }
    }

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

        public List<string> Asked { get; } = [];

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
            Asked.Add(name);
            return ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                ? new BlobRead(blob.MediaType, blob.Bytes.Length, new MemoryStream(blob.Bytes))
                : null);
        }

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            Asked.Add(name);
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
}

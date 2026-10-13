using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
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
/// The AG-UI endpoint's attachment pipeline: an image alone runs a turn, data parts survive
/// sanitize, disallowed types and oversized bodies are refused before the model runs, a filed
/// turn stores a file reference while the model gets the bytes, and deleting the conversation
/// sweeps the filed blobs.
/// </summary>
public sealed class AgUiAttachmentTests
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

    private static Task<ResponsesHost> StartAsync(IChatClient reply, FakeBlobStoreAdapter blobs)
    {
        return ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => options.UseBlobStores(blobs),
            map: app => app.MapAgUi());
    }

    private static Task<ResponsesHost> StartAsync(IChatClient reply)
    {
        return ResponsesHost.StartAsync(
            Yaml,
            reply,
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

    [Fact]
    public void SizeCheck_Counts_DecodedBytes()
    {
        ChatMessage message = new(ChatRole.User, [new DataContent(new byte[(int)AgUiInput.MaxMessageChars + 1], "image/png")]);

        Assert.Equal(AgUiInput.TooLargeCode, AgUiInput.CheckSize(message)?.Code);
    }

    [Fact(Timeout = 60_000)]
    public async Task ImageAlone_RunsATurn()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-image-alone");

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", ImagePart(PngBase64))]));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.NotNull(reply.LastRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task DisallowedType_Refused_BeforeTheModel()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-bad-type");

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(Convert.ToBase64String([1, 2, 3]), "text/plain"))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_type_not_allowed", only.GetProperty("code").GetString());
        Assert.Null(reply.LastRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task OversizedPart_Refused_BeforeTheModel()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-too-large");
        byte[] big = new byte[(5 * 1024 * 1024) + 1];

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(Convert.ToBase64String(big)))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_too_large", only.GetProperty("code").GetString());
        Assert.Null(reply.LastRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task TooManyParts_Refused_BeforeTheModel()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-too-many");

        using HttpResponseMessage response = await PostAsync(
            host.Client,
            Run(thread, [UserParts("u1", ImagePart(PngBase64), ImagePart(PngBase64), ImagePart(PngBase64), ImagePart(PngBase64), ImagePart(PngBase64))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_too_many", only.GetProperty("code").GetString());
        Assert.Null(reply.LastRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task UrlSource_Refused_BeforeTheModel()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-url");

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), UrlImagePart("https://attacker.test/a.png"))]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_url_not_allowed", only.GetProperty("code").GetString());
        Assert.Null(reply.LastRequest);
    }

    [Fact(Timeout = 60_000)]
    public async Task FiledTurn_StoresFileContent_AndTheModelGetsDataContent()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-filed");

        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))]));
        List<JsonElement> events = await ReadEventsAsync(response);

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, thread, 2);
        FileContent file = Assert.IsType<FileContent>(rows[0].Content.Contents.OfType<FileContent>().Single());
        Assert.True(InboundFiles.Is(file));
        DataContent model = Assert.IsType<DataContent>(reply.LastRequest!.SelectMany(m => m.Contents).OfType<DataContent>().First());
        Assert.Equal(Png, model.Data.ToArray());
    }

    [Fact(Timeout = 60_000)]
    public async Task ResolveRefusals_AreByteIdentical()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-resolve");
        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        List<ConversationMessage> before = await StoredRows.SettleAsync(host.Services, thread, 2);
        int calls = reply.Calls;
        string beforeJson = JsonSerializer.Serialize(before.Select(r => r.Content), TranscriptJson.Options);

        JsonObject forged = new()
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "url", ["value"] = $"https://other.test/agui/threads/other/files/in-evil", ["mimeType"] = "image/png" },
        };
        using HttpResponseMessage response = await PostAsync(host.Client, Run(thread, [UserParts("u2", TextPart("again"), forged)]));
        JsonElement only = Assert.Single(await ReadEventsAsync(response));

        Assert.Equal("RUN_ERROR", only.GetProperty("type").GetString());
        Assert.Equal("attachment_not_owned", only.GetProperty("code").GetString());
        Assert.Equal(calls, reply.Calls);
        List<ConversationMessage> after = await StoredRows.SettleAsync(host.Services, thread, 2);
        Assert.Equal(beforeJson, JsonSerializer.Serialize(after.Select(r => r.Content), TranscriptJson.Options));
    }

    [Fact(Timeout = 60_000)]
    public async Task Delete_SweepsFiledBlobs()
    {
        FakeBlobStoreAdapter blobs = new();
        using FragmentingChatClient reply = new("seen it");
        await using ResponsesHost host = await StartAsync(reply, blobs);
        string thread = await OpenThreadAsync(host, "t-sweep");
        using (HttpResponseMessage first = await PostAsync(host.Client, Run(thread, [UserParts("u1", TextPart("look"), ImagePart(PngBase64))])))
        {
            _ = await ReadEventsAsync(first);
        }

        _ = await StoredRows.SettleAsync(host.Services, thread, 2);
        Assert.NotEmpty(blobs.Store.Blobs);

        await host.Services.GetRequiredService<IConversations>().DeleteAsync(thread, Ct);

        Assert.Empty(blobs.Store.Blobs);
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The thread routes (file door, link, turns) name no entry. On a host with several entries, its
/// <c>BeforeEntry</c> hook picks the entry and the ownership gate runs as that entry, so the conversation's
/// owner opens them and anyone else sees nothing.
/// </summary>
public sealed class AgUiThreadOwnerTests
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
          other:
            agent: greeter
        """;

    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private const string UserHeader = "x-user";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string user, string? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add(UserHeader, user);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
        }

        return await client.SendAsync(request, Ct);
    }

    private static string UploadRun(string thread)
    {
        JsonObject image = new()
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "data", ["value"] = PngBase64, ["mimeType"] = "image/png" },
        };
        return new JsonObject
        {
            ["threadId"] = thread,
            ["runId"] = "r1",
            ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = "u1",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "look" }, image),
            }),
            ["tools"] = new JsonArray(),
            ["context"] = new JsonArray(),
            ["forwardedProps"] = new JsonObject(),
        }.ToJsonString();
    }

    private static async Task<(ResponsesHost Host, OwnerOnly Gate, string Reference)> StartWithOwnedUploadAsync(FragmentingChatClient reply, string thread)
    {
        OwnerOnly gate = new("alice");
        ResponsesHost host = await ResponsesHost.StartAsync(
            Yaml,
            reply,
            options => _ = options.UseBlobStores(new FakeBlobStoreAdapter()).UseHooks(gate),
            map: app => app.MapAgUi());
        _ = await host.Services.GetRequiredService<IConversations>().CreateAsync(thread, Ct);
        using (HttpResponseMessage run = await SendAsync(host.Client, HttpMethod.Post, "/agui/main", "alice", UploadRun(thread)))
        {
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            _ = await run.Content.ReadAsStringAsync(Ct);
        }

        _ = await StoredRows.SettleAsync(host.Services, thread, 2);
        using HttpResponseMessage turns = await SendAsync(host.Client, HttpMethod.Get, $"/agui/threads/{thread}/turns?limit=1", "alice");
        Assert.Equal(HttpStatusCode.OK, turns.StatusCode);
        JsonNode page = JsonNode.Parse(await turns.Content.ReadAsStringAsync(Ct))!;
        JsonNode user = page["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "user")!;
        string reference = new Uri(user["attachments"]![0]!["content"]![0]!["image"]!.GetValue<string>(), UriKind.Absolute).AbsolutePath;
        return (host, gate, reference);
    }

    [Fact(Timeout = 60_000)]
    public async Task Owner_OpensDoorLinkAndTurns_GatedAsTheHookPickedEntry()
    {
        using FragmentingChatClient reply = new("seen it");
        (ResponsesHost host, OwnerOnly gate, string reference) = await StartWithOwnedUploadAsync(reply, "t-owner");
        await using ResponsesHost owned = host;
        gate.Entries.Clear();

        using HttpClientHandler noFollow = new() { AllowAutoRedirect = false };
        using HttpClient direct = new(noFollow) { BaseAddress = host.Client.BaseAddress };
        using HttpResponseMessage door = await SendAsync(direct, HttpMethod.Get, reference, "alice");
        using HttpResponseMessage link = await SendAsync(host.Client, HttpMethod.Get, reference + "/link", "alice");
        using HttpResponseMessage turns = await SendAsync(host.Client, HttpMethod.Get, "/agui/threads/t-owner/turns?limit=1", "alice");

        Assert.Equal(HttpStatusCode.Found, door.StatusCode);
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        Assert.Equal(HttpStatusCode.OK, turns.StatusCode);
        Assert.Equal(["main", "main", "main"], gate.Entries);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnotherUser_IsRefusedDoorLinkAndTurns()
    {
        using FragmentingChatClient reply = new("seen it");
        (ResponsesHost host, _, string reference) = await StartWithOwnedUploadAsync(reply, "t-not-owner");
        await using ResponsesHost owned = host;

        using HttpResponseMessage door = await SendAsync(host.Client, HttpMethod.Get, reference, "bob");
        using HttpResponseMessage link = await SendAsync(host.Client, HttpMethod.Get, reference + "/link", "bob");
        using HttpResponseMessage turns = await SendAsync(host.Client, HttpMethod.Get, "/agui/threads/t-not-owner/turns", "bob");

        Assert.Equal(HttpStatusCode.NotFound, door.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, turns.StatusCode);
    }

    /// <summary>Picks the entry as a host would from its user, lets one user touch conversations, and notes the entry every gate ran as.</summary>
    private sealed class OwnerOnly(string owner) : AgentHook
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public override ValueTask BeforeEntryAsync(EntryGate gate, CancellationToken cancellationToken)
        {
            gate.Choose("main");
            return default;
        }

        public override ValueTask BeforeConversationAsync(ConversationGate gate, CancellationToken cancellationToken)
        {
            Entries.Enqueue(gate.Entry);
            if (!gate.Headers.TryGetValue(UserHeader, out string? user) || user != owner)
            {
                gate.Refuse();
            }

            return default;
        }
    }
}

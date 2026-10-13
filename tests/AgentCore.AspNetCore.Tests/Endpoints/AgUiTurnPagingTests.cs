using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// <c>GET /agui/threads/{threadId}/turns</c> pages: the newest <c>limit</c> turns before the <c>before</c> cursor,
/// a <c>nextCursor</c> for the next older page, and a 400 for a cursor the server never wrote.
/// </summary>
public sealed class AgUiTurnPagingTests
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ResponsesHost> StartWithTurnsAsync(IChatClient reply, string thread, int turns, Action<WebApplication>? map = null)
    {
        ResponsesHost host = await ResponsesHost.StartAsync(Yaml, reply, map: map ?? (app => app.MapAgUi()));
        _ = await host.Services.GetRequiredService<IConversations>().CreateAsync(thread, Ct);
        IConversationStore store = host.Services.GetRequiredService<IConversationStore>();
        for (int k = 0; k < turns; k++)
        {
            _ = await store.AppendAsync(
                thread,
                [
                    new ConversationMessageDraft(k, new ChatMessage(ChatRole.User, $"q{k}"), $"u{k}"),
                    new ConversationMessageDraft(k, new ChatMessage(ChatRole.Assistant, $"a{k}"), $"a{k}"),
                ],
                null,
                Ct);
        }

        return host;
    }

    private static async Task<JsonNode> PageAsync(HttpClient client, string thread, string query)
    {
        using HttpResponseMessage response = await client.GetAsync($"/agui/threads/{thread}/turns{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private static string[] TurnIds(JsonNode page)
        => [.. page["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "assistant").Select(m => m!["id"]!.GetValue<string>())];

    [Fact(Timeout = 60_000)]
    public async Task Pages_WalkBackThroughNextCursor_UntilNoneIsLeft()
    {
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartWithTurnsAsync(reply, "t-pages", 5);

        JsonNode first = await PageAsync(host.Client, "t-pages", "?limit=2");
        Assert.Equal([AgUiTurnIds.TurnMessageId(3), AgUiTurnIds.TurnMessageId(4)], TurnIds(first));
        Assert.Equal("3", first["nextCursor"]!.GetValue<string>());

        JsonNode second = await PageAsync(host.Client, "t-pages", "?limit=2&before=3");
        Assert.Equal([AgUiTurnIds.TurnMessageId(1), AgUiTurnIds.TurnMessageId(2)], TurnIds(second));
        Assert.Equal("1", second["nextCursor"]!.GetValue<string>());

        JsonNode last = await PageAsync(host.Client, "t-pages", "?limit=2&before=1");
        Assert.Equal([AgUiTurnIds.TurnMessageId(0)], TurnIds(last));
        Assert.Null(last["nextCursor"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task NoLimit_AnswersThirtyTurns()
    {
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartWithTurnsAsync(reply, "t-default", 31);

        JsonNode page = await PageAsync(host.Client, "t-default", string.Empty);

        Assert.Equal(30, TurnIds(page).Length);
        Assert.Equal("1", page["nextCursor"]!.GetValue<string>());
    }

    [Theory(Timeout = 60_000)]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=1000", 100)]
    public async Task Limit_IsClampedToOneThroughHundred(string query, int expected)
    {
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartWithTurnsAsync(reply, "t-clamp", 101);

        JsonNode page = await PageAsync(host.Client, "t-clamp", query);

        Assert.Equal(expected, TurnIds(page).Length);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+3")]
    public async Task UnreadableCursor_Is400_SaysToSendNextCursor(string before)
    {
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartWithTurnsAsync(reply, "t-bad-cursor", 1);

        using HttpResponseMessage response = await host.Client.GetAsync($"/agui/threads/t-bad-cursor/turns?before={Uri.EscapeDataString(before)}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nextCursor", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task MapAgUiTwice_ThreadRoutesStillAnswer()
    {
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartWithTurnsAsync(reply, "t-twice", 1, app =>
        {
            _ = app.MapAgUi();
            _ = app.MapAgUi("/custom/{entry}");
        });

        JsonNode page = await PageAsync(host.Client, "t-twice", string.Empty);

        Assert.Equal([AgUiTurnIds.TurnMessageId(0)], TurnIds(page));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ApprovalPairStraddlingAPageEdge_ShowsTheCallOnce_WithItsResult(int askingTurn)
    {
        using FragmentingChatClient reply = new("unused");
        await using ResponsesHost host = await StartWithTurnsAsync(reply, "t-straddle", 0);
        IConversationStore store = host.Services.GetRequiredService<IConversationStore>();
        FunctionCallContent call = new("c1", "send_email", new Dictionary<string, object?> { ["to"] = "a@b.c" });
        for (int k = 0; k < 4; k++)
        {
            List<ConversationMessageDraft> rows = [new(k, new ChatMessage(ChatRole.User, $"q{k}"), $"u{k}")];
            if (k == askingTurn)
            {
                rows.Add(new(k, new ChatMessage(ChatRole.Assistant, [new ToolApprovalRequestContent("req1", call)]), $"a{k}"));
            }
            else if (k == askingTurn + 1)
            {
                rows.Add(new(k, new ChatMessage(ChatRole.Assistant, [call]), $"c{k}"));
                rows.Add(new(k, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "sent")]), $"t{k}"));
                rows.Add(new(k, new ChatMessage(ChatRole.Assistant, "done"), $"a{k}"));
            }
            else
            {
                rows.Add(new(k, new ChatMessage(ChatRole.Assistant, $"a{k}"), $"a{k}"));
            }

            _ = await store.AppendAsync("t-straddle", rows, null, Ct);
        }

        List<JsonNode> calls = [];
        string query = "?limit=1";
        while (true)
        {
            JsonNode page = await PageAsync(host.Client, "t-straddle", query);
            calls.AddRange(page["messages"]!.AsArray()
                .SelectMany(m => m!["content"] is JsonArray parts ? parts : [])
                .Where(p => p!["type"]?.GetValue<string>() == "tool-call")!);
            if (page["nextCursor"]?.GetValue<string>() is not { } cursor)
            {
                break;
            }

            query = $"?limit=1&before={cursor}";
        }

        JsonNode only = Assert.Single(calls);
        Assert.Equal("c1", only["toolCallId"]!.GetValue<string>());
        Assert.Equal("sent", only["result"]!.GetValue<string>());
    }
}

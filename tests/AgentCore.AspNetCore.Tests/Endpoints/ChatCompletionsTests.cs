using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The text path: one request runs one turn, and the session carries the call between requests.
/// </summary>
/// <remarks>
/// Every test here runs offline against a fake model. There is no network call and no API key
/// anywhere in this file.
/// </remarks>
public sealed class ChatCompletionsTests
{
    private const string PolicyYaml =
        """
        apiVersion: agentcore/v1
        name: host-turn-loop
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        guards:
          saidGoodbye: { var: callerSaidGoodbye }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller" }
            - { id: closer,  instructions: "close the call" }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter, to: [ { stage: close, when: saidGoodbye } ] }
            - { id: close,    agent: closer,  terminal: true }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
            - { kind: openai, model: gpt-5.4-nano, as: fill }
        """;

    private const string TwoStagesYaml =
        """
        apiVersion: agentcore/v1
        name: two-stages
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "I am the greeter" }
            - { id: closer,  instructions: "I am the closer" }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter, to: [ { stage: close } ] }
            - { id: close,    agent: closer,  to: [ { stage: greeting } ] }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string StayingNull = """{ "callerSaidGoodbye": null }""";
    private const string SaidGoodbye = """{ "callerSaidGoodbye": true }""";

    // -------------------------------------------------------------------------------------------
    // One turn, whole.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATurn_AnswersTheOpenAiShapeAndNamesTheSession()
    {
        using SequencedChatClient reply = new("hello there");
        using SequencedChatClient fill = new(StayingNull);
        await using var host = await ChatCompletionsHost.StartAsync(PolicyYaml, reply, fill);

        using var response = await host.PostAsync("hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ChatCompletionsHost.ReadJsonAsync(response);
        Assert.Equal("chat.completion", body["object"]!.GetValue<string>());
        Assert.StartsWith("chatcmpl-", body["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("assistant", body["choices"]![0]!["message"]!["role"]!.GetValue<string>());
        Assert.Equal("hello there", body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("stop", body["choices"]![0]!["finish_reason"]!.GetValue<string>());

        // The answer names the session, in the header and in the body, so the next turn finds it.
        var session = Assert.Single(
            response.Headers.GetValues(ChatCompletionsEndpointRouteBuilderExtensions.SessionHeaderName));
        Assert.Equal(session, body["agentcore"]!["session"]!.GetValue<string>());
        Assert.Equal("greeting", body["agentcore"]!["stage_after"]!.GetValue<string>());
        Assert.False(body["agentcore"]!["is_terminal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ATurn_RunsTheReplyModelAndTheExtractorModelOnce()
    {
        using SequencedChatClient reply = new("hello there");
        using SequencedChatClient fill = new(StayingNull);
        await using var host = await ChatCompletionsHost.StartAsync(PolicyYaml, reply, fill);

        using var response = await host.PostAsync("hi");
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, reply.Calls);
        Assert.Equal(1, fill.Calls);
    }

    // -------------------------------------------------------------------------------------------
    // One turn, streamed.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AStreamedTurn_WritesOneEventForEachUpdateAndClosesWithDone()
    {
        using SequencedChatClient reply = new("hello there caller");
        using SequencedChatClient fill = new(StayingNull);
        await using var host = await ChatCompletionsHost.StartAsync(PolicyYaml, reply, fill);

        using var response = await host.PostStreamingAsync("hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = await ChatCompletionsHost.ReadEventsAsync(response);
        Assert.Equal("[DONE]", events[^1]);

        // The reply arrives in pieces, and the pieces rebuild the whole reply.
        var pieces = ChatCompletionsHost.ContentDeltas(events);
        Assert.True(pieces.Count > 1);
        Assert.Equal("hello there caller", string.Concat(pieces));
    }

    [Fact]
    public async Task AStreamedTurn_ReportsWhatTheTurnDidOnTheLastChunk()
    {
        using SequencedChatClient reply = new("hello there");
        using SequencedChatClient fill = new(SaidGoodbye);
        await using var host = await ChatCompletionsHost.StartAsync(PolicyYaml, reply, fill);

        using var response = await host.PostStreamingAsync("goodbye");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        // The turn is finished once the enumeration is, so the last chunk is the first place that
        // knows the stage the machine moved to.
        var info = ChatCompletionsHost.LastTurnInfo(events)!;
        Assert.Equal("greeting", info["stage_before"]!.GetValue<string>());
        Assert.Equal("close", info["stage_after"]!.GetValue<string>());
        Assert.True(info["is_terminal"]!.GetValue<bool>());
    }

    // -------------------------------------------------------------------------------------------
    // One session, two requests.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task TwoRequestsOnOneSession_CarryTheStageAndTheTranscript()
    {
        using SequencedChatClient reply = new("first reply", "second reply");
        await using var host = await ChatCompletionsHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("one");
        var firstBody = await ChatCompletionsHost.ReadJsonAsync(first);
        var session = firstBody["agentcore"]!["session"]!.GetValue<string>();

        Assert.Equal("greeting", firstBody["agentcore"]!["stage_before"]!.GetValue<string>());
        Assert.Equal("close", firstBody["agentcore"]!["stage_after"]!.GetValue<string>());
        Assert.Equal(0, firstBody["agentcore"]!["turn_index"]!.GetValue<int>());

        using var second = await host.PostAsync("two", session);
        var secondBody = await ChatCompletionsHost.ReadJsonAsync(second);

        // The second request found the same session, so the machine carried the stage across.
        Assert.Equal(session, secondBody["agentcore"]!["session"]!.GetValue<string>());
        Assert.Equal("close", secondBody["agentcore"]!["stage_before"]!.GetValue<string>());
        Assert.Equal("greeting", secondBody["agentcore"]!["stage_after"]!.GetValue<string>());
        Assert.Equal(1, secondBody["agentcore"]!["turn_index"]!.GetValue<int>());
        Assert.Equal("second reply", secondBody["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoRequestsWithNoSessionHeader_StartTwoCalls()
    {
        using SequencedChatClient reply = new("first reply", "second reply");
        await using var host = await ChatCompletionsHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("one");
        using var second = await host.PostAsync("one");

        var firstId = (await ChatCompletionsHost.ReadJsonAsync(first))["agentcore"]!["session"]!.GetValue<string>();
        var secondId = (await ChatCompletionsHost.ReadJsonAsync(second))["agentcore"]!["session"]!.GetValue<string>();

        Assert.NotEqual(firstId, secondId);
    }

    // -------------------------------------------------------------------------------------------
    // What the endpoint refuses.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AnUnknownSession_Is404AndStartsNothing()
    {
        using SequencedChatClient reply = new("hello there");
        await using var host = await ChatCompletionsHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.PostAsync("hi", "call-that-never-existed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ChatCompletionsHost.ReadJsonAsync(response);
        Assert.Equal("session_not_found", body["error"]!["code"]!.GetValue<string>());
        Assert.Contains("call-that-never-existed", body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // A silently new call would lose the stage the caller was in, so the model never ran.
        Assert.Equal(0, reply.Calls);
    }

    [Fact]
    public async Task ARequestWithNoUserMessage_Is400()
    {
        using SequencedChatClient reply = new("hello there");
        await using var host = await ChatCompletionsHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.Client.PostAsJsonAsync(
            ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern,
            new { model = "two-stages", messages = Array.Empty<object>() },
            JsonSerializerOptions.Web,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ChatCompletionsHost.ReadJsonAsync(response);
        Assert.Equal("no_user_message", body["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATurnOnAFinishedCall_Is409()
    {
        using SequencedChatClient reply = new("hello there");
        using SequencedChatClient fill = new(SaidGoodbye);
        await using var host = await ChatCompletionsHost.StartAsync(PolicyYaml, reply, fill);

        using var first = await host.PostAsync("goodbye");
        var body = await ChatCompletionsHost.ReadJsonAsync(first);
        Assert.True(body["agentcore"]!["is_terminal"]!.GetValue<bool>());

        var session = body["agentcore"]!["session"]!.GetValue<string>();
        using var second = await host.PostAsync("are you still there?", session);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var error = await ChatCompletionsHost.ReadJsonAsync(second);
        Assert.Equal("turn_refused", error["error"]!["code"]!.GetValue<string>());
    }
}

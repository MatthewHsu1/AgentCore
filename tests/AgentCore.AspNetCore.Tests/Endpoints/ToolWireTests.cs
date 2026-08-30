using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The wire a tool call travels: one SSE field that names the call, and a second that answers it.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint owns the tool loop, so the caller never runs a tool and must never be asked to. The
/// OpenAI <c>tool_calls</c> field means exactly that — "you run this and send me the result" — and
/// writing it here would tell an ordinary OpenAI client to do work that already happened. So the
/// facts of the loop ride their own <c>agentcore_tool</c> field, beside <c>agentcore_data</c>, where
/// a client that does not know the field ignores it and the browser that does can draw the loop.
/// </para>
/// <para>
/// Every test here runs offline against a fake model, on a real socket.
/// </para>
/// </remarks>
public sealed class ToolWireTests
{
    private const string ToolYaml =
        """
        apiVersion: agentcore/v1
        name: tool-wire
        tools:
          - id: look_it_up
            kind: binding
            binds: LookItUp
            description: Look something up for the caller.
            parameters:
              type: object
              properties: { what: { type: string } }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller", tools: [ look_it_up ] }
            - { id: closer,  instructions: "close the call",   tools: [ look_it_up ] }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter, to: [ { stage: close } ] }
            - { id: close,    agent: closer,  to: [ { stage: greeting } ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    [Fact]
    public async Task AToolCall_ReachesTheBrowserOnItsOwnFieldWithTheNameAndTheArguments()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>("42 rows")));

        using var response = await host.PostStreamingAsync("look up revenue");
        var tools = await ReadToolEventsAsync(response);

        var call = Assert.Single(tools, tool => tool.GetProperty("phase").GetString() == "call");

        Assert.Equal("call_1", call.GetProperty("call_id").GetString());
        Assert.Equal("look_it_up", call.GetProperty("name").GetString());
        Assert.Equal("a card", call.GetProperty("arguments").GetProperty("what").GetString());
    }

    [Fact]
    public async Task AToolResult_ReachesTheBrowserOnTheSameFieldAndNamesTheCallItAnswers()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>("42 rows")));

        using var response = await host.PostStreamingAsync("look up revenue");
        var tools = await ReadToolEventsAsync(response);

        var result = Assert.Single(tools, tool => tool.GetProperty("phase").GetString() == "result");

        // The id is what pairs the two halves in the browser, so the result carries the call's id
        // and not one of its own.
        Assert.Equal("call_1", result.GetProperty("call_id").GetString());
        Assert.Equal("look_it_up", result.GetProperty("name").GetString());
        Assert.False(result.GetProperty("failed").GetBoolean());
        Assert.Contains("42 rows", result.GetProperty("result").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolThatThrows_ReachesTheBrowserAsAFailedResultRatherThanAsSilence()
    {
        // The loop turns a fault the model can answer into an error result and carries on. The
        // caller's screen must say so, or a tool that failed looks the same as one that worked.
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => throw new InvalidOperationException("the table is gone")));

        using var response = await host.PostStreamingAsync("look up revenue");
        var tools = await ReadToolEventsAsync(response);

        var result = Assert.Single(tools, tool => tool.GetProperty("phase").GetString() == "result");

        Assert.True(result.GetProperty("failed").GetBoolean());
        Assert.Contains(
            "the table is gone",
            result.GetProperty("result").GetProperty(ToolErrorResult.MessageProperty).GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolThatAnswersWithAnErrorObjectAsText_IsStillMarkedFailed()
    {
        // Section 8.7 lets a tool answer with the error object rather than throw, and an MCP server
        // or a host delegate hands that object over as text. Reading the result as a node only when
        // it already is one would call this failure a success.
        var failure = ToolErrorResult.Create("look_it_up", "the table is gone").ToJsonString();

        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>(failure)));

        using var response = await host.PostStreamingAsync("look up revenue");
        var tools = await ReadToolEventsAsync(response);

        var result = Assert.Single(tools, tool => tool.GetProperty("phase").GetString() == "result");

        Assert.True(result.GetProperty("failed").GetBoolean());
        Assert.Equal(
            "the table is gone",
            result.GetProperty("result").GetProperty(ToolErrorResult.MessageProperty).GetString());
    }

    [Fact]
    public async Task AToolThatAnswersWithAnObject_ReachesTheBrowserAsItsFieldsAndNotItsTypeName()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>(new Lookup(7, "cards"))));

        using var response = await host.PostStreamingAsync("look up revenue");
        var tools = await ReadToolEventsAsync(response);

        var result = Assert.Single(tools, tool => tool.GetProperty("phase").GetString() == "result");
        var answer = result.GetProperty("result");

        Assert.False(result.GetProperty("failed").GetBoolean());
        Assert.Equal(7, answer.GetProperty("rows").GetInt32());
        Assert.Equal("cards", answer.GetProperty("of").GetString());
    }

    [Fact]
    public async Task AnAnswerThatIsJson_RidesTheWireAsJsonRatherThanAsEscapedText()
    {
        // Writing the answer as text escapes every quote inside it as \u0022, and leaves the browser
        // a string to print raw where it lays out an object. The answer travels as itself instead.
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>("""{ "entities": [ "it's" ] }""")));

        using var response = await host.PostStreamingAsync("look up revenue");

        // The quotes inside the answer were the whole complaint: an answer written as text has every
        // one of them escaped. The apostrophe stays escaped, because the default JSON writer escapes
        // it whatever the shape it sits in, and the browser turns it back on parse.
        var events = await ChatCompletionsHost.ReadEventsAsync(response);
        Assert.DoesNotContain("\\u0022", string.Join("\n", events), StringComparison.Ordinal);

        var result = Assert.Single(
            ToolEventsOf(events),
            tool => tool.GetProperty("phase").GetString() == "result");

        var entities = result.GetProperty("result").GetProperty("entities");
        Assert.Equal("it's", Assert.Single(entities.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ATurnThatCallsNoTool_WritesNoToolFieldAtAll()
    {
        // The tool is declared and bound, exactly as in every other test here. The model simply
        // never reaches for it, which is what this test is about.
        using FragmentingChatClient reply = new("hello there");
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            reply,
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>("42 rows")));

        using var response = await host.PostStreamingAsync("hi");

        Assert.Empty(await ReadToolEventsAsync(response));
    }

    [Fact]
    public async Task AToolCall_StaysOutOfTheWordsTheCallerIsTold()
    {
        // The voice path speaks this same turn, and a tool name read aloud is not an answer.
        await using var host = await ChatCompletionsHost.StartAsync(
            ToolYaml,
            new ToolCallingChatClient(),
            configure: options => options.Bind(
                "LookItUp", (_, _) => ValueTask.FromResult<object?>("42 rows")));

        using var response = await host.PostStreamingAsync("look up revenue");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        var spoken = string.Concat(events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .SelectMany(chunk => chunk.GetProperty("choices").EnumerateArray())
            .Select(choice => choice.GetProperty("delta").TryGetProperty("content", out var content)
                ? content.GetString() ?? string.Empty
                : string.Empty));

        Assert.Equal("here it is.", spoken);
    }

    /// <summary>What a tool answers with when it answers with an object of its own.</summary>
    private sealed record Lookup(int Rows, string Of);

    /// <summary>Reads every <c>agentcore_tool</c> payload the stream carried, in arrival order.</summary>
    private static async Task<List<JsonElement>> ReadToolEventsAsync(HttpResponseMessage response)
        => ToolEventsOf(await ChatCompletionsHost.ReadEventsAsync(response));

    /// <summary>Picks the <c>agentcore_tool</c> payloads out of events already read.</summary>
    private static List<JsonElement> ToolEventsOf(IEnumerable<string> events)
    {
        return events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_tool", out var tool)
                            && tool.ValueKind != JsonValueKind.Null)
            .Select(chunk => chunk.GetProperty("agentcore_tool"))
            .ToList();
    }

    /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
    private sealed class ToolCallingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyCalled = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyCalled && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call_1",
                        tool.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "a card" })]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "here it is.");
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}

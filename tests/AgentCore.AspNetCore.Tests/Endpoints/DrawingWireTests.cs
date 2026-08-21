using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The whole wire a drawing travels: a tool, the call's screen, and one extra SSE field.
/// </summary>
/// <remarks>
/// <para>
/// The drawing cannot ride the tool call itself. This endpoint owns the tool loop and the reply
/// carries only text — there is no <c>tool_calls</c> field on the answer, and the loop is shared
/// with the voice path, which has no browser. So a tool that draws publishes to the call's screen
/// and the stream writes it as its own chunk.
/// </para>
/// <para>
/// Every test here runs offline against a fake model, on a real socket.
/// </para>
/// </remarks>
public sealed class DrawingWireTests
{
    private const string DrawingYaml =
        """
        apiVersion: agentcore/v1
        name: drawing-wire
        tools:
          - id: draw_it
            kind: binding
            binds: DrawIt
            description: Draw something for the caller.
            parameters:
              type: object
              properties: { what: { type: string } }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller", tools: [ draw_it ] }
            - { id: closer,  instructions: "close the call",   tools: [ draw_it ] }
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
    public async Task AToolThatDraws_ReachesTheBrowserOnItsOwnFieldAndNotInTheReply()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            DrawingYaml,
            new DrawingChatClient(),
            configure: options => options.Bind("DrawIt", (_, _) =>
            {
                // Exactly what DrawingTool does: find the call's screen and push to it.
                CallRenderScope.Current?.Publish("generative-ui", new { title = "Q3 revenue" });
                return ValueTask.FromResult<object?>("drew a Card; buttons: none");
            }));

        using var response = await host.PostStreamingAsync("show me revenue");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        var drawings = events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_data", out var data) && data.ValueKind != JsonValueKind.Null)
            .ToList();

        var drawing = Assert.Single(drawings);
        var payload = drawing.GetProperty("agentcore_data");

        Assert.Equal("generative-ui", payload.GetProperty("name").GetString());
        Assert.Equal("Q3 revenue", payload.GetProperty("data").GetProperty("title").GetString());

        // And it is not in what the caller is told. The spoken reply is what the transcript keeps.
        var spoken = string.Concat(events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .SelectMany(chunk => chunk.GetProperty("choices").EnumerateArray())
            .Select(choice => choice.GetProperty("delta").TryGetProperty("content", out var content)
                ? content.GetString() ?? string.Empty
                : string.Empty));

        Assert.DoesNotContain("Q3 revenue", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAStream_DropsTheDrawingRatherThanKeepingItForALaterTurn()
    {
        // There is no chunk to carry it, and a drawing that surfaced part-way through the next turn
        // would be worse than one that never arrived.
        await using var host = await ChatCompletionsHost.StartAsync(
            DrawingYaml,
            new DrawingChatClient(),
            configure: options => options.Bind("DrawIt", (_, _) =>
            {
                CallRenderScope.Current?.Publish("generative-ui", new { title = "dropped" });
                return ValueTask.FromResult<object?>("drew a Card; buttons: none");
            }));

        using var first = await host.PostAsync("show me revenue");
        var session = first.Headers.GetValues("X-AgentCore-Session").Single();

        using var second = await host.PostStreamingAsync("and again", session);
        var events = await ChatCompletionsHost.ReadEventsAsync(second);

        Assert.DoesNotContain(events, text => text.Contains("\"dropped\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnswerThatIsNotAStream_ShowsTheToolNoScreenRatherThanTakingWhatItWillDrop()
    {
        // The whole-reply shape has no chunk to carry a drawing. Binding a screen it will then throw
        // away lets the tool report a picture to the model that the caller never sees.
        var hadScreen = true;

        await using var host = await ChatCompletionsHost.StartAsync(
            DrawingYaml,
            new DrawingChatClient(),
            configure: options => options.Bind("DrawIt", (_, _) =>
            {
                hadScreen = CallRenderScope.Current is not null;
                return ValueTask.FromResult<object?>("drew a Card; buttons: none");
            }));

        using var response = await host.PostAsync("show me revenue");

        Assert.False(hadScreen);
    }

    /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
    private sealed class DrawingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyDrew = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyDrew && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
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

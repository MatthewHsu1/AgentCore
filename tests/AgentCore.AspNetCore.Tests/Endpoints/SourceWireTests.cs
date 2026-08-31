using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Sources;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The whole wire a source travels: a producer, the call's sources, and one extra SSE field.
/// </summary>
/// <remarks>
/// Retrieval is the first producer of a source and this test deliberately does not use it. The
/// channel belongs to anything that can name what it read, so what is proved here is that a plain
/// bound tool can cite one and have it reach the browser.
/// </remarks>
public sealed class SourceWireTests
{
    private const string SourceYaml =
        """
        apiVersion: agentcore/v1
        name: source-wire
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
    public async Task AToolThatCites_ReachesTheBrowserOnItsOwnFieldAndNotInTheReply()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            SourceYaml,
            new SourceCitingChatClient(),
            configure: options => options.Bind("LookItUp", (_, _) =>
            {
                CallSourceScope.Current?.Publish(new SourceReference
                {
                    SourceId = "card-42",
                    Kind = SourceKind.Document,
                    Title = "Spirit CT900 owner's manual",
                    Origin = "knowledge",
                    Locator = "p.27",
                });

                return ValueTask.FromResult<object?>("E03 is an overcurrent trip.");
            }));

        using var response = await host.PostStreamingAsync("what is E03");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        var cited = events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_source", out var source)
                && source.ValueKind != JsonValueKind.Null)
            .ToList();

        var chunk = Assert.Single(cited).GetProperty("agentcore_source");

        Assert.Equal("card-42", chunk.GetProperty("id").GetString());
        Assert.Equal("document", chunk.GetProperty("source_type").GetString());
        Assert.Equal("Spirit CT900 owner's manual", chunk.GetProperty("title").GetString());
        Assert.Equal("p.27", chunk.GetProperty("locator").GetString());
        Assert.Equal("knowledge", chunk.GetProperty("origin").GetString());
        Assert.False(string.IsNullOrEmpty(chunk.GetProperty("call_id").GetString()));

        // And it is not in what the caller is told. The spoken reply is what the transcript keeps.
        var spoken = string.Concat(events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Select(chunk => chunk.GetProperty("choices")[0]
                .GetProperty("delta")
                .TryGetProperty("content", out var content) ? content.GetString() ?? "" : ""));

        Assert.DoesNotContain("card-42", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolThatCitesNothing_WritesNoSourceField()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            SourceYaml,
            new SourceCitingChatClient(),
            configure: options => options.Bind("LookItUp", (_, _) =>
                ValueTask.FromResult<object?>("nothing to cite")));

        using var response = await host.PostStreamingAsync("what is E03");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        Assert.DoesNotContain(events, text => text.Contains("agentcore_source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AToolThatCitesUnderTwoParallelCalls_StampsEachSourceWithItsOwnCallId()
    {
        // FunctionInvokingChatClient batches every parallel call's results of one round onto ONE
        // message (and this endpoint turns that message into ONE update), so a fix that reads the
        // call id off "the first FunctionResultContent on the update" rather than off the source
        // itself would silently stamp the second source with the first call's id. This is the
        // regression test for exactly that.
        await using var host = await ChatCompletionsHost.StartAsync(
            SourceYaml,
            new TwoParallelCallsChatClient(),
            configure: options => options.Bind("LookItUp", (arguments, _) =>
            {
                var what = arguments["what"]?.GetValue<string>();
                CallSourceScope.Current?.Publish(new SourceReference
                {
                    SourceId = what == "left" ? "card-left" : "card-right",
                    Kind = SourceKind.Document,
                    Title = what == "left" ? "Left manual" : "Right manual",
                    Origin = "knowledge",
                });

                return ValueTask.FromResult<object?>("looked up " + what);
            }));

        using var response = await host.PostStreamingAsync("what is E03");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        var cited = events
            .Where(text => text != "[DONE]")
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_source", out var source)
                && source.ValueKind != JsonValueKind.Null)
            .Select(chunk => chunk.GetProperty("agentcore_source"))
            .ToList();

        Assert.Equal(2, cited.Count);

        var left = Assert.Single(cited, source => source.GetProperty("id").GetString() == "card-left");
        var right = Assert.Single(cited, source => source.GetProperty("id").GetString() == "card-right");

        Assert.Equal("call_1", left.GetProperty("call_id").GetString());
        Assert.Equal("call_2", right.GetProperty("call_id").GetString());
    }

    /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
    /// <remarks>
    /// Copied from <c>DrawingWireTests.DrawingChatClient</c> — that class is private to its own file,
    /// so this test needs its own copy of the same shape rather than sharing the type.
    /// </remarks>
    private sealed class SourceCitingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyCited = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyCited && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call_1",
                        tool.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "E03" })]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "it is an overcurrent trip.");
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

    /// <summary>Calls the tool it is offered twice in one round — two parallel calls — then answers in words.</summary>
    /// <remarks>
    /// This is the shape a real model routinely produces and the single-call fakes above cannot
    /// exercise: <c>FunctionInvokingChatClient</c> batches both tool results of one round onto ONE
    /// message before this endpoint ever sees it.
    /// </remarks>
    private sealed class TwoParallelCallsChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyCited = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyCited && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "call_1",
                            tool.Name,
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "left" }),
                        new FunctionCallContent(
                            "call_2",
                            tool.Name,
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "right" }),
                    ]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "looked up both.");
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

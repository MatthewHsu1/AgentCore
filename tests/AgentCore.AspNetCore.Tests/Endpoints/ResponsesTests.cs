using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The Responses path: one request runs one turn, and the continuation carries the call.
/// </summary>
/// <remarks>
/// Every test here runs offline against a fake model. The two-stage document names the
/// stage in each reply, so the text proves which stage spoke: there is no subtler
/// assertion about continuity than the second turn answering with the second agent.
/// </remarks>
public sealed class ResponsesTests
{
    private const string TwoStagesYaml =
        """
        apiVersion: agentcore/v1
        name: responses-turn-loop
        guards:
          second: { ">=": [ { var: turnIndex }, 1 ] }
          third: { ">=": [ { var: turnIndex }, 2 ] }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer one" }
            - { id: duo, instructions: "answer two" }
        policy:
          initial: talking
          stages:
            - { id: talking, agent: solo, to: [ { stage: followup, when: second } ] }
            - { id: followup, agent: duo, to: [ { stage: talking, when: third } ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    [Fact]
    public async Task FirstTurn_MintsConversationAndAnswersStageOne()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.PostAsync("""{ "stream": false, "input": "first question" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ResponsesHost.ReadJsonAsync(response);
        Assert.Contains("answer one", body.OutputText(), StringComparison.Ordinal);
        Assert.StartsWith("conv_", body.ContinuationId(), StringComparison.Ordinal);
        Assert.StartsWith("resp_", body["id"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondTurnOnConversation_ContinuesStageAndTranscript()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("""{ "stream": false, "input": "first question" }""");
        var convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();
        var firstRequest = reply.LastRequest;

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await ResponsesHost.ReadJsonAsync(second);
        Assert.Contains("answer two", body.OutputText(), StringComparison.Ordinal);

        var secondRequest = reply.LastRequest;
        Assert.NotNull(secondRequest);
        Assert.Contains(secondRequest, m => m.Text.Contains("first question", StringComparison.Ordinal));
        Assert.Contains(secondRequest, m => m.Text.Contains("answer one", StringComparison.Ordinal));
        Assert.NotSame(firstRequest, secondRequest);
    }

    [Fact]
    public async Task SecondTurnOnResponseChain_ContinuesStage()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("""{ "stream": false, "input": "first question" }""");
        var respId = (await ResponsesHost.ReadJsonAsync(first))["id"]?.GetValue<string>();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "previous_response_id": "{{respId}}", "input": "chained question" }""");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await ResponsesHost.ReadJsonAsync(second);
        Assert.Contains("answer two", body.OutputText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondTurnStreams_StillContinuesStage()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("""{ "stream": false, "input": "first question" }""");
        var convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

        using var second = await host.PostAsync(
            $$"""{ "stream": true, "conversation": "{{convId}}", "input": "second question" }""");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("text/event-stream", second.Content.Headers.ContentType?.MediaType);
        var text = await ResponsesHost.ReadTextAsync(second);
        Assert.Contains("answer two", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownConversation_StartsTheCallUnderThatId()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync(
            """{ "stream": false, "conversation": "thread-7", "input": "first question" }""");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await ResponsesHost.ReadJsonAsync(first);
        Assert.Equal("thread-7", firstBody.ContinuationId());
        Assert.Equal("thread-7", firstBody["metadata"]!["call_id"]!.GetValue<string>());

        using var second = await host.PostAsync(
            """{ "stream": false, "conversation": "thread-7", "input": "second question" }""");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("answer two", (await ResponsesHost.ReadJsonAsync(second)).OutputText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownResponseId_ReturnsNotFound()
    {
        using FragmentingChatClient reply = new("answer one");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.PostAsync(
            """{ "stream": false, "previous_response_id": "resp_doesnotexist", "input": "hello" }""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MalformedBody_ReturnsBadRequest()
    {
        using FragmentingChatClient reply = new("answer one");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.PostAsync("""{ "stream": false, "input": """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingInput_ReturnsBadRequest()
    {
        using FragmentingChatClient reply = new("answer one");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.PostAsync("""{ "stream": false }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task FirstTurn_MetadataCarriesTurnFacts()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var response = await host.PostAsync("""{ "stream": false, "input": "first question" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ResponsesHost.ReadJsonAsync(response);
        var metadata = body["metadata"]!.AsObject();
        Assert.Equal("0", metadata["turn_index"]!.GetValue<string>());
        // The commit advances on the post-increment index, so the first turn already leaves talking.
        Assert.Equal("talking", metadata["stage_before"]!.GetValue<string>());
        Assert.Equal("followup", metadata["stage_after"]!.GetValue<string>());
        Assert.Equal("false", metadata["is_terminal"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(metadata["call_id"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(metadata["message_id"]!.GetValue<string>()));
        Assert.Null(body["metadata"]!["approvals"]);
        Assert.Equal("followup", Assert.Single(response.Headers.GetValues("X-AgentCore-Stage")));
    }

    [Fact]
    public async Task SecondTurn_MetadataAdvancesWithTheStage()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("""{ "stream": false, "input": "first question" }""");
        var convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");

        var metadata = (await ResponsesHost.ReadJsonAsync(second))["metadata"]!.AsObject();
        Assert.Equal("1", metadata["turn_index"]!.GetValue<string>());
        // The second turn speaks in followup and its post-increment index trips the third guard back.
        Assert.Equal("followup", metadata["stage_before"]!.GetValue<string>());
        Assert.Equal("talking", metadata["stage_after"]!.GetValue<string>());
    }

    [Fact]
    public async Task TerminalTurn_SignalsTerminalAndRefusesTheNext()
    {
        using FragmentingChatClient reply = new("answer one");
        await using var host = await ResponsesHost.StartAsync(TerminalYaml, reply);

        using var first = await host.PostAsync("""{ "stream": false, "input": "first question" }""");
        var body = await ResponsesHost.ReadJsonAsync(first);
        Assert.Equal("true", body["metadata"]!["is_terminal"]!.GetValue<string>());
        var convId = body.ContinuationId();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GatedToolCall_AnswersWholeWithTheRequestInMetadata()
    {
        int sent = 0;
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var response = await host.PostAsync("""{ "stream": false, "input": "send it" }""");
        var body = await ResponsesHost.ReadJsonAsync(response);

        Assert.Equal(string.Empty, body.OutputText());
        var approvals = body["metadata"]!["approvals"]!.GetValue<string>();
        Assert.Contains("send_email", approvals, StringComparison.Ordinal);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task ApprovalAnswer_RunsTheToolAndReplies()
    {
        int sent = 0;
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var first = await host.PostAsync("""{ "stream": false, "input": "send it" }""");
        var asked = await ResponsesHost.ReadJsonAsync(first);
        var convId = asked.ContinuationId();
        var requestId = System.Text.Json.JsonDocument.Parse(
            asked["metadata"]!["approvals"]!.GetValue<string>())
            .RootElement[0].GetProperty("request_id").GetString();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": [], "agentcore": { "approval": { "request_id": "{{requestId}}", "approved": true } } }""");
        var replied = await ResponsesHost.ReadJsonAsync(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, sent);
        Assert.Contains("done.", replied.OutputText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovalAnswerForARequestNothingAsked_IsConflict()
    {
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var first = await host.PostAsync("""{ "stream": false, "input": "send it" }""");
        var convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": [], "agentcore": { "approval": { "request_id": "req-nothing-asked", "approved": true } } }""");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ApprovalAnswerWithWords_IsBadRequest()
    {
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var first = await host.PostAsync("""{ "stream": false, "input": "send it" }""");
        var convId = (await ResponsesHost.ReadJsonAsync(first)).ContinuationId();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": "send it", "agentcore": { "approval": { "request_id": "req-1", "approved": true } } }""");

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task EditAtAnEarlierMessage_WithdrawsTheWordsAfterIt()
    {
        using FragmentingChatClient reply = new("answer one", "answer two");
        await using var host = await ResponsesHost.StartAsync(TwoStagesYaml, reply);

        using var first = await host.PostAsync("""{ "stream": false, "input": "first question" }""");
        var firstBody = await ResponsesHost.ReadJsonAsync(first);
        var convId = firstBody.ContinuationId();
        var anchor = firstBody["metadata"]!["message_id"]!.GetValue<string>();

        using var second = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": "second question" }""");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Hanging the third turn off the first reply takes back everything after it: the model
        // sees the first exchange and the corrected words, but never the withdrawn question.
        using var third = await host.PostAsync(
            $$"""{ "stream": false, "conversation": "{{convId}}", "input": "corrected question", "agentcore": { "message_id": "q3", "parent_id": "{{anchor}}" } }""");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var thirdRequest = reply.LastRequest;
        Assert.NotNull(thirdRequest);
        Assert.Contains(thirdRequest, m => m.Text.Contains("first question", StringComparison.Ordinal));
        Assert.DoesNotContain(thirdRequest, m => m.Text.Contains("second question", StringComparison.Ordinal));
        Assert.Contains(thirdRequest, m => m.Text.Contains("corrected question", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DialectStream_WritesDrawingsAsTheirOwnEvents()
    {
        await using var host = await ResponsesHost.StartAsync(
            DrawingYaml,
            new DrawingChatClient(),
            configure: options => options.Bind("DrawIt", (AgentCore.Application.Runtime.TurnInvocation? turn) =>
            {
                turn?.Screen?.Publish("generative-ui", "chart-1", new { title = "Q3 revenue" });
                return ValueTask.FromResult<object?>("drew a Card; buttons: none");
            }));

        using var response = await host.PostAsync(
            """{ "stream": true, "input": "show me revenue", "agentcore": { "message_id": "m1" } }""");
        var events = await AgentCore.AspNetCore.Tests.Fakes.ChatCompletionsHost.ReadEventsAsync(response);

        var chunks = events
            .Where(text => text != "[DONE]")
            .Select(text => System.Text.Json.JsonDocument.Parse(text).RootElement)
            .ToList();
        var drawing = Assert.Single(chunks, static chunk => chunk.TryGetProperty("agentcore_data", out var data)
            && data.ValueKind != System.Text.Json.JsonValueKind.Null);
        Assert.Equal("generative-ui", drawing.GetProperty("agentcore_data").GetProperty("name").GetString());
        Assert.Equal(
            "Q3 revenue",
            drawing.GetProperty("agentcore_data").GetProperty("data").GetProperty("title").GetString());
    }

    [Fact]
    public async Task PureStream_WritesNoDialectEvents()
    {
        await using var host = await ResponsesHost.StartAsync(
            DrawingYaml,
            new DrawingChatClient(),
            configure: options => options.Bind("DrawIt", (AgentCore.Application.Runtime.TurnInvocation? turn) =>
            {
                turn?.Screen?.Publish("generative-ui", "chart-1", new { title = "Q3 revenue" });
                return ValueTask.FromResult<object?>("drew a Card; buttons: none");
            }));

        using var response = await host.PostAsync("""{ "stream": true, "input": "show me revenue" }""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await AgentCore.AspNetCore.Tests.Fakes.ChatCompletionsHost.ReadEventsAsync(response);

        Assert.DoesNotContain(events
            .Where(text => text != "[DONE]")
            .Select(text => System.Text.Json.JsonDocument.Parse(text).RootElement),
            static chunk => chunk.TryGetProperty("agentcore_data", out var data)
                && data.ValueKind != System.Text.Json.JsonValueKind.Null);
    }
    private const string TerminalYaml =
        """
        apiVersion: agentcore/v1
        name: responses-terminal
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer one" }
        policy:
          initial: done
          stages:
            - { id: done, agent: solo, terminal: true }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string ApprovalYaml =
        """
        apiVersion: agentcore/v1
        name: responses-approval
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "send the mail", tools: [ send_email ] }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string DrawingYaml =
        """
        apiVersion: agentcore/v1
        name: responses-drawing
        tools:
          - id: draw_it
            kind: binding
            binds: DrawIt
            description: Draw something for the caller.
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

    private static Microsoft.Extensions.AI.ApprovalRequiredAIFunction GatedSendEmail(Action onSend) =>
        new(Microsoft.Extensions.AI.AIFunctionFactory.Create(
            (string to) =>
            {
                onSend();
                return "sent";
            },
            "send_email",
            "Send an email."));

    /// <summary>A tool source that serves one gated id, standing in for an approval-required host tool.</summary>
    private sealed class GatedSource(Func<Microsoft.Extensions.AI.AITool> build)
        : AgentCore.Application.Ports.IToolSource
    {
        public ValueTask<IReadOnlyList<AgentCore.Application.Tools.Registry.ToolRegistration>> ProvideAsync(
            AgentCore.Application.Tools.Registry.ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentCore.Application.Tools.Registry.ToolRegistration>>(
                [new AgentCore.Application.Tools.Registry.ToolRegistration("send_email", "Send an email.", build)]);
    }

    /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
    private sealed class GatedToolCallingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyCalled = messages.Any(message => message.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any());

            if (!alreadyCalled && options?.Tools?.OfType<Microsoft.Extensions.AI.AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new Microsoft.Extensions.AI.FunctionCallContent(
                        "call_1",
                        tool.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" })]);
                yield break;
            }

            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done.");
        }

        public async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<Microsoft.Extensions.AI.ChatResponseUpdate> updates = [];
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

    /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
    private sealed class DrawingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyDrew = messages.Any(message => message.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any());

            if (!alreadyDrew && options?.Tools?.OfType<Microsoft.Extensions.AI.AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new Microsoft.Extensions.AI.FunctionCallContent(
                        "call_1",
                        tool.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "a card" })]);
                yield break;
            }

            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "here it is.");
        }

        public async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<Microsoft.Extensions.AI.ChatResponseUpdate> updates = [];
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

/// <summary>Reads what one Responses answer carries.</summary>
internal static class ResponsesAnswer
{
    /// <summary>Reads the assistant text of one answer.</summary>
    /// <param name="body">The answer.</param>
    /// <returns>Every output text, joined.</returns>
    internal static string OutputText(this JsonNode body)
        => string.Join("", body["output"]?.AsArray()
            .SelectMany(o => o?["content"]?.AsArray() ?? [])
            .Select(c => c?["text"]?.GetValue<string>() ?? "") ?? []);

    /// <summary>Reads the id a later turn hangs off: the conversation, or the response.</summary>
    /// <param name="body">The answer.</param>
    /// <returns>The continuation id.</returns>
    internal static string ContinuationId(this JsonNode body)
    {
        if (body["conversation"] is JsonObject conversation
            && conversation["id"]?.GetValue<string>() is { Length: > 0 } conversationId)
        {
            return conversationId;
        }

        return body["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Answer carries no continuation id.");
    }
}

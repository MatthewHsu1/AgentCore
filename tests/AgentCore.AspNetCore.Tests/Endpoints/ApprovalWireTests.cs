using System.Net;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Registry;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The wire an approval travels: one SSE field that asks, and one request field that answers.
/// </summary>
/// <remarks>
/// <para>
/// Like the tool loop facts, an approval is host business the caller must see: the model asked to
/// run something it may not run alone, so the request rides its own <c>agentcore_approval</c>
/// field, and the answer comes back on <c>agentcore.approval</c> naming the same request id on the
/// same conversation. A client that does not speak the dialect reads text frames alone.
/// </para>
/// <para>
/// Every test here runs offline against a fake model, on a real socket.
/// </para>
/// </remarks>
public sealed class ApprovalWireTests
{
    private const string ApprovalYaml =
        """
        apiVersion: agentcore/v1
        name: approval-wire
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

    [Fact]
    public async Task AGatedToolCall_StreamsAnApprovalEventWithTheCallFacts()
    {
        int sent = 0;
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var response = await PostStreamAsync(host, "send it");
        var events = await ResponsesHost.ReadEventsAsync(response);

        var approval = Assert.Single(ApprovalEventsOf(events));
        Assert.False(string.IsNullOrEmpty(approval.GetProperty("request_id").GetString()));
        Assert.Equal("send_email", approval.GetProperty("tool").GetString());
        Assert.Equal("a@b.com", approval.GetProperty("arguments").GetProperty("to").GetString());

        Assert.Empty(ResponsesHost.TextDeltas(events));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AGatedToolCall_AnswersWholeWithTheRequestOnTheTurn()
    {
        int sent = 0;
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var response = await PostTextAsync(host, "send it");
        var body = await ResponsesHost.ReadJsonAsync(response);

        Assert.Equal(string.Empty, body.OutputText());
        var approvals = JsonDocument.Parse(body["metadata"]!["approvals"]!.GetValue<string>()).RootElement;
        var approval = Assert.Single(approvals.EnumerateArray());
        Assert.Equal("send_email", approval.GetProperty("tool").GetString());
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AnApprovalAnswer_RunsTheToolAndReplies()
    {
        int sent = 0;
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var first = await PostTextAsync(host, "send it");
        var asked = await ResponsesHost.ReadJsonAsync(first);
        var conversation = asked.ContinuationId();
        var requestId = JsonDocument.Parse(
            asked["metadata"]!["approvals"]!.GetValue<string>())
            .RootElement[0].GetProperty("request_id").GetString() ?? string.Empty;

        using var second = await PostApprovalAsync(host, conversation, requestId, approved: true);
        var replied = await ResponsesHost.ReadJsonAsync(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, sent);
        Assert.Equal("done.", replied.OutputText());
    }

    [Fact]
    public async Task ARejection_LeavesTheToolUnrun()
    {
        int sent = 0;
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var first = await PostTextAsync(host, "send it");
        var asked = await ResponsesHost.ReadJsonAsync(first);
        var conversation = asked.ContinuationId();
        var requestId = JsonDocument.Parse(
            asked["metadata"]!["approvals"]!.GetValue<string>())
            .RootElement[0].GetProperty("request_id").GetString() ?? string.Empty;

        using var second = await PostApprovalAsync(host, conversation, requestId, approved: false);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AnApprovalAnswerForAnUnknownCall_IsNotFound()
    {
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var response = await PostApprovalAsync(host, "conv-that-never-existed", "req-1", approved: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnApprovalAnswerWithNoCall_IsBadRequest()
    {
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var response = await PostApprovalAsync(host, null, "req-1", approved: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnApprovalAnswerForARequestNothingAsked_IsConflict()
    {
        await using var host = await ResponsesHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var first = await PostTextAsync(host, "send it");
        var asked = await ResponsesHost.ReadJsonAsync(first);
        var conversation = asked.ContinuationId();

        using var second = await PostApprovalAsync(host, conversation, "req-nothing-asked", approved: true);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>Sends one turn of words.</summary>
    private static Task<HttpResponseMessage> PostTextAsync(ResponsesHost host, string text, string? conversation = null)
        => host.PostAsync(conversation is { Length: > 0 }
            ? $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "{{text}}" }"""
            : $$"""{ "stream": false, "input": "{{text}}" }""");

    /// <summary>Sends one turn of words and reads the answer as it arrives.</summary>
    /// <remarks>
    /// The dialect member opts the stream into the browser parts: without it the frames carry
    /// text alone and an approval ask would suspend the turn in silence.
    /// </remarks>
    private static Task<HttpResponseMessage> PostStreamAsync(ResponsesHost host, string text, string? conversation = null)
        => host.PostAsync(conversation is { Length: > 0 }
            ? $$"""{ "stream": true, "conversation": "{{conversation}}", "input": "{{text}}", "agentcore": { "message_id": "m1" } }"""
            : $$"""{ "stream": true, "input": "{{text}}", "agentcore": { "message_id": "m1" } }""");

    /// <summary>Answers one pending approval.</summary>
    private static Task<HttpResponseMessage> PostApprovalAsync(
        ResponsesHost host, string? conversation, string requestId, bool approved)
    {
        var answer = $$"""{ "approval": { "request_id": "{{requestId}}", "approved": {{(approved ? "true" : "false")}} } }""";
        return host.PostAsync(conversation is { Length: > 0 }
            ? $$"""{ "stream": false, "conversation": "{{conversation}}", "input": [], "agentcore": {{answer}} }"""
            : $$"""{ "stream": false, "input": [], "agentcore": {{answer}} }""");
    }

    private static ApprovalRequiredAIFunction GatedSendEmail(Action onSend) => new(AIFunctionFactory.Create(
        (string to) =>
        {
            onSend();
            return "sent";
        },
        "send_email",
        "Send an email."));

    /// <summary>Picks the <c>agentcore_approval</c> payloads out of events already read.</summary>
    private static List<JsonElement> ApprovalEventsOf(IEnumerable<string> events)
    {
        return events
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_approval", out var approval)
                            && approval.ValueKind != JsonValueKind.Null)
            .Select(chunk => chunk.GetProperty("agentcore_approval"))
            .ToList();
    }

    /// <summary>A tool source that serves one gated id, standing in for an approval-required host tool.</summary>
    private sealed class GatedSource(Func<AITool> build) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                [new ToolRegistration("send_email", "Send an email.", build)]);
    }

    /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
    private sealed class GatedToolCallingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" })]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
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

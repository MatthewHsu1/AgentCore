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
/// same call. An ordinary OpenAI client ignores both fields.
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
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var response = await host.PostStreamingAsync("send it");
        var events = await ChatCompletionsHost.ReadEventsAsync(response);

        var approval = Assert.Single(ApprovalEventsOf(events));
        Assert.False(string.IsNullOrEmpty(approval.GetProperty("request_id").GetString()));
        Assert.Equal("send_email", approval.GetProperty("tool").GetString());
        Assert.Equal("a@b.com", approval.GetProperty("arguments").GetProperty("to").GetString());

        Assert.Empty(ChatCompletionsHost.ContentDeltas(events));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AGatedToolCall_AnswersWholeWithTheRequestOnTheTurn()
    {
        int sent = 0;
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var response = await host.PostAsync("send it");
        var body = await ChatCompletionsHost.ReadJsonAsync(response);

        Assert.Equal(string.Empty, body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        var approval = Assert.Single(body["agentcore"]!["approvals"]!.AsArray());
        Assert.Equal("send_email", approval!["tool"]!.GetValue<string>());
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AnApprovalAnswer_RunsTheToolAndReplies()
    {
        int sent = 0;
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var first = await host.PostAsync("send it");
        var asked = await ChatCompletionsHost.ReadJsonAsync(first);
        var session = asked["agentcore"]!["session"]!.GetValue<string>();
        var requestId = asked["agentcore"]!["approvals"]![0]!["request_id"]!.GetValue<string>();

        using var second = await host.PostApprovalAsync(session, requestId, approved: true, stream: false);
        var replied = await ChatCompletionsHost.ReadJsonAsync(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, sent);
        Assert.Equal("done.", replied["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARejection_LeavesTheToolUnrun()
    {
        int sent = 0;
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => sent++))));

        using var first = await host.PostAsync("send it");
        var asked = await ChatCompletionsHost.ReadJsonAsync(first);
        var session = asked["agentcore"]!["session"]!.GetValue<string>();
        var requestId = asked["agentcore"]!["approvals"]![0]!["request_id"]!.GetValue<string>();

        using var second = await host.PostApprovalAsync(session, requestId, approved: false, stream: false);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AnApprovalAnswerForAnUnknownCall_IsNotFound()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var response = await host.PostApprovalAsync("call-that-never-existed", "req-1", approved: true, stream: false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnApprovalAnswerWithNoCall_IsBadRequest()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var response = await host.PostApprovalAsync(string.Empty, "req-1", approved: true, stream: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnApprovalAnswerForARequestNothingAsked_IsConflict()
    {
        await using var host = await ChatCompletionsHost.StartAsync(
            ApprovalYaml,
            new GatedToolCallingChatClient(),
            configure: options => options.AddToolSource(_ => new GatedSource(() => GatedSendEmail(() => { }))));

        using var first = await host.PostAsync("send it");
        var asked = await ChatCompletionsHost.ReadJsonAsync(first);
        var session = asked["agentcore"]!["session"]!.GetValue<string>();

        using var second = await host.PostApprovalAsync(session, "req-nothing-asked", approved: true, stream: false);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
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
            .Where(text => text != "[DONE]")
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

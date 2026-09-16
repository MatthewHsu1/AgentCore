using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The approval round trip through the turn loop: a gated tool suspends the turn with a request
/// instead of failing it, and the caller's answer on the same call runs the tool.
/// </summary>
/// <remarks>
/// Every test here runs offline. There is no network call and no API key anywhere in this file.
/// </remarks>
public sealed class CallSessionApprovalTests
{
    private const string GatedYaml =
        """
        apiVersion: agentcore/v1
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email ]
        entries:
          main:
            agent: only
        """;

    [Fact]
    public async Task AGatedToolCall_EndsTheTurnWithTheRequestAndNoFailure()
    {
        var token = TestContext.Current.CancellationToken;
        int sent = 0;
        var session = Session(GatedYaml, () => sent++, token);

        var turn = await session.RunTurnAsync("send it", token);

        Assert.Null(turn.Failure);
        Assert.Equal(string.Empty, turn.ReplyText);
        Assert.Equal(0, sent);
        var approval = Assert.Single(turn.Approvals);
        Assert.False(string.IsNullOrEmpty(approval.RequestId));
        Assert.Equal("send_email", approval.ToolName);
        Assert.Equal("a@b.com", approval.Arguments.GetProperty("to").GetString());
    }

    [Fact]
    public async Task AnApprovalAnswer_RunsTheToolAndReplies()
    {
        var token = TestContext.Current.CancellationToken;
        int sent = 0;
        var session = Session(GatedYaml, () => sent++, token);

        var asked = await session.RunTurnAsync("send it", token);
        var answer = session.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: true);
        Assert.NotNull(answer);
        var replied = await session.RunTurnMessageAsync(answer!, token);

        Assert.Equal(1, sent);
        Assert.Null(replied.Failure);
        Assert.Equal("done.", replied.ReplyText);
        Assert.Empty(replied.Approvals);
    }

    [Fact]
    public async Task ARejection_LeavesTheToolUnrun()
    {
        var token = TestContext.Current.CancellationToken;
        int sent = 0;
        var session = Session(GatedYaml, () => sent++, token);

        var asked = await session.RunTurnAsync("send it", token);
        var answer = session.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: false);
        Assert.NotNull(answer);
        var replied = await session.RunTurnMessageAsync(answer!, token);

        Assert.Equal(0, sent);
        Assert.Null(replied.Failure);
    }

    [Fact]
    public async Task AnUnknownRequestId_BuildsNoAnswer()
    {
        var token = TestContext.Current.CancellationToken;
        var session = Session(GatedYaml, () => { }, token);

        _ = await session.RunTurnAsync("send it", token);

        Assert.Null(session.TryCreateApprovalAnswer("no-such-request", approved: true));
    }

    [Fact]
    public async Task AnAnsweredRequest_BuildsNoSecondAnswer()
    {
        var token = TestContext.Current.CancellationToken;
        var session = Session(GatedYaml, () => { }, token);

        var asked = await session.RunTurnAsync("send it", token);
        var requestId = asked.Approvals[0].RequestId;
        var answer = session.TryCreateApprovalAnswer(requestId, approved: true);

        Assert.NotNull(answer);
        _ = await session.RunTurnMessageAsync(answer, token);

        Assert.Null(session.TryCreateApprovalAnswer(requestId, approved: true));
    }

    [Fact]
    public async Task AGatedToolCall_StreamsTheRequestToTheHost()
    {
        var token = TestContext.Current.CancellationToken;
        var session = Session(GatedYaml, () => { }, token);

        List<ChatResponseUpdate> updates = [];
        await foreach (var update in session.RunTurnStreamingAsync("send it", token))
        {
            updates.Add(update);
        }

        Assert.Contains(
            updates.SelectMany(update => update.Contents),
            content => content is ToolApprovalRequestContent);
        Assert.Null(session.LastTurn?.Failure);
        Assert.NotEmpty(session.LastTurn?.Approvals ?? []);
    }

    [Fact]
    public async Task AGatedToolCall_KeepsTheQueueInTheSnapshot()
    {
        var token = TestContext.Current.CancellationToken;
        var session = Session(GatedYaml, () => { }, token);

        _ = await session.RunTurnAsync("send it", token);

        Assert.True(session.Snapshot().Providers.ContainsKey("_pendingApprovalRequests"));
    }

    private static CallSession Session(string yaml, Action onSend, CancellationToken token)
    {
        ToolCallingChatClient client = new(
            "done.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" });
        var document = ConfigurationLoader.LoadYaml(yaml);
        var compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(client))
            {
                Tools = TestToolRegistry.From(
                    document,
                    declared => declared.Uses == "test.send" ? GatedSendEmail(onSend) : null,
                    token),
            })["main"];

        return new CallSessionFactory(
            compiled,
            new TestGuardEvaluator(document),
            extractor: null,
            timeProvider: null).Create();
    }

    private static ApprovalRequiredAIFunction GatedSendEmail(Action onSend) => new(AIFunctionFactory.Create(
        (string to) =>
        {
            onSend();
            return "sent";
        },
        "send_email",
        "Send an email."));
}

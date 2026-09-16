using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.AgentCoreAgentTestSupport;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The <see cref="AgentCoreAgent"/> shim: the whole turn loop behind the framework's own
/// <see cref="AIAgent"/> seam. One session is one call, one run is one turn.
/// </summary>
public sealed class AgentCoreAgentTests
{
    [Fact]
    public async Task RunAsync_WithOneSession_RunsTurnsOfOneCall()
    {
        var reply = new SequencedChatClient("first reply", "second reply");
        var agent = BuildAgent(reply, out _);

        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var first = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);
        var second = await agent.RunAsync("and again", session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("first reply", first.Text);
        Assert.Equal("second reply", second.Text);

        // The second run carried the whole call: turn one's exchange sits in front of turn two.
        var request = reply.Requests[1];
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "hello");
        Assert.Contains(request, message => message.Role == ChatRole.Assistant && message.Text == "first reply");
        Assert.Equal("and again", reply.LastUserText(1));
    }

    [Fact]
    public async Task RunStreamingAsync_StreamsTheFilteredReply()
    {
        var reply = new SequencedChatClient("streamed reply");
        var agent = BuildAgent(reply, out _);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        List<AgentResponseUpdate> updates = [];
        await foreach (var update in agent.RunStreamingAsync(
            "hello", session, cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.Equal("streamed reply", string.Concat(updates.Select(update => update.Text)));

        // The turn committed: the session's call holds the finished turn.
        var call = session.GetService<CallSession>();
        Assert.NotNull(call);
        Assert.Equal("streamed reply", call.LastTurn?.ReplyText);
    }

    [Fact]
    public async Task RunAsync_TakesTheLastUserMessage_AndIgnoresTheHistoryInFront()
    {
        var reply = new SequencedChatClient("the reply");
        var agent = BuildAgent(reply, out _);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // The shape a protocol host sends: history first, the new message last. The session owns
        // the transcript, so only the last user message is new.
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "an old turn"),
            new(ChatRole.Assistant, "an old reply"),
            new(ChatRole.User, "the new turn"),
        ];

        _ = await agent.RunAsync(messages, session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("the new turn", reply.LastUserText(0));
        Assert.DoesNotContain(reply.Requests[0], message => message.Text == "an old reply");
    }

    [Fact]
    public async Task RunAsync_WithNoUserMessage_Throws()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out _);

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => agent.RunAsync(
                [new ChatMessage(ChatRole.Assistant, "not a prompt")],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("no user message", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithNoSession_RunsOneShotCalls()
    {
        var reply = new SequencedChatClient("one", "two");
        var agent = BuildAgent(reply, out _);

        _ = await agent.RunAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        _ = await agent.RunAsync("second", cancellationToken: TestContext.Current.CancellationToken);

        // No session, no continuity: the second run is a new call and saw nothing of the first.
        Assert.DoesNotContain(reply.Requests[1], message => message.Text == "first");
        Assert.DoesNotContain(reply.Requests[1], message => message.Text == "one");
    }

    [Fact]
    public async Task RunAsync_WithAForeignSession_Throws()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out _);

        // A session another agent kind created. ChatClientAgent builds one of its own type.
        var foreign = await new ChatClientAgent(new SequencedChatClient("other"))
            .CreateSessionAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => agent.RunAsync("hello", foreign, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Incompatible session type", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSessionAsync_WithACallId_NamesTheCall()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out _);

        var session = await agent.CreateSessionAsync("call-42", TestContext.Current.CancellationToken);

        Assert.Equal("call-42", session.GetService<CallSession>()?.CallId);
    }

    [Fact]
    public async Task GetService_OnTheSession_AnswersTheCallSession()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out _);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var call = session.GetService<CallSession>();

        Assert.NotNull(call);
        Assert.Same(call, session.GetService<Application.Ports.IConversationPort>());
    }

    [Fact]
    public void Name_ReportsWhatTheHostNamedIt()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out var compiled);

        Assert.Equal(compiled.Name, agent.Name);
    }
}

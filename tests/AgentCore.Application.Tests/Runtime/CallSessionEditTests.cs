using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// A caller sends an earlier message again, and the call takes back what it said after it.
/// </summary>
public sealed class CallSessionEditTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        name: edit-check
        agents:
          items:
            - { id: only, instructions: "answer the caller" }
        """;

    private const string TerminalYaml = """
        apiVersion: agentcore/v1
        name: ends-at-once
        agents:
          items:
            - { id: only, instructions: "answer the caller" }
        policy:
          initial: done
          stages:
            - { id: done, agent: only, terminal: true }
        """;

    [Fact]
    public async Task AnEdit_TakesTheReplacedWordsOutOfWhatTheModelReads()
    {
        using ScriptedChatClient scripted = new("an answer.");
        RequestCapturingChatClient reply = new(scripted);
        var session = CreateSession(OneAgentYaml, reply);

        await session.RunTurnAtOriginAsync(
            "first question",
            new CallTurnOrigin("caller-1", null) { NamesParent = true },
            TestContext.Current.CancellationToken);
        var firstReply = session.LastReplyMessageId;

        await session.RunTurnAtOriginAsync(
            "second question",
            new CallTurnOrigin("caller-2", firstReply) { NamesParent = true },
            TestContext.Current.CancellationToken);

        await session.RunTurnAtOriginAsync(
            "second question, rewritten",
            new CallTurnOrigin("caller-3", firstReply) { NamesParent = true },
            TestContext.Current.CancellationToken);

        // What the model was actually handed, not what the reply says: a stub controls the reply
        // whatever the history did.
        var seen = reply.Requests[^1].Select(message => message.Text).ToList();
        Assert.Contains("first question", seen);
        Assert.Contains("second question, rewritten", seen);
        Assert.DoesNotContain("second question", seen);
    }

    [Fact]
    public async Task AnEdit_NamesTheTurnsItWithdrew()
    {
        using ScriptedChatClient reply = new("an answer.");
        RecordingObserver observer = new();
        var session = CreateSession(OneAgentYaml, reply, observer);

        await session.RunTurnAtOriginAsync(
            "q1",
            new CallTurnOrigin("caller-1", null) { NamesParent = true },
            TestContext.Current.CancellationToken);
        var firstReply = session.LastReplyMessageId;

        await session.RunTurnAsync("q2", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);

        await session.RunTurnAtOriginAsync(
            "q2, rewritten",
            new CallTurnOrigin("caller-4", firstReply) { NamesParent = true },
            TestContext.Current.CancellationToken);

        // The rows of turns 1 and 2 are deleted by now, so the trail can only say what it was told.
        var superseded = Assert.Single(
            observer.Events, raised => raised.Kind == CallEventKind.TurnSuperseded);
        Assert.Equal("1", superseded.Payload[AuditPayloadKeys.WithdrewFromTurnIndex]);
        Assert.Equal("2", superseded.Payload[AuditPayloadKeys.WithdrewThroughTurnIndex]);
        Assert.Equal(3, superseded.TurnIndex);
    }

    [Fact]
    public async Task AnEditOnATerminalCall_IsRefusedAndTakesNothing()
    {
        using ScriptedChatClient reply = new("an answer.");
        var session = CreateSession(TerminalYaml, reply);

        await session.RunTurnAtOriginAsync(
            "q1",
            new CallTurnOrigin("caller-1", null) { NamesParent = true },
            TestContext.Current.CancellationToken);
        var firstReply = session.LastReplyMessageId;
        Assert.True(session.IsComplete);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunTurnAtOriginAsync(
                "q1, rewritten",
                new CallTurnOrigin("caller-2", null) { NamesParent = true },
                TestContext.Current.CancellationToken));

        // The withdrawal deletes. A turn the guards refuse must not have taken the tail of the call
        // with it on the way out, or the caller is left with neither the old words nor the new.
        Assert.Equal(2, session.Transcript.Count);
        Assert.NotNull(firstReply);
    }

    [Fact]
    public async Task AnOriginThatNamesNoParent_WithdrawsNothing()
    {
        using ScriptedChatClient reply = new("an answer.");
        RecordingObserver observer = new();
        var session = CreateSession(OneAgentYaml, reply, observer);

        await session.RunTurnAsync("q1", TestContext.Current.CancellationToken);

        // A null parent means the start of the call and would take every word of it. A caller that
        // named its own message and said nothing about a parent has asked for no such thing.
        await session.RunTurnAtOriginAsync(
            "q2",
            new CallTurnOrigin("caller-2", null) { NamesParent = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(4, session.Transcript.Count);
        Assert.DoesNotContain(observer.Events, raised => raised.Kind == CallEventKind.TurnSuperseded);
    }

    private static CallSession CreateSession(
        string yaml, IChatClient reply, ICallObserver? observer = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                CallStore = new InMemoryCallStore(),
                Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
            });

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null,
            observers: observer is null ? null : [observer]);

        return factory.Create("call-1");
    }

    /// <summary>Keeps every fact of the call, in the order the turn loop raised them.</summary>
    private sealed class RecordingObserver : ICallObserver
    {
        private readonly Lock _gate = new();
        private readonly List<CallEvent> _events = [];

        public IReadOnlyList<CallEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _events.Add(callEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}

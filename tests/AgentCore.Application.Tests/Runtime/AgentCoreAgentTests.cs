using AgentCore.TestSupport;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The <see cref="AgentCoreAgent"/> shim: the whole turn loop behind the framework's own
/// <see cref="AIAgent"/> seam. One session is one call, one run is one turn.
/// </summary>
/// <remarks>
/// Every test here runs offline. There is no network call and no API key anywhere in this file.
/// </remarks>
public sealed class AgentCoreAgentTests
{
    private const string SingleAgentYaml =
        """
        apiVersion: agentcore/v1
        name: shim-test
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer the caller" }
        """;

    // The same row with one declared slot, so the round trip has something of its own to carry.
    // SingleAgentYaml declares none, and a document with no slots proves nothing about slots.
    private const string SlottedAgentYaml =
        """
        apiVersion: agentcore/v1
        name: shim-test-slots
        state:
          escalate: { type: boolean, writer: extractor, default: false }
          note: { type: string, writer: extractor, default: "" }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer the caller" }
        """;

    // A policy that ends itself after one turn, so a call can be driven terminal and then round
    // tripped. The guard reads the reserved turnIndex slot, so no extractor and no tool is needed.
    private const string TerminalAgentYaml =
        """
        apiVersion: agentcore/v1
        name: shim-test-terminal
        guards:
          always: { ">=": [ { var: turnIndex }, 0 ] }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer the caller" }
        policy:
          initial: talking
          stages:
            - { id: talking, agent: solo, to: [ { stage: done, when: always } ] }
            - { id: done, agent: solo, terminal: true }
        """;

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
    public async Task SerializeSessionAsync_WritesTheStateTheCallHolds()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

        var call = session.GetService<CallSession>();
        Assert.NotNull(call);
        Assert.True(call.State.TryWrite("escalate", JsonValue.Create(true)));

        var serialized = await agent.SerializeSessionAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        var read = serialized.GetProperty("state").Deserialize<CallSessionState>(CallStateJson.Options);

        Assert.NotNull(read);
        Assert.Equal(CallSessionState.CurrentVersion, read.Version);
        Assert.Equal(call.Stage, read.Stage);
        Assert.True(read.Slots["escalate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SerializeSessionAsync_NamesTheCallTheStateBelongsTo()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _);
        var session = await agent.CreateSessionAsync("call-42", TestContext.Current.CancellationToken);

        var serialized = await agent.SerializeSessionAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        // Store 1 is keyed by call id. State that travelled without one would come back on a call
        // that has no words behind it, which is the one failure this envelope exists to prevent.
        Assert.Equal("call-42", serialized.GetProperty("callId").GetString());
    }

    [Fact]
    public async Task SerializeSessionAsync_NoSession_IsRefusedRatherThanAnsweredWithAFreshCall()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _);

        // A run answers a null session by making one — the framework's one-shot shape, a call nothing
        // continues. There is no one-shot serialize: the envelope would name a fresh random id beside
        // an empty state, and a host would keep that as its checkpoint and never learn it points at
        // nothing.
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await agent.SerializeSessionAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARoundTrip_ComesBackOnTheSameCall()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply", "another reply"), out _);
        var session = await agent.CreateSessionAsync("call-42", TestContext.Current.CancellationToken);
        _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

        var serialized = await agent.SerializeSessionAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);
        var revived = await agent.DeserializeSessionAsync(
            serialized, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("call-42", revived.GetService<CallSession>()?.CallId);
    }

    [Fact]
    public async Task DeserializeSessionAsync_BringsBackACallThatCarriesThatState()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

        CallSessionState stored = new()
        {
            Stage = string.Empty,
            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["escalate"] = JsonValue.Create(true),
            },
        };

        var revived = await agent.DeserializeSessionAsync(
            Envelope("call-42", stored), cancellationToken: TestContext.Current.CancellationToken);

        // The turn is what opens the session, and the session is the one place a call resumes from,
        // whichever of the two sources it resumes out of.
        _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

        var call = revived.GetService<CallSession>();

        Assert.NotNull(call);
        Assert.False(call.IsComplete);
        Assert.True(call.State.Read("escalate")!.GetValue<bool>());
    }

    [Fact]
    public async Task ARoundTripBeforeTheFirstTurn_LosesNothing()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

        CallSessionState checkpoint = new()
        {
            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["escalate"] = JsonValue.Create(true),
                ["note"] = JsonValue.Create("carried"),
            },
        };

        var revived = await agent.DeserializeSessionAsync(
            Envelope("call-42", checkpoint), cancellationToken: TestContext.Current.CancellationToken);

        // No turn in between. The restore has not run yet, so a snapshot taken off the state
        // document would answer nothing at all — and this host would then write that nothing over
        // the checkpoint it just handed in. The seam has to read back what it was given.
        var serialized = await agent.SerializeSessionAsync(
            revived, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("call-42", serialized.GetProperty("callId").GetString());

        var read = serialized.GetProperty("state").Deserialize<CallSessionState>(CallStateJson.Options);

        Assert.NotNull(read);
        Assert.True(read.Slots["escalate"]!.GetValue<bool>());
        Assert.Equal("carried", read.Slots["note"]!.GetValue<string>());

        // And the ordinary path is untouched. Once the turn opens the call, the document is what
        // the call would resume from and the checkpoint is spent — so a value written after that
        // turn is what comes back, not the one the checkpoint still holds. Store 1's writer reads
        // this same method, so a snapshot that kept answering the checkpoint would freeze store 0
        // at the moment the call was revived and never record another thing the call learned.
        _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

        var call = revived.GetService<CallSession>();
        Assert.NotNull(call);
        Assert.True(call.State.TryWrite("note", JsonValue.Create("learned after the turn")));

        var again = await agent.SerializeSessionAsync(
            revived, cancellationToken: TestContext.Current.CancellationToken);
        var after = again.GetProperty("state").Deserialize<CallSessionState>(CallStateJson.Options);

        Assert.NotNull(after);
        Assert.True(after.Slots["escalate"]!.GetValue<bool>());
        Assert.Equal("learned after the turn", after.Slots["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeserializeSessionAsync_WithStateTheDocumentRefuses_StillRunsTheTurn()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

        // Nothing here is anything Snapshot would write. DeserializeSessionAsync is host-facing, so
        // the blob is arbitrary JSON: Restore is best effort and drops each of these with its own
        // diagnostic rather than refusing the call.
        CallSessionState stored = new()
        {
            Stage = "a stage this document never declared",
            IsComplete = true,
            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [ReservedStateSlots.Stage] = JsonValue.Create("reserved"),
                ["a slot this document never declared"] = JsonValue.Create(1),
            },
        };

        var revived = await agent.DeserializeSessionAsync(
            Envelope("call-42", stored), cancellationToken: TestContext.Current.CancellationToken);

        var reply = await agent.RunAsync(
            "hello", revived, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("a reply", reply.Text);

        // The refused stage took IsComplete with it: a call brought back complete would turn every
        // turn away, which is the outcome the best-effort restore exists to avoid.
        Assert.False(revived.GetService<CallSession>()?.IsComplete);
    }

    [Fact]
    public async Task SerializeSessionAsync_CarriesNoWords()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        _ = await agent.RunAsync(
            "remember this sentence", session, cancellationToken: TestContext.Current.CancellationToken);

        var call = session.GetService<CallSession>();
        Assert.NotNull(call);
        Assert.True(call.State.TryWrite("escalate", JsonValue.Create(true)));

        var serialized = await agent.SerializeSessionAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);
        var raw = serialized.GetRawText();

        // Something to lose first. On a document with no slots the whole blob is a handful of short
        // scalars, and an implementation that wrote {} would pass the two assertions below.
        Assert.Contains("escalate", raw, StringComparison.Ordinal);

        // Store 1 is durable already. A checkpoint that carried the words would give one conversation
        // two records and one chance to disagree.
        Assert.DoesNotContain("remember this sentence", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("a reply", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeserializeSessionAsync_WhenStore0KnowsTheCall_Store0Wins()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("call-42", TestContext.Current.CancellationToken);

        // Store 0's own copy of this call: one word, and the state written in the same batch.
        await store.AppendAsync(
            [new CallMessage("call-42", 0, 0, new ChatMessage(ChatRole.User, "an earlier turn"))],
            new CallSessionState
            {
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["escalate"] = JsonValue.Create(false),
                },
            },
            TestContext.Current.CancellationToken);

        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml, store);

        // It disagrees with store 0 on one slot and carries a second store 0 knows nothing about.
        // The second is what a merge would leak: store 0 has no value to write over it.
        CallSessionState checkpoint = new()
        {
            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["escalate"] = JsonValue.Create(true),
                ["note"] = JsonValue.Create("from the checkpoint"),
            },
        };

        var revived = await agent.DeserializeSessionAsync(
            Envelope("call-42", checkpoint), cancellationToken: TestContext.Current.CancellationToken);

        _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

        var call = revived.GetService<CallSession>();
        Assert.NotNull(call);

        // False, which is store 0's value and not the checkpoint's. Store 0's blob rides the same
        // batch as the words above, so its state and store 1's words are of one moment; a
        // checkpoint's state beside those same words can be of two. One precedence, stated.
        Assert.False(call.State.Read("escalate")!.GetValue<bool>());

        // And store 0 wins outright rather than per slot. Where store 0 holds state, the checkpoint
        // contributes nothing at all — not even the slot store 0 never heard of.
        Assert.True(call.State.IsUnfilled("note"));
    }

    [Fact]
    public async Task DeserializeSessionAsync_WithNeitherMember_Throws()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out _, SlottedAgentYaml);

        // The bare CallSessionState: the literal value store 0 keeps in call.state, and the other
        // of the two shapes in this system. Read as an envelope it names no call, and a new random
        // id would take the call's whole transcript with it.
        var bare = JsonSerializer.SerializeToElement(
            new CallSessionState { Stage = string.Empty }, CallStateJson.Options);

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            async () => await agent.DeserializeSessionAsync(
                bare, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("{ callId, state }", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeserializeSessionAsync_WithStateButNoCallId_StartsANewCall()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

        // Lenient on purpose, and the remark promises it: state without an id is still state, and a
        // call that names none gets one made up, exactly as CreateSessionAsync does.
        var named = JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["state"] = JsonSerializer.SerializeToNode(
                    new CallSessionState
                    {
                        Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                        {
                            ["escalate"] = JsonValue.Create(true),
                        },
                    },
                    CallStateJson.Options),
            },
            CallStateJson.Options);

        var revived = await agent.DeserializeSessionAsync(
            named, cancellationToken: TestContext.Current.CancellationToken);

        var call = revived.GetService<CallSession>();

        Assert.NotNull(call);
        Assert.NotEmpty(call.CallId);

        _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(call.State.Read("escalate")!.GetValue<bool>());
    }

    [Fact]
    public async Task ARoundTripOfATerminalCall_ComesBackTerminalAndRefusesItsTurn()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, TerminalAgentYaml);
        var session = await agent.CreateSessionAsync("call-42", TestContext.Current.CancellationToken);

        _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(session.GetService<CallSession>()!.IsComplete);

        var serialized = await agent.SerializeSessionAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);
        var revived = await agent.DeserializeSessionAsync(
            serialized, cancellationToken: TestContext.Current.CancellationToken);

        // A behaviour change worth pinning rather than inferring: before the call state blob, a
        // reloaded page met a call that had forgotten it ended and answered one more turn.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("again", revived, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("runs no further turn", failure.Message, StringComparison.Ordinal);
        Assert.True(revived.GetService<CallSession>()!.IsComplete);
    }

    [Fact]
    public async Task Resume_AfterTheCallHasOpened_ThrowsRatherThanDoingNothing()
    {
        var agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

        var call = session.GetService<CallSession>();
        Assert.NotNull(call);

        // The state is read as the session opens and never again, so a late hand-off would be a
        // silent no-op. Saying so is the whole reason the guard is here.
        var failure = Assert.Throws<InvalidOperationException>(() => call.Resume(new CallSessionState()));

        Assert.Contains("already run a turn", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Name_ReportsWhatTheHostNamedIt()
    {
        var agent = BuildAgent(new SequencedChatClient("unused"), out var compiled);

        Assert.Equal(compiled.Name, agent.Name);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    private static AgentCoreAgent BuildAgent(
        IChatClient reply,
        out CompiledAgent compiled,
        string yaml = SingleAgentYaml,
        ICallStore? store = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new RoutingChatClientFactory(reply)) { CallStore = store });

        CallSessionFactory sessions = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        return new AgentCoreAgent(sessions, compiled.Name);
    }

    /// <summary>Writes what a host hands to <c>DeserializeSessionAsync</c>: a call id and its state.</summary>
    private static JsonElement Envelope(string callId, CallSessionState state)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["callId"] = JsonValue.Create(callId),
                ["state"] = JsonSerializer.SerializeToNode(state, CallStateJson.Options),
            },
            CallStateJson.Options);
}

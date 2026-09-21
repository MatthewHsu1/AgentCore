using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.ConversationSessionResumeTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// What a second session of one conversation restores from the state a first one left behind.
    /// </summary>
    public sealed class ConversationSessionStateRestoreTests
    {
        [Fact]
        public async Task ASecondSessionOfOneConversation_KeepsTheStageTheFirstOneReached()
        {
            InMemoryConversationStore store = new();

            using ScriptedChatClient first = new("what model is it?");
            ConversationSession opened = CreateSession(StagedYaml, first, store);
            _ = await opened.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            await opened.FlushTranscriptAsync();

            Assert.Equal("help", opened.Stage);

            using ScriptedChatClient second = new("sure");
            ConversationSession resumed = CreateSession(StagedYaml, second, store, opened.ConversationId);
            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // StageBefore, and not Stage. The stage is restored when the session OPENS and the session
            // opens on the first turn, so the restored stage is the one that turn spoke in, and 'intake'
            // is what a session that forgot would report. Reading Stage after the turn would prove
            // nothing: this document's only guard reads the reserved turnIndex slot, which store 1
            // restores off the words on its own, so even a session that remembered nothing would land in
            // 'help' by the end of the turn.
            Assert.Equal("help", resumed.LastTurn!.StageBefore);
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_MovesTheStageMachineAndNotOnlyItsLabel()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-machine", TestContext.Current.CancellationToken);

            _ = await store.AppendAsync(
                "C-machine",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState { Stage = "two" },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            ConversationSession resumed = CreateSession(ThreeStageYaml, reply, store, "C-machine");

            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // 'three', because the turn ran in 'two' and moved on from there. A session that restored
            // the reserved stage slot and left the machine where the document starts would report 'two'
            // twice over: 'two' as the stage the turn spoke in, because the slot says so, and 'two'
            // again afterwards, because the machine only just walked to it — while the turn itself was
            // answered by the agent of 'one'.
            Assert.Equal("two", resumed.LastTurn!.StageBefore);
            Assert.Equal("three", resumed.Stage);
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_KeepsTheSlotsTheFirstOneFilled()
        {
            InMemoryConversationStore store = new();

            using ScriptedChatClient first = new("what model is it?");
            ConversationSession opened = CreateSession(StagedYaml, first, store);
            _ = await opened.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            await opened.FlushTranscriptAsync();

            Assert.Equal(1L, opened.State.Read("turnsTaken")!.GetValue<long>());

            using ScriptedChatClient second = new("sure");
            ConversationSession resumed = CreateSession(StagedYaml, second, store, opened.ConversationId);
            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // Two, not one. A session that forgot would count its own turn from zero.
            Assert.Equal(2L, resumed.State.Read("turnsTaken")!.GetValue<long>());
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_DropsASlotTheDocumentNoLongerDeclares()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-drift", TestContext.Current.CancellationToken);

            // Written as though a build that declared 'retired' had run this conversation.
            _ = await store.AppendAsync(
                "C-drift",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState
                {
                    Stage = "intake",
                    Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                    {
                        ["turnsTaken"] = JsonValue.Create(4L),
                        ["retired"] = JsonValue.Create("gone"),
                    },
                },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            RecordingObserver observer = new();
            ConversationSession resumed = CreateSession(StagedYaml, reply, store, "C-drift", observer);
            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            Assert.Equal(5L, resumed.State.Read("turnsTaken")!.GetValue<long>());
            Assert.Null(resumed.State.Read("retired"));

            // Dropped, and said so. A document change that quietly costs a conversation one slot is a change
            // nobody can price afterwards, so the slot that went is named in the fact.
            ConversationEvent dropped = Assert.Single(observer.Events, fact => fact.Kind == ConversationEventKind.StateRestorePartial);
            Assert.Equal(
                "the document no longer declares the slot 'retired'.",
                dropped.Payload[ConversationEventPayloadKeys.Reason]);
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_DropsASlotWhoseDeclaredTypeNoLongerTakesTheStoredValue()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-retyped", TestContext.Current.CancellationToken);

            // Written as though 'turnsTaken' had been declared a string when this conversation ran.
            _ = await store.AppendAsync(
                "C-retyped",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState
                {
                    Stage = "intake",
                    Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                    {
                        ["turnsTaken"] = JsonValue.Create("four"),
                    },
                },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            RecordingObserver observer = new();
            ConversationSession resumed = CreateSession(StagedYaml, reply, store, "C-retyped", observer);
            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // Back to the declared default, and then this turn's own increment.
            Assert.Equal(1L, resumed.State.Read("turnsTaken")!.GetValue<long>());

            // The other half of what TryWrite answers false for. An operator fixes a retyped slot and a
            // deleted slot differently, so the two reasons have to read differently.
            ConversationEvent dropped = Assert.Single(observer.Events, fact => fact.Kind == ConversationEventKind.StateRestorePartial);
            Assert.Equal(
                "the slot 'turnsTaken' no longer takes the value it was stored with.",
                dropped.Payload[ConversationEventPayloadKeys.Reason]);
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_DropsAReservedSlotRatherThanRefusingTheConversation()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-reserved", TestContext.Current.CancellationToken);

            // No writer produces this, but the blob is arbitrary JSON out of store 0 and a host can hand
            // one in directly. StateDocument.TryWrite THROWS on a reserved slot rather than answering
            // false, so a Restore that just asked it would take the conversation down on its first turn.
            _ = await store.AppendAsync(
                "C-reserved",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState
                {
                    Stage = "intake",
                    Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                    {
                        ["turnsTaken"] = JsonValue.Create(4L),
                        ["stage"] = JsonValue.Create("help"),
                    },
                },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            RecordingObserver observer = new();
            ConversationSession resumed = CreateSession(StagedYaml, reply, store, "C-reserved", observer);

            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // It ran, and the declared slot beside the reserved one still landed.
            Assert.NotNull(resumed.LastTurn);
            Assert.Equal(5L, resumed.State.Read("turnsTaken")!.GetValue<long>());

            ConversationEvent dropped = Assert.Single(observer.Events, fact => fact.Kind == ConversationEventKind.StateRestorePartial);
            Assert.Equal(
                "the slot 'stage' is reserved, and a reserved slot is never restored.",
                dropped.Payload[ConversationEventPayloadKeys.Reason]);
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_IgnoresAStateBlobWrittenInAShapeThisBuildDoesNotKnow()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-future", TestContext.Current.CancellationToken);

            _ = await store.AppendAsync(
                "C-future",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState
                {
                    Version = ConversationSessionState.CurrentVersion + 1,
                    Stage = "help",
                    Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                    {
                        ["turnsTaken"] = JsonValue.Create(4L),
                    },
                },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            RecordingObserver observer = new();
            ConversationSession resumed = CreateSession(StagedYaml, reply, store, "C-future", observer);

            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // Nothing of it is taken, not the stage and not the slot. A conversation that forgets answers the
            // caller; a conversation that guesses at a shape it does not know answers them wrongly.
            Assert.Equal("intake", resumed.LastTurn!.StageBefore);
            Assert.Equal(1L, resumed.State.Read("turnsTaken")!.GetValue<long>());

            ConversationEvent dropped = Assert.Single(observer.Events, fact => fact.Kind == ConversationEventKind.StateRestorePartial);
            Assert.Equal(
                $"the stored state is version {ConversationSessionState.CurrentVersion + 1} "
                    + $"and this build writes {ConversationSessionState.CurrentVersion}.",
                dropped.Payload[ConversationEventPayloadKeys.Reason]);
        }

        [Fact]
        public async Task ASecondSessionOfAConversationWhoseDocumentLostItsPolicy_DropsTheStoredStage()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-nopolicy", TestContext.Current.CancellationToken);

            _ = await store.AppendAsync(
                "C-nopolicy",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState { Stage = "intake" },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            RecordingObserver observer = new();

            // OneAgentYaml declares no policy: at all, which is the shape a document takes when its
            // policy section is removed between one session of a conversation and the next.
            ConversationSession resumed = CreateSession(OneAgentYaml, reply, store, "C-nopolicy", observer);

            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // Empty, not 'intake'. There is no machine to hold that stage, and writing it into the
            // reserved slot anyway would hand the guards and the audit chain a stage nothing is in.
            Assert.Equal(string.Empty, resumed.Stage);

            ConversationEvent dropped = Assert.Single(observer.Events, fact => fact.Kind == ConversationEventKind.StateRestorePartial);
            Assert.Equal(
                "the entry declares no policy, so the stage 'intake' has nowhere to go.",
                dropped.Payload[ConversationEventPayloadKeys.Reason]);
        }

        [Fact]
        public async Task ASecondSessionOfAConversationThatAlreadyFinished_RefusesTheTurn()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-finished", TestContext.Current.CancellationToken);

            _ = await store.AppendAsync(
                "C-finished",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState { Stage = "help", IsComplete = true },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            ConversationSession resumed = CreateSession(StagedYaml, reply, store, "C-finished");

            // The restore happens in OpenSessionAsync, which RunTurnAsync awaits before it reads
            // IsComplete — so a conversation that reached a terminal stage stays finished across sessions and
            // turns the caller away rather than starting again. Worth pinning rather than inferring: it
            // is what a chat page reload on a finished conversation now does.
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_FallsBackWhenTheStageIsGone()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-nostage", TestContext.Current.CancellationToken);

            _ = await store.AppendAsync(
                "C-nostage",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState { Stage = "a-stage-nobody-declares" },
                TestContext.Current.CancellationToken);

            using ScriptedChatClient reply = new("sure");
            RecordingObserver observer = new();
            ConversationSession resumed = CreateSession(StagedYaml, reply, store, "C-nostage", observer);

            _ = await resumed.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // It ran. Falling back beats refusing: a conversation that comes back knowing less is worth more than
            // one that will not come back.
            Assert.NotNull(resumed.LastTurn);
            Assert.Equal("intake", resumed.LastTurn!.StageBefore);

            ConversationEvent dropped = Assert.Single(observer.Events, fact => fact.Kind == ConversationEventKind.StateRestorePartial);
            Assert.Contains(
                "a-stage-nobody-declares", dropped.Payload[ConversationEventPayloadKeys.Reason], StringComparison.Ordinal);
        }

        [Fact]
        public async Task AConversationWithNoStoredState_StartsWhereTheDocumentSays()
        {
            InMemoryConversationStore store = new();

            using ScriptedChatClient reply = new("what model is it?");
            ConversationSession session = CreateSession(StagedYaml, reply, store);

            _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.NotNull(session.LastTurn);
            Assert.Equal("intake", session.LastTurn!.StageBefore);
        }
    }
}

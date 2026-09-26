using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static AgentCore.Application.Tests.Runtime.AgentCoreAgentTestSupport;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The <see cref="AgentCoreAgent"/> shim's session envelope: what
    /// <c>SerializeSessionAsync</c>/<c>DeserializeSessionAsync</c> carry across a checkpoint, and what a
    /// resumed conversation keeps of store 0's own record.
    /// </summary>
    public sealed class AgentCoreAgentSessionTests
    {
        [Fact]
        public async Task SerializeSessionAsync_WritesTheStateTheConversationHolds()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);
            AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
            _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = session.GetService<ConversationSession>();
            Assert.NotNull(conversation);
            Assert.True(conversation.State.TryWrite("escalate", JsonValue.Create(true)));

            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSessionState? read = serialized.GetProperty("state").Deserialize<ConversationSessionState>(ConversationStateJson.Options);

            Assert.NotNull(read);
            Assert.Equal(ConversationSessionState.CurrentVersion, read.Version);
            Assert.Equal(conversation.Stage, read.Stage);
            Assert.True(read.Slots["escalate"]!.GetValue<bool>());
        }

        [Fact]
        public async Task SerializeSessionAsync_NamesTheConversationTheStateBelongsTo()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _);
            AgentSession session = await agent.CreateSessionAsync("conversation-42", TestContext.Current.CancellationToken);

            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: TestContext.Current.CancellationToken);

            // Store 1 is keyed by conversation id. State that travelled without one would come back on a conversation
            // that has no words behind it, which is the one failure this envelope exists to prevent.
            Assert.Equal("conversation-42", serialized.GetProperty("conversationId").GetString());
        }

        [Fact]
        public async Task SerializeSessionAsync_NoSession_IsRefusedRatherThanAnsweredWithAFreshConversation()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _);

            // A run with no session ends its conversation with the run, so there is nothing to serialize: the
            // envelope would name a fresh random id beside an empty state, and a host would keep that as its
            // checkpoint and never learn it points at nothing.
            _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await agent.SerializeSessionAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task SerializeSessionAsync_AfterItsConversationHasUnloaded_WritesTheIdWithNoState()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(1));

            AgentSession session = await agent.CreateSessionAsync("conversation-42", TestContext.Current.CancellationToken);
            _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

            clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: TestContext.Current.CancellationToken);

            // Store 0's own copy of the state outranks whatever this session held before it unloaded, so
            // the blob carries only the id: a live session's snapshot would be stale the moment it wrote.
            Assert.Equal("conversation-42", serialized.GetProperty("conversationId").GetString());
            Assert.Equal(JsonValueKind.Null, serialized.GetProperty("state").ValueKind);
        }

        [Fact]
        public async Task ARoundTrip_ComesBackOnTheSameConversation()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply", "another reply"), out _);
            AgentSession session = await agent.CreateSessionAsync("conversation-42", TestContext.Current.CancellationToken);
            _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: TestContext.Current.CancellationToken);
            AgentSession revived = await agent.DeserializeSessionAsync(
                serialized, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("conversation-42", revived.GetService<ConversationSession>()?.ConversationId);
        }

        [Fact]
        public async Task DeserializeSessionAsync_BringsBackAConversationThatCarriesThatState()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

            ConversationSessionState stored = new()
            {
                Stage = string.Empty,
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["escalate"] = JsonValue.Create(true),
                },
            };

            AgentSession revived = await agent.DeserializeSessionAsync(
                Envelope("conversation-42", stored), cancellationToken: TestContext.Current.CancellationToken);

            // The turn is what opens the session, and the session is the one place a conversation resumes from,
            // whichever of the two sources it resumes out of.
            _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = revived.GetService<ConversationSession>();

            Assert.NotNull(conversation);
            Assert.False(conversation.IsComplete);
            Assert.True(conversation.State.Read("escalate")!.GetValue<bool>());
        }

        [Fact]
        public async Task ARoundTripBeforeTheFirstTurn_LosesNothing()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

            ConversationSessionState checkpoint = new()
            {
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["escalate"] = JsonValue.Create(true),
                    ["note"] = JsonValue.Create("carried"),
                },
            };

            AgentSession revived = await agent.DeserializeSessionAsync(
                Envelope("conversation-42", checkpoint), cancellationToken: TestContext.Current.CancellationToken);

            // No turn in between. The restore has not run yet, so a snapshot taken off the state
            // document would answer nothing at all — and this host would then write that nothing over
            // the checkpoint it just handed in. The seam has to read back what it was given.
            JsonElement serialized = await agent.SerializeSessionAsync(
                revived, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("conversation-42", serialized.GetProperty("conversationId").GetString());

            ConversationSessionState? read = serialized.GetProperty("state").Deserialize<ConversationSessionState>(ConversationStateJson.Options);

            Assert.NotNull(read);
            Assert.True(read.Slots["escalate"]!.GetValue<bool>());
            Assert.Equal("carried", read.Slots["note"]!.GetValue<string>());

            // And the ordinary path is untouched. Once the turn opens the conversation, the document is what
            // the conversation would resume from and the checkpoint is spent — so a value written after that
            // turn is what comes back, not the one the checkpoint still holds. Store 1's writer reads
            // this same method, so a snapshot that kept answering the checkpoint would freeze store 0
            // at the moment the conversation was revived and never record another thing the conversation learned.
            _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = revived.GetService<ConversationSession>();
            Assert.NotNull(conversation);
            Assert.True(conversation.State.TryWrite("note", JsonValue.Create("learned after the turn")));

            JsonElement again = await agent.SerializeSessionAsync(
                revived, cancellationToken: TestContext.Current.CancellationToken);
            ConversationSessionState? after = again.GetProperty("state").Deserialize<ConversationSessionState>(ConversationStateJson.Options);

            Assert.NotNull(after);
            Assert.True(after.Slots["escalate"]!.GetValue<bool>());
            Assert.Equal("learned after the turn", after.Slots["note"]!.GetValue<string>());
        }

        [Fact]
        public async Task DeserializeSessionAsync_WithStateTheDocumentRefuses_StillRunsTheTurn()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

            // Nothing here is anything Snapshot would write. DeserializeSessionAsync is host-facing, so
            // the blob is arbitrary JSON: Restore is best effort and drops each of these with its own
            // diagnostic rather than refusing the conversation.
            ConversationSessionState stored = new()
            {
                Stage = "a stage this document never declared",
                IsComplete = true,
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    [ReservedStateSlots.Stage] = JsonValue.Create("reserved"),
                    ["a slot this document never declared"] = JsonValue.Create(1),
                },
            };

            AgentSession revived = await agent.DeserializeSessionAsync(
                Envelope("conversation-42", stored), cancellationToken: TestContext.Current.CancellationToken);

            AgentResponse reply = await agent.RunAsync(
                "hello", revived, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("a reply", reply.Text);

            // The refused stage took IsComplete with it: a conversation brought back complete would turn every
            // turn away, which is the outcome the best-effort restore exists to avoid.
            Assert.False(revived.GetService<ConversationSession>()?.IsComplete);
        }

        [Fact]
        public async Task SerializeSessionAsync_CarriesNoWords()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);
            AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
            _ = await agent.RunAsync(
                "remember this sentence", session, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = session.GetService<ConversationSession>();
            Assert.NotNull(conversation);
            Assert.True(conversation.State.TryWrite("escalate", JsonValue.Create(true)));

            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: TestContext.Current.CancellationToken);
            string raw = serialized.GetRawText();

            // Something to lose first. On a document with no slots the whole blob is a handful of short
            // scalars, and an implementation that wrote {} would pass the two assertions below.
            Assert.Contains("escalate", raw, StringComparison.Ordinal);

            // Store 1 is durable already. A checkpoint that carried the words would give one conversation
            // two records and one chance to disagree.
            Assert.DoesNotContain("remember this sentence", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("a reply", raw, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DeserializeSessionAsync_WhenStore0KnowsTheConversation_Store0Wins()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("conversation-42", TestContext.Current.CancellationToken);

            // Store 0's own copy of this conversation: one word, and the state written in the same batch.
            _ = await store.AppendAsync(
                "conversation-42",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "an earlier turn"), "m0")],
                new ConversationSessionState
                {
                    Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                    {
                        ["escalate"] = JsonValue.Create(false),
                    },
                },
                TestContext.Current.CancellationToken);

            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml, store);

            // It disagrees with store 0 on one slot and carries a second store 0 knows nothing about.
            // The second is what a merge would leak: store 0 has no value to write over it.
            ConversationSessionState checkpoint = new()
            {
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["escalate"] = JsonValue.Create(true),
                    ["note"] = JsonValue.Create("from the checkpoint"),
                },
            };

            AgentSession revived = await agent.DeserializeSessionAsync(
                Envelope("conversation-42", checkpoint), cancellationToken: TestContext.Current.CancellationToken);

            _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = revived.GetService<ConversationSession>();
            Assert.NotNull(conversation);

            // False, which is store 0's value and not the checkpoint's. Store 0's blob rides the same
            // batch as the words above, so its state and store 1's words are of one moment; a
            // checkpoint's state beside those same words can be of two. One precedence, stated.
            Assert.False(conversation.State.Read("escalate")!.GetValue<bool>());

            // And store 0 wins outright rather than per slot. Where store 0 holds state, the checkpoint
            // contributes nothing at all — not even the slot store 0 never heard of.
            Assert.True(conversation.State.IsUnfilled("note"));
        }

        [Fact]
        public async Task DeserializeSessionAsync_WithNeitherMember_Throws()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("unused"), out _, SlottedAgentYaml);

            // The bare ConversationSessionState: the literal value store 0 keeps in conversation.state, and the other
            // of the two shapes in this system. Read as an envelope it names no conversation, and a new random
            // id would take the conversation's whole transcript with it.
            JsonElement bare = JsonSerializer.SerializeToElement(
                new ConversationSessionState { Stage = string.Empty }, ConversationStateJson.Options);

            ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(
                async () => await agent.DeserializeSessionAsync(
                    bare, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("{ conversationId, state }", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DeserializeSessionAsync_WithStateButNoConversationId_StartsANewConversation()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);

            // Lenient on purpose, and the remark promises it: state without an id is still state, and a
            // conversation that names none gets one made up, exactly as CreateSessionAsync does.
            JsonElement named = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["state"] = JsonSerializer.SerializeToNode(
                        new ConversationSessionState
                        {
                            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                            {
                                ["escalate"] = JsonValue.Create(true),
                            },
                        },
                        ConversationStateJson.Options),
                },
                ConversationStateJson.Options);

            AgentSession revived = await agent.DeserializeSessionAsync(
                named, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = revived.GetService<ConversationSession>();

            Assert.NotNull(conversation);
            Assert.NotEmpty(conversation.ConversationId);

            _ = await agent.RunAsync("hello", revived, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(conversation.State.Read("escalate")!.GetValue<bool>());
        }

        [Fact]
        public async Task ARoundTripOfATerminalConversation_ComesBackTerminalAndRefusesItsTurn()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, TerminalAgentYaml);
            AgentSession session = await agent.CreateSessionAsync("conversation-42", TestContext.Current.CancellationToken);

            _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(session.GetService<ConversationSession>()!.IsComplete);

            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: TestContext.Current.CancellationToken);
            AgentSession revived = await agent.DeserializeSessionAsync(
                serialized, cancellationToken: TestContext.Current.CancellationToken);

            // A behaviour change worth pinning rather than inferring: before the conversation state blob, a
            // reloaded page met a conversation that had forgotten it ended and answered one more turn.
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => agent.RunAsync("again", revived, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("runs no further turn", failure.Message, StringComparison.Ordinal);
            Assert.True(revived.GetService<ConversationSession>()!.IsComplete);
        }

        [Fact]
        public async Task Resume_AfterTheConversationHasOpened_ThrowsRatherThanDoingNothing()
        {
            AgentCoreAgent agent = BuildAgent(new SequencedChatClient("a reply"), out _, SlottedAgentYaml);
            AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
            _ = await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

            ConversationSession? conversation = session.GetService<ConversationSession>();
            Assert.NotNull(conversation);

            // The state is read as the session opens and never again, so a late hand-off would be a
            // silent no-op. Saying so is the whole reason the guard is here.
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => conversation.Resume(new ConversationSessionState()));

            Assert.Contains("already run a turn", failure.Message, StringComparison.Ordinal);
        }
    }
}

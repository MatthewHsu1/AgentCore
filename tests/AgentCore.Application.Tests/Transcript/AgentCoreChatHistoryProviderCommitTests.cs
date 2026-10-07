using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using static AgentCore.Application.Tests.Transcript.AgentCoreChatHistoryProviderTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// The provider as the only door to the message store: the framework's hook stages, and <c>CommitTurn</c> and
    /// <c>RewriteReply</c> are the durable writes.
    /// </summary>
    public sealed class AgentCoreChatHistoryProviderCommitTests
    {
        private const string LoopYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: coder, instructions: "fix bugs", todos: true, loop: { maxRounds: 3, until: [{ todos: {} }] } }
        entries:
          main:
            agent: coder
        """;

        private const string AutoApprovedYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email ]
              approval: { auto: [ send_email ] }
        entries:
          main:
            agent: only
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A loop: entry calls the hook once per iteration inside one
        // turn; staging accumulates and the commit writes all of it once. maxRounds: 3 with an open todo runs
        // three iterations (LoopCompilationTests), each a call, its result and a line.
        [Fact]
        public async Task CommitTurn_LoopEntry_StagesEveryIteration_AndWritesThemInOneAppend()
        {
            RecordingConversationStore store = new();
            ToolCallingChatClient client = new(
                "added",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["todos"] = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "T" } },
                },
                everyRun: true);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(LoopYaml),
                new AgentCompilationContext(new FakeChatClientFactory(client)) { ConversationStore = store })["main"];
            AIAgent agent = compiled.Agents["coder"];
            AgentSession session = await OpenAsync(compiled, agent, store);

            _ = await agent.RunAsync("fix it", session, cancellationToken: Ct);
            IReadOnlyList<ChatMessage> staged = compiled.History.Staged(session);
            _ = compiled.History.CommitTurn(session, new TurnCommit(new ChatMessage(ChatRole.User, "fix it")));
            await compiled.History.DrainAsync(session);

            string[] iteration = ["assistant:call", "tool:result", "assistant:text"];
            Assert.Equal([.. iteration, .. iteration, .. iteration], Kinds(staged));
            Assert.Equal(1, store.Appends);
            Assert.Equal(["user:text", .. iteration, .. iteration, .. iteration], Kinds(store.Rows.Select(row => row.Content)));
            Assert.Empty(compiled.History.Staged(session));
        }

        // An approval re-entry runs the agent again inside the same
        // turn. The first run ends on the approval request, the second runs the tool and answers "done.".
        [Fact]
        public async Task CommitTurn_ApprovalReEntry_StagesBothRuns_AndWritesThemInOneAppend()
        {
            RecordingConversationStore store = new();
            ToolCallingChatClient client = new("done.", new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" });
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(AutoApprovedYaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new FakeChatClientFactory(client))
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(document, declared => declared.Uses == "test.send" ? SendEmail() : null, Ct),
                })["main"];
            AIAgent agent = compiled.Agents["only"];
            AgentSession session = await OpenAsync(compiled, agent, store);

            _ = await agent.RunAsync("send it", session, cancellationToken: Ct);
            IReadOnlyList<ChatMessage> staged = compiled.History.Staged(session);
            _ = compiled.History.CommitTurn(session, new TurnCommit(new ChatMessage(ChatRole.User, "send it")));
            await compiled.History.DrainAsync(session);

            Assert.Contains(staged.SelectMany(message => message.Contents), content => content is ToolApprovalRequestContent);
            Assert.Equal("done.", staged[^1].Text);
            Assert.Equal(1, store.Appends);
            Assert.Equal(staged.Count + 1, store.Rows.Count);
            Assert.Equal("done.", store.Rows[^1].Content.Text);
        }

        // An AIContextProvider's system line reaches the model, and never the committed turn.
        [Fact]
        public async Task CommitTurn_ContextProviderSystemLine_ReachesTheModelButNotTheTranscript()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession _) = await NewConversation();
            RequestRecordingChatClient model = new("x");
            ChatClientAgent agent = new(
                model,
                new ChatClientAgentOptions { ChatHistoryProvider = provider, AIContextProviders = [new SystemLineProvider()] });
            AgentSession session = await agent.CreateSessionAsync(Ct);
            _ = provider.BeginConversation(session, ConversationId, []);
            provider.BeginTurn(session, 0);

            _ = await agent.RunAsync("hi", session, cancellationToken: Ct);
            _ = provider.CommitTurn(session, new TurnCommit(new ChatMessage(ChatRole.User, "hi")));
            await provider.DrainAsync(session);

            Assert.Contains("system:Today is Tuesday.", model.Requests[0]);
            Assert.Equal(["user:hi", "assistant:x"], store.Rows.Select(row => $"{row.Content.Role}:{row.Content.Text}"));
        }

        // A background child carries the parent's TurnRegistry entry and BlobOwnerKey, but not the
        // provider's own key, so it stages nothing and commits nothing.
        [Fact]
        public async Task BackgroundChild_WithoutTheProviderKey_StagesAndCommitsNothing()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession _) = await NewConversation();
            using SequencedChatClient model = new("child words");
            BackgroundChildAgent child = new(new ChatClientAgent(model, new ChatClientAgentOptions { ChatHistoryProvider = provider }));
            TurnInvocation parent = new() { ConversationId = "parent-conv", TurnIndex = 0, Stage = string.Empty };
            AgentSession? childSession = null;
            Outer outer = new(async () =>
            {
                childSession = await child.CreateSessionAsync(Ct);
                _ = await child.RunAsync("do it", childSession, cancellationToken: Ct);
            });

            _ = await outer.RunAsync("go", options: parent.RunOptions(), cancellationToken: Ct);
            TurnWrite? committed = provider.CommitTurn(childSession!, new TurnCommit(new ChatMessage(ChatRole.User, "do it")));
            await provider.DrainAsync(childSession!);

            Assert.NotNull(TurnRegistry.For(childSession));
            Assert.True(childSession!.StateBag.TryGetValue(BlobOwnerKey.Value, out string? _));
            Assert.False(childSession.StateBag.TryGetValue(StateKey, out string? _));
            Assert.Empty(provider.Staged(childSession));
            Assert.Null(committed);
            Assert.Empty(store.Rows);
        }

        // A cut mid-text keeps the user, the finished tool pairs, and exactly the shown text.
        [Fact]
        public async Task CommitTurn_CutMidText_KeepsTheFinishedPairAndTheShownText()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession session) = await NewConversation();
            provider.BeginTurn(session, 0);
            AgentResponse seen = new(
            [
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "42")]),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "lookup")]),
                new ChatMessage(ChatRole.Assistant, "Hello there"),
            ]);

            _ = provider.CommitTurn(
                session,
                new TurnCommit(new ChatMessage(ChatRole.User, "hi")) { Seen = seen, Cut = new TurnCut("Hel", TimeSpan.FromMilliseconds(300)) });
            await provider.DrainAsync(session);

            Assert.Equal(["user:text", "assistant:call", "tool:result", "assistant:text"], Kinds(store.Rows.Select(row => row.Content)));
            Assert.Equal("Hel", store.Rows[^1].Content.Text);
        }

        // A sealed turn is rewritten, never appended to; an older turn answers false.
        [Fact]
        public async Task RewriteReply_OnlyTheLastTurnIsRewritten_AndNothingIsAppended()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession session) = await NewConversation();
            AppendTurn(provider, session, turnIndex: 0, "one", "first");
            AppendTurn(provider, session, turnIndex: 1, "two", "second reply");

            bool older = provider.RewriteReply(session, 0, "fir");
            bool last = provider.RewriteReply(session, 1, "second");
            await provider.DrainAsync(session);

            Assert.False(older);
            Assert.True(last);
            Assert.Equal(2, store.Appends);
            Assert.Equal(["one", "first", "two", "second"], store.Live(ConversationId).Select(row => row.Content.Text));
        }

        // CommitTurn: the reply id is null when the user's message is all the turn wrote.
        [Fact]
        public async Task CommitTurn_CutBeforeAnyWord_NamesTheUserMessageAndNoReply()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession session) = await NewConversation();
            provider.BeginTurn(session, 0);

            (string UserMessageId, string? ReplyMessageId)? ids = provider.CommitTurn(
                session, new TurnCommit(new ChatMessage(ChatRole.User, "hi")) { Cut = new TurnCut(string.Empty, null) })?.Ids;
            await provider.DrainAsync(session);

            ConversationMessage only = Assert.Single(store.Rows);
            Assert.Equal((only.MessageId, null), ids);
        }

        private static async Task<AgentSession> OpenAsync(CompiledAgent compiled, AIAgent agent, RecordingConversationStore store)
        {
            _ = await store.CreateAsync(ConversationId, Ct);
            AgentSession session = await agent.CreateSessionAsync(Ct);
            _ = compiled.History.BeginConversation(session, ConversationId, []);
            compiled.History.BeginTurn(session, 0);
            return session;
        }

        private static List<string> Kinds(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Select(message => $"{message.Role}:{Kind(message)}")];
        }

        private static string Kind(ChatMessage message)
        {
            return message.Contents.Any(content => content is FunctionCallContent) ? "call"
                : message.Contents.Any(content => content is FunctionResultContent) ? "result"
                : "text";
        }

        private static ApprovalRequiredAIFunction SendEmail()
        {
            return new(AIFunctionFactory.Create((string to) => "sent", "send_email", "Send an email."));
        }

        private sealed class SystemLineProvider : AIContextProvider
        {
            protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
            {
                return new(new AIContext { Messages = [new ChatMessage(ChatRole.System, "Today is Tuesday.")] });
            }
        }

        /// <summary>Runs a body inside a run of its own, so the framework's current run context is the parent's.</summary>
        private sealed class Outer(Func<Task> body) : AIAgent
        {
            protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
                AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
                JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override async Task<AgentResponse> RunCoreAsync(
                IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            {
                await body();
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            }

            protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
                IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}

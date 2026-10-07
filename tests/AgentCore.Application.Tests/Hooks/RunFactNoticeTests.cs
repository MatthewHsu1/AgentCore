using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class RunFactNoticeTests : IDisposable
    {
        private const string KnowledgeYaml = """
        apiVersion: agentcore/v1
        providers:
          conversation: { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok", knowledge: { mode: prefetch, scoped: false } }
        entries:
          main:
            agent: only
        """;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-hook-publish-" + Guid.NewGuid().ToString("N"));

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public async Task APrefetchSearchIsNamedWithItsHits()
        {
            RecordingHook hook = new();
            StubKnowledgePort port = new([
                new KnowledgeCard { CardId = "a", Text = "belt tension", ViaLink = false },
                new KnowledgeCard { CardId = "b", Text = "belt slip", ViaLink = false },
            ]);
            ConversationSession session = CreateWithKnowledge(hook, port);

            _ = await session.RunTurnAsync("my belt slips", Ct);
            await session.FlushNoticesAsync();

            KnowledgeSearched searched = Assert.Single(hook.Of<KnowledgeSearched>());
            Assert.Equal(2, searched.Hits);
            Assert.Null(searched.Failure);
            Assert.Equal(0, searched.Scope.TurnIndex);
        }

        [Fact]
        public async Task AFailedSearchIsNamedWithItsFailure()
        {
            RecordingHook hook = new();
            ConversationSession session = CreateWithKnowledge(hook, new ThrowingKnowledgePort(new InvalidOperationException("kb down")));

            _ = await session.RunTurnAsync("my belt slips", Ct);
            await session.FlushNoticesAsync();

            KnowledgeSearched searched = Assert.Single(hook.Of<KnowledgeSearched>());
            Assert.Equal(0, searched.Hits);
            Assert.Contains("kb down", searched.Failure, StringComparison.Ordinal);
        }

        // One search, one notice: a search the store answered that fails afterwards is named once, as failed.
        [Fact]
        public async Task ASearchThatFailsAfterTheStoreAnsweredIsNamedOnceAsFailed()
        {
            RecordingHook hook = new();
            StubKnowledgePort port = new([new KnowledgeCard { CardId = "a", Text = "belt tension", ViaLink = false }]);
            ConversationSession session = CreateWithKnowledge(
                hook, port, KnowledgeYaml.Replace("scoped: false", "scoped: false, citations: true", StringComparison.Ordinal), new ThrowingCitations());

            _ = await session.RunTurnAsync("my belt slips", Ct);
            await session.FlushNoticesAsync();

            KnowledgeSearched searched = Assert.Single(hook.Of<KnowledgeSearched>());
            Assert.Contains("no label", searched.Failure, StringComparison.Ordinal);
        }

        // An agent-as-tool run begins and ends inside the parent's turn.
        [Fact]
        public async Task AnAgentToolRunIsNamedWhenItStartsAndEnds()
        {
            RecordingHook hook = new();
            ToolCallingChatClient model = new("done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "help me" });
            ConversationSession session = HookSessions.Create(HookSessions.DelegatingYaml, model, [hook]);

            _ = await session.RunTurnAsync("go", Ct);
            await session.FlushNoticesAsync();

            SubagentStarted started = Assert.Single(hook.Of<SubagentStarted>());
            SubagentEnded ended = Assert.Single(hook.Of<SubagentEnded>());
            Assert.Equal("helper", started.AgentId);
            Assert.NotNull(started.ParentToolCallId);
            Assert.Equal(started.ParentToolCallId, ended.ParentToolCallId);
            Assert.Equal(SubagentOutcome.Answered, ended.Outcome);
            Assert.True(started.Scope.Sequence < ended.Scope.Sequence);
        }

        // The publish fixture copies FilePublishToolDefinitionTests.cs:23-28,188-208.
        [Fact]
        public async Task APublishedFileIsNamedWithItsBlobKey()
        {
            RecordingHook hook = new();
            HookRuntime runtime = HookRuntime.Create([hook], loggers: null);
            SessionHooks hooks = new(runtime, "conversation-1", "main", TimeProvider.System);
            _ = Directory.CreateDirectory(Path.Combine(_root, "conversation-1"));
            File.WriteAllText(Path.Combine(_root, "conversation-1", "rows.csv"), "a,b\n1,2\n");
            ToolConfiguration declared = new() { Id = "publish", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.FilePublish, Description = "Publish a file." };
            AIFunction function = (AIFunction)new FilePublishToolDefinition().Build(
                declared, new BuiltinToolPorts(ChatClients: null, Blobs: new RecordingBlobStore(), WorkspaceRoot: _root));
            AIFunctionArguments arguments = new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "rows.csv" });
            _ = new TurnInvocation
            {
                ConversationId = "conversation-1",
                TurnIndex = 0,
                Stage = string.Empty,
                Workspace = Path.Combine(_root, "conversation-1"),
                Hooks = hooks,
            }.FileIn(arguments);

            _ = await function.InvokeAsync(arguments, Ct);
            await hooks.FlushAsync();

            FilePublished published = Assert.Single(hook.Of<FilePublished>());
            Assert.Equal(("rows.csv", 8L, "text/csv", "conversation-1/rows.csv"), (published.Name, published.Length, published.MediaType, published.BlobKey));
        }

        private static ConversationSession CreateWithKnowledge(
            RecordingHook hook, Ports.IKnowledgeRetrievalPort port, string yaml = KnowledgeYaml, IKnowledgeCitationFormatter? citations = null)
        {
            ScriptedChatClient reply = new("an answer");
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port, Hooks = [hook], Citations = citations })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create();
        }

        private sealed class ThrowingCitations : IKnowledgeCitationFormatter
        {
            public string Name => "throwing";

            public string? Format(KnowledgeCard card)
            {
                throw new InvalidOperationException("no label");
            }
        }
    }
}

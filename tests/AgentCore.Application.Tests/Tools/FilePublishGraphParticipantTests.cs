using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Tools
{
    /// <summary>
    /// A file any agent of the turn publishes is the caller's. A graph participant files its cards in a collector of
    /// its own and posts them to the caller as its run goes, around the workflow, so a card reaches the caller even
    /// from a participant that does not speak. An agent-as-tool child's card rides its parent's call.
    /// </summary>
    public sealed class FilePublishGraphParticipantTests : IDisposable
    {
        private const string SequentialYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: publish, kind: builtin, uses: file.publish, description: "Publish a file." }
        agents:
          defaults: { clock: false }
          items:
            - { id: publisher, instructions: "publish the rows", model: { ref: publisher }, tools: [ publish ], todos: true }
            - { id: responder, instructions: "answer", model: { ref: responder } }
        entries:
          main:
            graph:
              pattern: sequential
              agents: [ publisher, responder ]
        """;

        private const string ConcurrentYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: publish, kind: builtin, uses: file.publish, description: "Publish a file." }
        agents:
          defaults: { clock: false }
          items:
            - { id: a, instructions: "publish a", model: { ref: a }, tools: [ publish ] }
            - { id: b, instructions: "publish b", model: { ref: b }, tools: [ publish ] }
        entries:
          main:
            graph:
              pattern: concurrent
              agents: [ a, b ]
        """;

        private const string DelegatingYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: publish, kind: builtin, uses: file.publish, description: "Publish a file." }
          - { id: ask_helper, kind: agent, agent: helper, description: "Ask the helper." }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "delegate", tools: [ ask_helper ] }
            - { id: helper, instructions: "publish the rows", tools: [ publish ] }
        entries:
          main:
            agent: only
        """;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-graph-publish-" + Guid.NewGuid().ToString("N"));

        private readonly RecordingBlobStore _blobs = new();

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // The publisher does not speak (a sequential row answers from its last agent): its file still reaches the
        // caller, while its words and its tool call and result do not. todos: makes the row keep its workflow session,
        // so the turn also ends by serializing that session and every participant's: none may hold the file.
        [Fact]
        public async Task AFilePublishedByAParticipantThatDoesNotSpeakReachesTheCallerAlone()
        {
            RoutingChatClientFactory models = new();
            _ = models.Route("publisher", new ToolCallingChatClient("publisher words", PathArgument("rows.csv")));
            _ = models.Route("responder", new ScriptedChatClient("responder words"));
            RecordingHook notices = new();
            ConversationSession session = Create(SequentialYaml, models, notices);

            List<ChatResponseUpdate> updates = await StreamAsync(session);
            await session.FlushNoticesAsync();

            Assert.Equal(["rows.csv"], updates.SelectMany(static update => update.Contents.OfType<FileContent>()).Select(static file => file.Name));
            Assert.Equal("responder words", string.Concat(updates.Select(static update => update.Text)));
            Assert.DoesNotContain(updates, static update => update.Contents.Any(static content => content is FunctionCallContent or FunctionResultContent));
            Assert.Equal([("conversation-1", "rows.csv")], _blobs.Blobs.Keys);
            _ = Assert.Single(notices.Of<FilePublished>());
        }

        // The stored shape of a graph turn is the words the caller was shown; the file card the caller was handed
        // is part of that, as it is for a single agent's turn: the stored list names the file, and the card is
        // still in the stored messages after a reload.
        [Fact]
        public async Task AFilePublishedInAGraphTurnIsInTheStoredFileListAndItsCardSurvivesAReload()
        {
            RoutingChatClientFactory models = new();
            _ = models.Route("publisher", new ToolCallingChatClient("publisher words", PathArgument("rows.csv")));
            _ = models.Route("responder", new ScriptedChatClient("responder words"));
            InMemoryConversationStore store = new();
            ConversationSession session = Create(SequentialYaml, models, new RecordingHook(), store: store);

            _ = await StreamAsync(session);
            await session.FlushTranscriptAsync();

            StoredConversation? stored = await new Conversations(store, _blobs)
                .LoadWindowAsync("conversation-1", new TranscriptWindow(null, 10), Ct);

            Assert.NotNull(stored);
            Assert.Equal(["rows.csv"], stored.Files.Select(static file => file.Name));
            Assert.Equal(
                ["rows.csv"],
                stored.Messages.SelectMany(static message => message.Content.Contents).OfType<FileContent>().Select(static file => file.Name));
            Assert.Equal("responder words", stored.Messages[^1].Content.Text);
        }

        [Fact]
        public async Task AFilePublishedByAnAgentToolChildReachesTheCallerOnce()
        {
            ScriptedToolCallingChatClient model = new(("ask_helper", """{ "query": "publish the rows" }"""), ("publish", """{ "path": "rows.csv" }"""))
            {
                FinalText = "published",
            };
            RecordingHook notices = new();
            ConversationSession session = Create(DelegatingYaml, new RoutingChatClientFactory(model), notices);

            List<ChatResponseUpdate> updates = await StreamAsync(session);
            await session.FlushNoticesAsync();

            Assert.Equal(["rows.csv"], updates.SelectMany(static update => update.Contents.OfType<FileContent>()).Select(static file => file.Name));
            Assert.Equal([("conversation-1", "rows.csv")], _blobs.Blobs.Keys);
            _ = Assert.Single(notices.Of<FilePublished>());
        }

        // Two participants publish while both calls are in flight. Each card must name its own participant.
        [Fact(Timeout = 60_000)]
        public async Task ConcurrentParticipantsEachGetTheirOwnFileEveryTime()
        {
            for (int run = 0; run < 10; run++)
            {
                RoutingChatClientFactory models = new();
                _ = models.Route("a", new ToolCallingChatClient("a done", PathArgument("a.csv")));
                _ = models.Route("b", new ToolCallingChatClient("b done", PathArgument("b.csv")));
                RecordingHook notices = new();
                ConversationSession session = Create(ConcurrentYaml, models, notices, meetingOf: 2, $"conversation-{run}");

                List<ChatResponseUpdate> updates = await StreamAsync(session);
                await session.FlushNoticesAsync();

                Assert.Equal(TurnOutcome.Answered, Assert.Single(notices.Of<TurnCompleted>()).Outcome);

                Assert.Equal(
                    [("a", "a.csv"), ("b", "b.csv")],
                    updates
                        .SelectMany(static update => update.Contents.OfType<FileContent>().Select(file => (update.AuthorName, file.Name)))
                        .OrderBy(static card => card.AuthorName, StringComparer.Ordinal));
            }
        }

        private static Dictionary<string, object?> PathArgument(string path)
        {
            return new(StringComparer.Ordinal) { ["path"] = path };
        }

        private ConversationSession Create(
            string yaml, RoutingChatClientFactory models, RecordingHook notices, int meetingOf = 0, string conversationId = "conversation-1", IConversationStore? store = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(models)
                {
                    Tools = TestToolRegistry.From(document, Publish(meetingOf), Ct),
                    Hooks = [notices],
                    ConversationStore = store,
                })["main"];

            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root)
                .Create(conversationId);
            foreach (string name in (string[])["rows.csv", "a.csv", "b.csv"])
            {
                File.WriteAllText(Path.Combine(session.Workspace!, name), "a,b\n1,2\n");
            }

            return session;
        }

        /// <summary>Builds the real <c>file.publish</c>, held until <paramref name="meetingOf"/> calls are in flight when it is above 0.</summary>
        private Func<ToolConfiguration, AITool?> Publish(int meetingOf)
        {
            return tool =>
            {
                if (tool.Kind != ToolKind.Builtin)
                {
                    return null;
                }

                AIFunction publish = (AIFunction)new FilePublishToolDefinition().Build(
                    tool, new BuiltinToolPorts(ChatClients: null, Blobs: _blobs, WorkspaceRoot: _root));
                return meetingOf == 0 ? publish : new MeetingFunction(publish, meetingOf);
            };
        }

        private static async Task<List<ChatResponseUpdate>> StreamAsync(ConversationSession session)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("publish it", Ct))
            {
                updates.Add(update);
            }

            return updates;
        }
    }
}

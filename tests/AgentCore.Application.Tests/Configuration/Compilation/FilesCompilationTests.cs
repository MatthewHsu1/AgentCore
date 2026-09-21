using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation
{
    /// <summary>
    /// The <c>files:</c> block reaching a compiled agent as a <see cref="FileAccessProvider"/>, the
    /// <c>file_access_*</c> tools it puts in front of the model, and the conversation folder it scopes to.
    /// </summary>
#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class FilesCompilationTests : IDisposable
    {
        private static readonly string[] ExpectedFilesTools =
        [
            "file_access_read",
            "file_access_read_lines",
            "file_access_ls",
            "file_access_grep",
            "file_access_write",
            "file_access_delete",
            "file_access_replace",
            "file_access_replace_lines",
        ];

        private static readonly string[] ExpectedReadOnlyFilesTools =
        [
            "file_access_read",
            "file_access_read_lines",
            "file_access_ls",
            "file_access_grep",
        ];

        private const string FilesYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "work with files", files: { store: workspace } }
        entries:
          main:
            agent: only
        """;

        private const string ReadOnlyFilesYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "read files", files: { store: workspace, write: false } }
        entries:
          main:
            agent: only
        """;

        private const string NoFilesYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "no files here" }
        entries:
          main:
            agent: only
        """;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "agentcore-files-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public async Task Compile_FilesWorkspaceWithARoot_GetsAFileAccessProviderAndTheEightTools()
        {
            using SequencedChatClient reply = new("hello there.");
            AIAgent agent = CompileOne(FilesYaml, reply, _root);
            Assert.Contains(Providers(agent), provider => provider is ConversationFilesProvider);

            ConversationSessionFactory factory = BuildFactory(FilesYaml, _root, reply);
            ConversationSession session = factory.Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            AITool[] tools = reply.Options[^1]?.Tools?.OfType<AITool>().ToArray() ?? [];
            AssertSameNames(ExpectedFilesTools, [.. tools.Select(tool => tool.Name)]);
            Assert.True(
                tools.All(tool => tool is not ApprovalRequiredAIFunction),
                "expected no file_access_* tool to require approval");
        }

        [Fact]
        public async Task Compile_FilesWorkspaceWithARoot_TheModelsInstructionsAreTruthfulAboutDeletion()
        {
            using SequencedChatClient reply = new("hello there.");
            ConversationSessionFactory factory = BuildFactory(FilesYaml, _root, reply);
            ConversationSession session = factory.Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string instructions = reply.Options[^1]?.Instructions ?? string.Empty;
            Assert.Contains("deleted when the conversation ends", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("persist beyond", instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Compile_FilesWorkspaceWriteFalse_GetsOnlyTheFourReadOnlyTools()
        {
            using SequencedChatClient reply = new("hello there.");
            ConversationSessionFactory factory = BuildFactory(ReadOnlyFilesYaml, _root, reply);
            ConversationSession session = factory.Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string[] toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            AssertSameNames(ExpectedReadOnlyFilesTools, toolNames);
        }

        [Fact]
        public async Task Compile_NoFilesBlock_GetsNoFileAccessProviderAndNoTool()
        {
            using SequencedChatClient reply = new("hello there.");

            AIAgent agent = CompileOne(NoFilesYaml, reply, _root);
            Assert.DoesNotContain(Providers(agent), provider => provider is FileAccessProvider);

            CancellationToken token = TestContext.Current.CancellationToken;
            AgentSession session = await agent.CreateSessionAsync(token);
            _ = await agent.RunAsync("hi", session, cancellationToken: token);

            string[] toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            Assert.DoesNotContain(toolNames, name => name.StartsWith("file_access_", StringComparison.Ordinal));
        }

        [Fact]
        public void Compile_FilesWorkspaceWithNoRoot_FailsNamingTheFilesPointer()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(FilesYaml);

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi"))))["main"]);

            Assert.Equal("/agents/items/0/files", failure.Pointer);
            Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task EndToEnd_WriteReadLsGrep_ThenEndConversationDeletesTheFolder()
        {
            SequencedToolCallClient client = new(
                (FileAccessProvider.WriteToolName, Args(("fileName", "out.txt"), ("content", "hello"))),
                (FileAccessProvider.ReadFileToolName, Args(("fileName", "out.txt"))),
                (FileAccessProvider.LsToolName, Args()),
                (FileAccessProvider.GrepToolName, Args(("regexPattern", "hell"))));

            ConversationSessionFactory factory = BuildFactory(FilesYaml, _root, client);
            ConversationSession session = factory.Create("conversation-1");
            string conversationFolder = session.Workspace!;

            _ = await session.RunTurnAsync("write hello to out.txt", TestContext.Current.CancellationToken);
            Assert.Equal("hello", await File.ReadAllTextAsync(
                Path.Combine(conversationFolder, "out.txt"), TestContext.Current.CancellationToken));

            _ = await session.RunTurnAsync("read out.txt", TestContext.Current.CancellationToken);
            Assert.Contains("hello", client.ToolResults[1]);

            _ = await session.RunTurnAsync("list the folder", TestContext.Current.CancellationToken);
            Assert.Contains("out.txt", client.ToolResults[2]);
            Assert.DoesNotContain("conversation-1", client.ToolResults[2], StringComparison.Ordinal);

            _ = await session.RunTurnAsync("grep for hell", TestContext.Current.CancellationToken);
            Assert.Contains("out.txt", client.ToolResults[3]);

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            Assert.False(Directory.Exists(conversationFolder));
        }

        [Fact]
        public async Task Escape_WriteOutsideTheConversationFolder_CreatesNothingOutsideItAndDoesNotThrow()
        {
            SequencedToolCallClient client = new(
                (FileAccessProvider.WriteToolName, Args(("fileName", "../escape.txt"), ("content", "leaked"))));

            ConversationSessionFactory factory = BuildFactory(FilesYaml, _root, client);
            ConversationSession session = factory.Create("conversation-1");

            // The point of the assertion is that this completes at all: an escape attempt must not
            // throw out of RunTurnAsync.
            _ = await session.RunTurnAsync("escape", TestContext.Current.CancellationToken);

            // The real string, not the probe's "Error: Function failed." (that string is what a store
            // exception looks like when it escapes uncaught, e.g. the missing-ambient case; a rejected
            // path is instead refused before it throws, as a JSON error object naming the tool.)
            Assert.Contains("\"error\": true", client.ToolResults[0], StringComparison.Ordinal);
            Assert.Contains("file_access_write", client.ToolResults[0], StringComparison.Ordinal);

            List<string> escaped = Directory.Exists(_root)
                ? [.. Directory.EnumerateFiles(_root, "escape.txt", SearchOption.AllDirectories)]
                : [];
            Assert.Empty(escaped);
        }

        [Fact]
        public async Task TwoSessions_DifferentConversationIds_SeeOnlyTheirOwnFile()
        {
            SequencedToolCallClient clientA = new(
                (FileAccessProvider.WriteToolName, Args(("fileName", "out.txt"), ("content", "A"))));
            SequencedToolCallClient clientB = new(
                (FileAccessProvider.WriteToolName, Args(("fileName", "out.txt"), ("content", "B"))));

            ConversationSessionFactory factoryA = BuildFactory(FilesYaml, _root, clientA);
            ConversationSession sessionA = factoryA.Create("conversation-a");
            _ = await sessionA.RunTurnAsync("write A", TestContext.Current.CancellationToken);

            ConversationSessionFactory factoryB = BuildFactory(FilesYaml, _root, clientB);
            ConversationSession sessionB = factoryB.Create("conversation-b");
            _ = await sessionB.RunTurnAsync("write B", TestContext.Current.CancellationToken);

            string pathA = Path.Combine(sessionA.Workspace!, "out.txt");
            string pathB = Path.Combine(sessionB.Workspace!, "out.txt");

            Assert.Equal("A", await File.ReadAllTextAsync(pathA, TestContext.Current.CancellationToken));
            Assert.Equal("B", await File.ReadAllTextAsync(pathB, TestContext.Current.CancellationToken));
        }

        private static void AssertSameNames(string[] expected, string[] actual)
        {
            string[] expectedSorted = [.. expected.OrderBy(n => n, StringComparer.Ordinal)];
            string[] actualSorted = [.. actual.OrderBy(n => n, StringComparer.Ordinal)];
            Assert.Equal(expectedSorted, actualSorted, StringComparer.Ordinal);
        }

        private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
        {
            Dictionary<string, object?> args = new(StringComparer.Ordinal);
            foreach ((string? key, object? value) in pairs)
            {
                args[key] = value;
            }

            return args;
        }

        private static ConversationSessionFactory BuildFactory(string yaml, string root, IChatClient? client = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(client ?? new SequencedChatClient("done"));

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { WorkspaceRoot = root })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                workspaceRoot: root);
        }

        private static AIAgent CompileOne(string yaml, SequencedChatClient reply, string root)
        {
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(new FakeChatClientFactory(reply)) { WorkspaceRoot = root })["main"];

            return Assert.Single(compiled.Agents.Values);
        }

        private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
        {
            ChatClientAgent? inner = agent.GetService<ChatClientAgent>();
            Assert.NotNull(inner);
            return inner.AIContextProviders ?? [];
        }

        /// <summary>
        /// A deterministic offline model that plays a queue of specific named tool calls, one per turn:
        /// it calls the next queued tool when the last message is a fresh user turn, and replies with
        /// text once the framework has fed the tool's result back mid-turn.
        /// </summary>
        private sealed class SequencedToolCallClient(params (string Name, Dictionary<string, object?> Args)[] steps) : IChatClient
        {
            private readonly Queue<(string Name, Dictionary<string, object?> Args)> _steps = new(steps);

            /// <summary>Gets the text of each tool result this client was fed back, in call order.</summary>
            public List<string> ToolResults { get; } = [];

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                List<ChatMessage> transcript = [.. messages];
                string responseId = Guid.NewGuid().ToString("N");

                List<FunctionResultContent> lastResults = transcript.Count > 0
                    ? [.. transcript[^1].Contents.OfType<FunctionResultContent>()]
                    : [];

                foreach (FunctionResultContent? result in lastResults)
                {
                    lock (ToolResults)
                    {
                        ToolResults.Add(result.Result?.ToString() ?? string.Empty);
                    }
                }

                if (lastResults.Count == 0 && _steps.Count > 0)
                {
                    (string? name, Dictionary<string, object?>? args) = _steps.Dequeue();

                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent("conversation_" + Guid.NewGuid().ToString("N"), name, args)])
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                    };

                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    updates.Add(update);
                }

                return updates.ToChatResponse();
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                ArgumentNullException.ThrowIfNull(serviceType);
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
                // Nothing to release.
            }
        }
    }
#pragma warning restore MAAI001
}

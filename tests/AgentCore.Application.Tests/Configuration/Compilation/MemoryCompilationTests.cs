using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Configuration.Compilation
{
    /// <summary>
    /// The <c>memory:</c> block reaching a compiled agent as a <see cref="FileMemoryProvider"/>, the
    /// seven <c>file_memory_*</c> tools it puts in front of the model, and the working folder it binds
    /// to the running conversation.
    /// </summary>
    public sealed class MemoryCompilationTests : IDisposable
    {
        private static readonly string[] ExpectedMemoryTools =
        [
            "file_memory_write",
            "file_memory_read",
            "file_memory_delete",
            "file_memory_ls",
            "file_memory_grep",
            "file_memory_replace",
            "file_memory_replace_lines",
        ];

        private const string MemoryYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "remember things", memory: { store: workspace } }
        entries:
          main:
            agent: only
        """;

        private const string NoMemoryYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "remember nothing" }
        entries:
          main:
            agent: only
        """;

        private const string MemoryPolicyYaml =
            """
          apiVersion: agentcore/v1
          state:
            done:
              type: boolean
              default: false
              writer: const
              value: false
          guards:
            isDone: { var: done }
          agents:
            items:
              - { id: only, instructions: "remember things", memory: { store: workspace } }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - id: working
                    agent: only
                    to: [ { stage: finished, when: isDone } ]
                  - id: finished
                    agent: only
                    terminal: true
          """;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "agentcore-memory-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public async Task Compile_MemoryWorkspaceWithARoot_GetsAFileMemoryProviderAndTheSevenTools()
        {
            using SequencedChatClient reply = new("hello there.");
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(MemoryYaml);
            RoutingChatClientFactory chatClients = new(reply);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { WorkspaceRoot = _root })["main"];
            AIAgent agent = Assert.Single(compiled.Agents.Values);
            Assert.Contains(Providers(agent), provider => provider is FileMemoryProvider);

            ConversationSessionFactory factory = new(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
            ConversationSession session = factory.Create("conversation-1");

            // A turn must be open for the FileMemoryProvider's state initializer to read the conversation id off
            // the session, so the tool list is read through a real conversation rather than agent.RunAsync
            // directly, unlike the todos/mode providers in HarnessCompilationTests.
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string[] toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            Assert.Equal(ExpectedMemoryTools, toolNames, StringComparer.Ordinal);
        }

        [Fact]
        public async Task Compile_MemoryWorkspaceWithARoot_TheModelsInstructionsAreTruthfulAboutDeletion()
        {
            using SequencedChatClient reply = new("hello there.");
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(MemoryYaml);
            RoutingChatClientFactory chatClients = new(reply);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { WorkspaceRoot = _root })["main"];
            ConversationSessionFactory factory = new(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
            ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string instructions = reply.Options[^1]?.Instructions ?? string.Empty;
            Assert.Contains("deleted when the conversation ends", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("persist beyond", instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Compile_NoMemoryBlock_GetsNoFileMemoryProviderAndNoTool()
        {
            using SequencedChatClient reply = new("hello there.");

            AIAgent agent = CompileOne(NoMemoryYaml, reply, _root);
            Assert.DoesNotContain(Providers(agent), provider => provider is FileMemoryProvider);

            CancellationToken token = TestContext.Current.CancellationToken;
            AgentSession session = await agent.CreateSessionAsync(token);
            _ = await agent.RunAsync("hi", session, cancellationToken: token);

            string[] toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            Assert.DoesNotContain(toolNames, name => name.StartsWith("file_memory_", StringComparison.Ordinal));
        }

        [Fact]
        public void Compile_MemoryWorkspaceWithNoRoot_FailsNamingTheMemoryPointer()
        {
            using SequencedChatClient reply = new("hello there.");
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(MemoryYaml);

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"]);

            Assert.Equal("/agents/items/0/memory", failure.Pointer);
            Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task EndToEnd_TheModelWritesAFile_ItLandsUnderTheConversationsFolder_AndIsGoneWhenTheConversationEnds()
        {
            ConversationSessionFactory factory = BuildFactory(MemoryYaml, _root);
            ConversationSession session = factory.Create("conversation-1");
            string conversationFolder = session.Workspace!;

            _ = await session.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

            string[] notes = FindRecursive(conversationFolder, "notes.md");
            Assert.NotEmpty(notes);
            Assert.Contains("hello", await File.ReadAllTextAsync(notes[0], TestContext.Current.CancellationToken));

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);

            Assert.False(Directory.Exists(conversationFolder));
        }

        [Fact]
        public async Task TwoSessions_DifferentConversationIds_WriteToDifferentFolders()
        {
            ConversationSessionFactory factory = BuildFactory(MemoryYaml, _root);

            ConversationSession first = factory.Create("conversation-a");
            _ = await first.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

            ConversationSession second = factory.Create("conversation-b");
            _ = await second.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

            string[] firstNotes = FindRecursive(first.Workspace!, "notes.md");
            string[] secondNotes = FindRecursive(second.Workspace!, "notes.md");

            Assert.NotEmpty(firstNotes);
            Assert.NotEmpty(secondNotes);
            Assert.DoesNotContain(firstNotes[0], secondNotes);
        }

        [Fact]
        public async Task MidTurnEndConversation_DuringAFileMemoryWrite_StillDeletesTheFolder_EvenWhenThePolicyIsNotTerminal()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(MemoryPolicyYaml);
            EndConversationDuringToolChatClient chatClient = new(
                "hello there.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fileName"] = "notes.md",
                    ["content"] = "hello",
                });
            RoutingChatClientFactory chatClients = new(chatClient);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document, new AgentCompilationContext(chatClients) { WorkspaceRoot = _root })["main"];
            ConversationSessionFactory factory = new(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
            ConversationSession session = factory.Create("conversation-1");
            string conversationFolder = session.Workspace!;

            // The callback fires from inside the model's response, before the framework runs the tool
            // it just asked for — mid-turn, exactly where a mid-turn EndConversation would land in production.
            chatClient.BeforeToolCall = () => session.EndConversation(ConversationEndReason.CallerHungUp);

            TurnResult turn = await session.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

            Assert.True(turn.IsTerminal);
            Assert.False(Directory.Exists(conversationFolder));
        }

        private static ConversationSessionFactory BuildFactory(string yaml, string root)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(new ToolCallingChatClient(
                "hello there.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fileName"] = "notes.md",
                    ["content"] = "hello",
                }));

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { WorkspaceRoot = root })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                workspaceRoot: root);
        }

        private static string[] FindRecursive(string root, string fileName)
        {
            return Directory.Exists(root)
                        ? Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
                        : [];
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
        /// Like <see cref="ToolCallingChatClient"/>, but runs <see cref="BeforeToolCall"/> immediately
        /// before it emits the one tool call it makes — the model-side moment a mid-turn
        /// <c>EndConversation</c> lands at, before the framework has actually run the tool.
        /// </summary>
        private sealed class EndConversationDuringToolChatClient(string reply, Dictionary<string, object?> arguments) : IChatClient
        {
            private const string ConversationId = "conversation_1";

            private readonly string _reply = reply;
            private readonly Dictionary<string, object?> _arguments = arguments;
            private bool _answered;

            public Action? BeforeToolCall { get; set; }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                bool answeredAlready = messages.Any(
                    message => message.Contents.Any(content => content is FunctionResultContent));
                AIFunction? tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();
                string responseId = Guid.NewGuid().ToString("N");

                if (tool is not null && !answeredAlready && !_answered)
                {
                    _answered = true;
                    BeforeToolCall?.Invoke();

                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(ConversationId, tool.Name, _arguments)])
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                    };

                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
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
}

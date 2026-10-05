using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// The end-to-end path of a <c>shell:</c> block: the tool the model is handed, the workspace it
    /// runs in, the policy that refuses a command, and the process it starts being cleaned up when the
    /// conversation closes or a turn reaches a terminal stage.
    /// </summary>
    public sealed class ConversationSessionShellTests : IDisposable
    {
        private const string ShellYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "run commands", shell: { kind: local, policy: { deny: ["^rm "] } } }
        entries:
          main:
            agent: only
        """;

        private const string NoShellYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "just talk" }
        entries:
          main:
            agent: only
        """;

        private const string TerminalShellYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: only, instructions: "run one command then stop", shell: { kind: local } }
          entries:
            main:
              policy:
                initial: only
                stages:
                  - { id: only, agent: only, terminal: true }
          """;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "agentcore-shell-session-" + Guid.NewGuid().ToString("N"));

        /// <summary>The name the model sees for the shell tool, read off a throwaway real executor.</summary>
        private static readonly string RunShellToolName = ToolName();

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public async Task FirstRequest_ListsATool_NamedRunShell()
        {
            // One literal assertion: it pins the name the model actually sees.
            Assert.Equal("run_shell", RunShellToolName);

            ShellScriptedClient client = new(RunShellToolName, "pwd", "rm -rf x", "echo $$");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            await using ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            IList<AITool> tools = client.Requests[0].Options?.Tools ?? [];
            Assert.Contains(tools, tool => tool.Name == RunShellToolName);
        }

        [Fact]
        public async Task PwdResult_ContainsTheConversationsWorkspace()
        {
            // ShellYaml's policy declares deny: only, no allow:. If AgentHarnessProviders.BuildPolicy
            // ever stopped translating the empty allow: list to null, ShellPolicy would treat it as
            // deny-all and this undenied pwd would come back rejected instead of succeeding.
            ShellScriptedClient client = new(RunShellToolName, "pwd", "rm -rf x", "echo $$");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            await using ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            Assert.Contains(session.Workspace!, client.ToolResults[0], StringComparison.Ordinal);
        }

        [Fact]
        public async Task RmResult_ComesBackAsRejectedText_NotAFailedTurn()
        {
            ShellScriptedClient client = new(RunShellToolName, "pwd", "rm -rf x", "echo $$");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            await using ConversationSession session = factory.Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            Assert.Contains("rejected", client.ToolResults[1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("done", turn.ReplyText);
        }

        [Fact]
        public async Task AfterTheConversationIsClosed_TheEchoPidIsGoneFromProc()
        {
            ShellScriptedClient client = new(RunShellToolName, "pwd", "rm -rf x", "echo $$");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(factory), TimeSpan.FromMinutes(30), TimeProvider.System);

            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            int pid = ProcHelpers.ParsePid(client.ToolResults[2]);
            Assert.True(Directory.Exists($"/proc/{pid}"), "the bash should be alive before the conversation closes");

            await sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", TestContext.Current.CancellationToken);

            await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task AfterTheConversationIsClosed_ItsWorkspaceFolderIsGone()
        {
            // CloseAsync must delete the folder by itself: a caller may close without ending the conversation.
            ShellScriptedClient client = new(RunShellToolName, "echo $$");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(factory), TimeSpan.FromMinutes(30), TimeProvider.System);

            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);
            string workspace = session.Workspace!;
            Assert.True(Directory.Exists(workspace));
            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            await sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(workspace));
        }

        [Fact]
        public async Task AnIdleUnload_DisposesTheShellAndDeletesTheWorkspaceFolder()
        {
            ShellScriptedClient client = new(RunShellToolName, "echo $$");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            FakeTimeProvider clock = Clock();
            InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(factory), InMemoryConversationSessions.DefaultIdleTimeout, clock);

            ConversationSession session = await sessions.GetOrOpenAsync(
                SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            int pid = ProcHelpers.ParsePid(client.ToolResults[0]);
            string workspace = session.Workspace!;
            Assert.True(Directory.Exists(workspace));

            clock.Advance(InMemoryConversationSessions.DefaultIdleTimeout);

            // The idle timer starts the unload on a background task that this call does not await.
            // Wait for the teardown itself first — the workspace delete is its last step — then give
            // the pid its own short budget for the OS to reap it, rather than race the two.
            await EventuallyAsync(() => !Directory.Exists(workspace));
            await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task CloseAsync_WhenTheStoreThrowsOnFlush_StillDisposesTheShell()
        {
            // AgentCoreChatHistoryProvider.WriteAfterAsync catches every exception a store write
            // raises and reports it through ITranscriptLossCounter.Dropped, whose own contract is that it
            // never throws back out — "the conversation outlives a store that refuses" — so FlushTranscriptAsync
            // itself surfaces nothing here and CloseAsync completes rather than throw. What this proves: even
            // though the flush ate the fault silently, the shell this conversation started is still disposed
            // once CloseAsync returns.
            ThrowingConversationStore store = new();
            ShellScriptedClient client = new(RunShellToolName, "echo $$");
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ShellYaml);
            RoutingChatClientFactory chatClients = new(client);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { WorkspaceRoot = _root, ConversationStore = store })["main"];
            ConversationSessionFactory factory = new(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
            InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(factory), TimeSpan.FromMinutes(30), TimeProvider.System);

            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);
            string workspace = session.Workspace!;
            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);
            int pid = ProcHelpers.ParsePid(client.ToolResults[0]);

            await sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", TestContext.Current.CancellationToken);

            await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(workspace), "a close over a store that refuses its writes must still delete the workspace folder");
        }

        [Fact]
        public async Task ASecondConversationFromTheSameFactory_GetsADifferentPid()
        {
            ToolCallingChatClient client = new(
                "done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = "echo $$" });
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);

            await using ConversationSession sessionA = factory.Create("conversation-a");
            _ = await sessionA.RunTurnAsync("go", TestContext.Current.CancellationToken);

            await using ConversationSession sessionB = factory.Create("conversation-b");
            _ = await sessionB.RunTurnAsync("go", TestContext.Current.CancellationToken);

            int pidA = ProcHelpers.ParsePid(client.ToolResults[0]);
            int pidB = ProcHelpers.ParsePid(client.ToolResults[1]);

            Assert.NotEqual(pidA, pidB);
        }

        [Fact]
        public async Task NoShellBlock_NoRunShellToolInTheRequest()
        {
            using SequencedChatClient reply = new("hello there.");
            ConversationSessionFactory factory = BuildFactory(NoShellYaml, reply);
            await using ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string[] toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            Assert.DoesNotContain(RunShellToolName, toolNames);
            Assert.DoesNotContain(
                "## Shell environment", reply.Options[^1]?.Instructions ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ATurnThatReachesATerminalStage_DisposesTheShellWithoutCloseAsync()
        {
            ShellScriptedClient client = new(RunShellToolName, "echo $$");
            ConversationSessionFactory factory = BuildFactory(TerminalShellYaml, client);
            ConversationSession session = factory.Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);
            Assert.True(turn.IsTerminal);

            int pid = ProcHelpers.ParsePid(client.ToolResults[0]);

            await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task FirstRequest_CarriesShellEnvironmentInstructions_NamingTheConversationsWorkspace()
        {
            ShellScriptedClient client = new(RunShellToolName, "pwd");
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);
            await using ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            string? instructions = client.Requests[0].Options?.Instructions;
            Assert.Contains("## Shell environment", instructions, StringComparison.Ordinal);
            Assert.Contains(session.Workspace!, instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ASecondConversation_GetsItsOwnWorkspaceInItsInstructions()
        {
            ShellScriptedClient client = new(RunShellToolName);
            ConversationSessionFactory factory = BuildFactory(ShellYaml, client);

            await using ConversationSession sessionA = factory.Create("conversation-a");
            _ = await sessionA.RunTurnAsync("go", TestContext.Current.CancellationToken);

            await using ConversationSession sessionB = factory.Create("conversation-b");
            _ = await sessionB.RunTurnAsync("go", TestContext.Current.CancellationToken);

            string? instructionsA = client.Requests[0].Options?.Instructions;
            string? instructionsB = client.Requests[1].Options?.Instructions;

            Assert.Contains(sessionA.Workspace!, instructionsA, StringComparison.Ordinal);
            Assert.Contains(sessionB.Workspace!, instructionsB, StringComparison.Ordinal);
            Assert.DoesNotContain(sessionB.Workspace!, instructionsA, StringComparison.Ordinal);
        }

        private static string ToolName()
        {
            string workspace = Directory.CreateTempSubdirectory("shell-tool-name-").FullName;
            try
            {
                return new LocalShellExecutor(new LocalShellExecutorOptions
                {
                    WorkingDirectory = workspace,
                    AcknowledgeUnsafe = true,
                }).AsAIFunction(requireApproval: false).Name;
            }
            finally
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        private ConversationSessionFactory BuildFactory(string yaml, IChatClient client)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(client);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { WorkspaceRoot = _root })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                workspaceRoot: _root);
        }
    }
}

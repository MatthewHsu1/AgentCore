using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime.Harness;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// A request from one entry on a conversation another entry holds live is refused: the other entry's
    /// session keeps running untouched, and this entry may open the id only once that session is gone.
    /// </summary>
    public sealed class ConversationSessionCrossEntryTests : IDisposable
    {
        private const string PhoneEntry = "phone";

        private const string ChatEntry = "chat";

        // Pinned by ConversationSessionShellTests.FirstRequest_ListsATool_NamedRunShell.
        private const string RunShellToolName = "run_shell";

        private const string PhoneYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "phone agent" }
        entries:
          phone:
            agent: only
        """;

        private const string ShellPhoneYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "run commands", shell: { kind: local, policy: { deny: ["^rm "] } } }
        entries:
          phone:
            agent: only
        """;

        private const string ChatYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "chat agent" }
        entries:
          chat:
            agent: only
        """;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "agentcore-cross-entry-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public async Task AnEntryOpeningAnIdAnotherEntryHoldsIsRefusedAndThatEntrysSessionIsUntouched()
        {
            ShellScriptedClient phoneClient = new(RunShellToolName, "echo $$");
            InMemoryConversationStore store = new();

            ConversationSessionFactory phoneFactory = BuildFactory(ShellPhoneYaml, PhoneEntry, phoneClient, store, _root);
            ConversationSessionFactory chatFactory = BuildFactory(ChatYaml, ChatEntry, new ScriptedChatClient("chat reply"), store, _root);
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession phone = await sessions.GetOrOpenAsync(PhoneEntry, "conversation-1", null, Token);
            _ = await phone.RunTurnAsync("go", Token);

            int pid = ProcHelpers.ParsePid(phoneClient.ToolResults[0]);
            Assert.True(Directory.Exists($"/proc/{pid}"), "the shell should be alive before the refusal.");

            // A file phone's folder holds. A refusal that touched the folder would leave this gone.
            string workspace = phone.Workspace!;
            string sentinel = Path.Combine(workspace, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "still here?", Token);

            ConversationInUseException refused = await Assert.ThrowsAsync<ConversationInUseException>(
                () => sessions.GetOrOpenAsync(ChatEntry, "conversation-1", null, Token).AsTask());
            Assert.Equal("conversation-1", refused.ConversationId);
            Assert.Equal(PhoneEntry, refused.HoldingEntry);

            Assert.True(Directory.Exists($"/proc/{pid}"), "a refused open must not touch phone's shell.");
            Assert.True(File.Exists(sentinel), "a refused open must not touch phone's workspace folder.");
            Assert.Same(phone, await sessions.TryGetAsync(PhoneEntry, "conversation-1", Token));
            Assert.Equal(1, sessions.Count);
        }

        [Fact]
        public async Task AfterThatEntrysSessionIsClosedAnotherEntryOpensTheSameIdAndSeesItsHistory()
        {
            RequestCapturingChatClient chatReply = new(new ScriptedChatClient("chat reply"));
            InMemoryConversationStore store = new();

            ConversationSessionFactory phoneFactory = BuildFactory(PhoneYaml, PhoneEntry, new ScriptedChatClient("phone reply"), store);
            ConversationSessionFactory chatFactory = BuildFactory(ChatYaml, ChatEntry, chatReply, store);
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession phone = await sessions.GetOrOpenAsync(PhoneEntry, "conversation-1", null, Token);
            _ = await phone.RunTurnAsync("go", Token);

            await sessions.CloseAsync(PhoneEntry, "conversation-1", Token);
            Assert.Null(await sessions.TryGetAsync(PhoneEntry, "conversation-1", Token));

            ConversationSession chat = await sessions.GetOrOpenAsync(ChatEntry, "conversation-1", null, Token);
            _ = await chat.RunTurnAsync("hi", Token);

            // chat's own first request carries the words phone's turn wrote to the shared store.
            Assert.Contains(chatReply.Requests[0], message => message.Text == "go");
        }

        [Fact]
        public async Task TwoEntriesWithDifferentIdsEachGetTheirOwnAgentsSession()
        {
            ScriptedChatClient phoneReply = new("phone reply");
            ScriptedChatClient chatReply = new("chat reply");
            ConversationSessionFactory phoneFactory = BuildFactory(PhoneYaml, PhoneEntry, phoneReply);
            ConversationSessionFactory chatFactory = BuildFactory(ChatYaml, ChatEntry, chatReply);
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession phone = await sessions.GetOrOpenAsync(PhoneEntry, "conversation-a", null, Token);
            ConversationSession chat = await sessions.GetOrOpenAsync(ChatEntry, "conversation-b", null, Token);

            TurnResult phoneTurn = await phone.RunTurnAsync("hi", Token);
            TurnResult chatTurn = await chat.RunTurnAsync("hi", Token);

            Assert.Equal("phone reply", phoneTurn.ReplyText);
            Assert.Equal("chat reply", chatTurn.ReplyText);
            Assert.Equal(2, sessions.Count);
        }

        [Fact]
        public async Task TryGetAsyncUnderAnotherEntryReturnsNullAndLeavesTheOtherEntrysSessionOpen()
        {
            ConversationSessionFactory phoneFactory = BuildFactory(PhoneYaml, PhoneEntry, new ScriptedChatClient("phone reply"));
            ConversationSessionFactory chatFactory = BuildFactory(ChatYaml, ChatEntry, new ScriptedChatClient("chat reply"));
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession phone = await sessions.GetOrOpenAsync(PhoneEntry, "conversation-1", null, Token);

            Assert.Null(await sessions.TryGetAsync(ChatEntry, "conversation-1", Token));
            Assert.Same(phone, await sessions.TryGetAsync(PhoneEntry, "conversation-1", Token));
            Assert.Equal(1, sessions.Count);
        }

        [Fact]
        public async Task OpeningAnUndeclaredEntryThrowsArgumentException()
        {
            ConversationSessionFactory phoneFactory = BuildFactory(PhoneYaml, PhoneEntry, new ScriptedChatClient("phone reply"));
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory), TimeSpan.FromMinutes(30), Clock());

            ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(
                () => sessions.GetOrOpenAsync("missing", "conversation-1", null, Token).AsTask());
            Assert.Contains(PhoneEntry, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TwoEntriesRacingToOpenOneIdHoldTheFirstToReserveItAndTheRefusedOneBuildsNothing()
        {
            // Every session writes conversation.started as it is built, so a loser built and then thrown away
            // would leave a start in the winner's audit chain that no conversation ever had.
            RecordingConversationObserver observer = new();
            InMemoryConversationStore store = new();

            using GatedSessionFactory phoneFactory = new(
                BuildFactory(PhoneYaml, PhoneEntry, new ScriptedChatClient("phone reply"), store, _root, observer));
            using GatedSessionFactory chatFactory = new(
                BuildFactory(ChatYaml, ChatEntry, new ScriptedChatClient("chat reply"), store, _root, observer));
            chatFactory.Release();
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            // phone reserves the id, then stalls while building its session; chat asks for the id meanwhile.
            Task<ConversationSession> phoneOpen = OwnThread.Run(
                () => sessions.GetOrOpenAsync(PhoneEntry, "conversation-1", null, Token).AsTask(), Token);
            Assert.True(phoneFactory.Entered.Wait(TimeSpan.FromSeconds(30), Token), "phone never reached the build.");

            ConversationInUseException refused = await Assert.ThrowsAsync<ConversationInUseException>(
                () => sessions.GetOrOpenAsync(ChatEntry, "conversation-1", null, Token).AsTask());
            Assert.Equal(PhoneEntry, refused.HoldingEntry);

            phoneFactory.Release();
            ConversationSession phone = await phoneOpen;

            Assert.Equal(0, chatFactory.Calls);
            Assert.Single(observer.Kinds, kind => kind == ConversationEventKind.ConversationStarted);
            Assert.Same(phone, await sessions.TryGetAsync(PhoneEntry, "conversation-1", Token));
            Assert.Null(await sessions.TryGetAsync(ChatEntry, "conversation-1", Token));
            Assert.Equal(1, sessions.Count);
        }

        [Fact]
        public async Task ACloseFromAnEntryThatDoesNotHoldTheIdClosesNothing()
        {
            ShellScriptedClient phoneClient = new(RunShellToolName, "echo $$");
            InMemoryConversationStore store = new();

            ConversationSessionFactory phoneFactory = BuildFactory(ShellPhoneYaml, PhoneEntry, phoneClient, store, _root);
            ConversationSessionFactory chatFactory = BuildFactory(ChatYaml, ChatEntry, new ScriptedChatClient("chat reply"), store, _root);
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession phone = await sessions.GetOrOpenAsync(PhoneEntry, "conversation-1", null, Token);
            _ = await phone.RunTurnAsync("go", Token);
            int pid = ProcHelpers.ParsePid(phoneClient.ToolResults[0]);
            string sentinel = Path.Combine(phone.Workspace!, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "phone's file", Token);

            await sessions.CloseAsync(ChatEntry, "conversation-1", Token);

            Assert.Same(phone, await sessions.TryGetAsync(PhoneEntry, "conversation-1", Token));
            Assert.True(Directory.Exists($"/proc/{pid}"), "chat's close must not touch phone's shell.");
            Assert.True(File.Exists(sentinel), "chat's close must not touch phone's workspace folder.");
        }

        [Fact]
        public async Task AnOpenFromAnotherEntryDuringACloseWaitsForItThenOpens()
        {
            // A closing session's slot is still taken by its old entry. An open from a different entry must
            // wait for the teardown rather than be refused as a cross-entry conflict.
            ParkingConversationStore transcript = new();
            ConversationSessionFactory phoneFactory = BuildFactory(PhoneYaml, PhoneEntry, new ScriptedChatClient("phone reply"), transcript, _root);
            ConversationSessionFactory chatFactory = BuildFactory(ChatYaml, ChatEntry, new ScriptedChatClient("chat reply"), transcript, _root);
            using InMemoryConversationSessions sessions = new(Owner(phoneFactory, chatFactory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession phone = await sessions.GetOrOpenAsync(PhoneEntry, "conversation-1", null, Token);
            Task<TurnResult> turn = phone.RunTurnAsync("go", Token);
            await transcript.Parked;

            Task closing = sessions.CloseAsync(PhoneEntry, "conversation-1", Token).AsTask();
            ValueTask<ConversationSession> opening = sessions.GetOrOpenAsync(ChatEntry, "conversation-1", null, Token);

            Assert.False(opening.IsCompleted, "an open from another entry during a close must wait for it, not be refused.");
            Assert.Null(await sessions.TryGetAsync(ChatEntry, "conversation-1", Token));

            transcript.Release();
            ConversationSession chat = await opening;
            await closing;
            _ = await turn;

            Assert.NotSame(phone, chat);
            Assert.Same(chat, await sessions.TryGetAsync(ChatEntry, "conversation-1", Token));
            Assert.Null(await sessions.TryGetAsync(PhoneEntry, "conversation-1", Token));
        }

        private static ConversationSessionFactory BuildFactory(
            string yaml,
            string entry,
            IChatClient reply,
            IConversationStore? store = null,
            string? workspaceRoot = null,
            IConversationObserver? observer = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { ConversationStore = store, WorkspaceRoot = workspaceRoot })[entry];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                observers: observer is null ? null : [observer],
                workspaceRoot: workspaceRoot);
        }

        private static Dictionary<string, IConversationSessionFactory> Owner(
            IConversationSessionFactory phone, IConversationSessionFactory? chat = null)
        {
            Dictionary<string, IConversationSessionFactory> owner = new(StringComparer.Ordinal) { [PhoneEntry] = phone };
            if (chat is not null)
            {
                owner[ChatEntry] = chat;
            }

            return owner;
        }
    }
}

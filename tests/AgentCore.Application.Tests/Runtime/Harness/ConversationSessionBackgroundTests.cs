using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// The conversation-end release of background sessions: a child still running when the conversation closes or a
    /// turn reaches a terminal stage is cancelled instead of leaking past the conversation.
    /// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class ConversationSessionBackgroundTests
    {
        private const string LingeringChildYaml =
            """
          apiVersion: agentcore/v1
          guards:
            never: { "<": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: never } ] }
                  - { id: done, agent: blocker, terminal: true }
          """;

        private const string TerminalChildYaml =
            """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: always } ] }
                  - { id: done, agent: blocker, terminal: true }
          """;

        [Fact]
        public async Task CloseAsync_WithAChildStillRunning_CancelsIt()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "blocker",
                    ["input"] = "work",
                    ["description"] = "d",
                });
            BlockingChatClient child = new();
            InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(BuildFactory(LingeringChildYaml, parent, child)), TimeSpan.FromMinutes(30), TimeProvider.System);

            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, token);
            _ = await session.RunTurnAsync("go", token);

            // The turn is over and the conversation is not, and the child is still in its model call: the
            // leak the release closes. Nothing has cancelled it yet.
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.False(child.Cancelled.IsCompleted);

            await sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", token);

            await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), token);
        }

        [Fact]
        public async Task ATurnThatReachesATerminalStage_CancelsAChildStillRunningWithoutCloseAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "blocker",
                    ["input"] = "work",
                    ["description"] = "d",
                });
            BlockingChatClient child = new();
            ConversationSessionFactory factory = BuildFactory(TerminalChildYaml, parent, child);

            await using ConversationSession session = factory.Create("conversation-1");
            TurnResult turn = await session.RunTurnAsync("go", token);

            Assert.True(turn.IsTerminal);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), token);
            await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), token);
        }

        [Fact]
        public async Task ATurnOnTheSameHost_LeavesARunningChildAlone()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "blocker",
                    ["input"] = "work",
                    ["description"] = "d",
                });
            BlockingChatClient child = new();
            ConversationSession session = BuildFactory(LingeringChildYaml, parent, child, new InMemoryConversationStore()).Create("conversation-1");

            _ = await session.RunTurnAsync("go", token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), token);
            _ = await session.RunTurnAsync("status?", token);

            Assert.False(child.Cancelled.IsCompleted);

            await session.DisposeAsync();
            await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), token);
        }

        [Fact]
        public async Task ASessionThatCatchesUpOnAnotherHostsTurn_CancelsItsChildAtTheCatchUp()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            InMemoryConversationStore store = new();
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "blocker",
                    ["input"] = "work",
                    ["description"] = "d",
                });
            BlockingChatClient child = new();
            await using ConversationSession a = BuildFactory(LingeringChildYaml, parent, child, store).Create("conversation-1");
            await using ConversationSession b = BuildFactory(LingeringChildYaml, new ScriptedChatClient("fine"), new BlockingChatClient(), store)
                .Create("conversation-1");

            _ = await a.RunTurnAsync("go", token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), token);
            _ = await b.RunTurnAsync("hello from the other host", token);
            TurnResult caughtUp = await a.RunTurnAsync("status?", token);

            Assert.Equal(2, caughtUp.TurnIndex);
            await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), token);
        }

        private static ConversationSessionFactory BuildFactory(string yaml, IChatClient parent, IChatClient child, IConversationStore? store = null)
        {
            RoutingChatClientFactory chatClients = new(parent);
            _ = chatClients.Route("parent", parent);
            _ = chatClients.Route("blocker", child);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(chatClients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        }

        /// <summary>A model that stays inside its first request until the run is cancelled.</summary>
        private sealed class BlockingChatClient : IChatClient
        {
            private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes when the child enters its model call.</summary>
            public Task Started => _started.Task;

            /// <summary>Completes when the child observes the cancel.</summary>
            public Task Cancelled => _cancelled.Task;

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                _ = _started.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ = _cancelled.TrySetResult();
                    throw;
                }

                yield break;
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

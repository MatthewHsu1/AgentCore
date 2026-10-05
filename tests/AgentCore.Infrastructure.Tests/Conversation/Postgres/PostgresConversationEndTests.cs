using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// A host end after the last turn, in PostgreSQL: the store keeps the end though no words carry it, so a later
    /// session refuses the conversation's turns and does not start it again.
    /// </summary>
    public sealed class PostgresConversationEndTests : PostgresDatabaseTest
    {
        private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        private const string StagedYaml = """
        apiVersion: agentcore/v1
        guards:
          never: { ">=": [ { var: turnIndex }, 99 ] }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            policy:
              initial: working
              stages:
                - { id: working, agent: only, to: [ { stage: done, when: never } ] }
                - { id: done, agent: only, terminal: true }
        """;

        /// <inheritdoc />
        protected override bool Migrated => true;

        private IConversationStore Store => new PostgresConversationStore(DataSource);

        [PostgresFact]
        public Task SaveState_LevelWithTheStoredState_ReplacesIt()
        {
            return ConversationStoreStateFacts.AStateLevelWithTheStoredOneReplacesIt(Store, Token);
        }

        [PostgresFact]
        public Task SaveState_BehindTheStoredState_IsDropped()
        {
            return ConversationStoreStateFacts.AStateBehindTheStoredOneIsDropped(Store, Token);
        }

        [PostgresFact]
        public Task SaveState_OfAConversationWithNoRow_LeavesItAlone()
        {
            return ConversationStoreStateFacts.AConversationWithNoRowIsLeftAlone(Store, Token);
        }

        [PostgresTheory]
        [InlineData(OneAgentYaml)]
        [InlineData(StagedYaml)]
        public async Task AHostEndAfterAFinishedTurn_RefusesTheNextTurnAfterAReload(string yaml)
        {
            // Arrange
            IConversationStore store = Store;
            ConversationSession first = Create(yaml, store, hook: null);
            _ = await first.RunTurnAsync("hi", Token);
            Assert.True(first.EndConversation(ConversationEndReason.CallerHungUp));
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            RecordingHook hook = new();
            ConversationSession second = Create(yaml, store, hook);

            // Act
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => second.RunTurnAsync("are you there?", Token));

            // Assert
            await second.FlushNoticesAsync();
            Assert.Empty(hook.Of<ConversationStarted>());
        }

        [PostgresTheory]
        [InlineData(OneAgentYaml)]
        [InlineData(StagedYaml)]
        public async Task AHostEndBeforeAnyTurnOfAReloadedSession_RefusesTheNextTurnAfterAnotherReload(string yaml)
        {
            // Arrange
            IConversationStore store = Store;
            ConversationSession first = Create(yaml, store, hook: null);
            _ = await first.RunTurnAsync("hi", Token);
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            ConversationSession second = Create(yaml, store, hook: null);
            Assert.True(second.EndConversation(ConversationEndReason.CallerHungUp));
            await second.FlushTranscriptAsync();
            await second.DisposeAsync();

            RecordingHook hook = new();
            ConversationSession third = Create(yaml, store, hook);

            // Act
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => third.RunTurnAsync("are you there?", Token));

            // Assert
            await third.FlushNoticesAsync();
            Assert.Empty(hook.Of<ConversationStarted>());
        }

        private static ConversationSession Create(string yaml, IConversationStore store, AgentHook? hook)
        {
            RoutingChatClientFactory models = new(new HelloChatClient());
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(models) { ConversationStore = store, Hooks = hook is null ? null : [hook] })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create("conversation-1");
        }

        /// <summary>A model that says hello to every request.</summary>
        private sealed class HelloChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "hello") { MessageId = Guid.NewGuid().ToString("N") };
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                }

                return updates.ToChatResponse();
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
                // Nothing to release.
            }
        }
    }
}

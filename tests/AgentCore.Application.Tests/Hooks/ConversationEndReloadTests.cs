using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    // A host end after the last turn is the normal phone hang-up: it must survive a reload.
    public sealed class ConversationEndReloadTests
    {
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

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Theory]
        [InlineData(HookSessions.OneAgentYaml)]
        [InlineData(StagedYaml)]
        public async Task AHostEndAfterAFinishedTurnRefusesTheNextTurnAfterAReload(string yaml)
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient reply = new("hello", "hello again");
            ConversationSession first = HookSessions.Create(yaml, reply, store: store, conversationId: "conversation-1");
            _ = await first.RunTurnAsync("hi", Ct);
            Assert.True(first.EndConversation(ConversationEndReason.CallerHungUp));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => first.RunTurnAsync("again", Ct));
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            RecordingHook hook = new();
            ConversationSession second = HookSessions.Create(yaml, reply, [hook], store, conversationId: "conversation-1");
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => second.RunTurnAsync("are you there?", Ct));
            await second.FlushNoticesAsync();
            Assert.Empty(hook.Of<ConversationStarted>());
        }

        // The turn read its state before the end, so its own append stores the conversation as still open.
        [Theory]
        [InlineData(HookSessions.OneAgentYaml)]
        [InlineData(StagedYaml)]
        public async Task AHostEndWhileTheTurnIsSavingRefusesTheNextTurnAfterAReload(string yaml)
        {
            HeldAppendStore store = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession first = HookSessions.Create(yaml, reply, store: store, conversationId: "conversation-1");
            Task<TurnResult> turn = first.RunTurnAsync("hi", Ct);
            await store.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.True(first.EndConversation(ConversationEndReason.CallerHungUp));
            store.Release.SetResult();
            _ = await turn;
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            RecordingHook hook = new();
            ConversationSession second = HookSessions.Create(yaml, reply, [hook], store, conversationId: "conversation-1");
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => second.RunTurnAsync("are you there?", Ct));
            await second.FlushNoticesAsync();
            Assert.Empty(hook.Of<ConversationStarted>());
        }

        // A reloaded conversation the host ends before this session ran a turn: no session is open to write through.
        [Theory]
        [InlineData(HookSessions.OneAgentYaml)]
        [InlineData(StagedYaml)]
        public async Task AHostEndBeforeAnyTurnOfAReloadedSessionRefusesTheNextTurnAfterAnotherReload(string yaml)
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient reply = new("hello", "hello again");
            ConversationSession first = HookSessions.Create(yaml, reply, store: store, conversationId: "conversation-1");
            _ = await first.RunTurnAsync("hi", Ct);
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            ConversationSession second = HookSessions.Create(yaml, reply, store: store, conversationId: "conversation-1");
            Assert.True(second.EndConversation(ConversationEndReason.CallerHungUp));
            await second.FlushTranscriptAsync();
            await second.DisposeAsync();

            RecordingHook hook = new();
            ConversationSession third = HookSessions.Create(yaml, reply, [hook], store, conversationId: "conversation-1");
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => third.RunTurnAsync("are you there?", Ct));
            await third.FlushNoticesAsync();
            Assert.Empty(hook.Of<ConversationStarted>());
            Assert.Equal(1, (await store.GetAsync("conversation-1", Ct))!.State!.NextTurnIndex);
        }

        // The end is still on its way to the store when the same session opens the conversation for a turn.
        [Fact]
        public async Task AHostEndBeforeAnyTurnRefusesATurnOfTheSameSession()
        {
            HeldStateStore store = new();
            using ScriptedChatClient reply = new("hello", "hello again");
            ConversationSession first = HookSessions.Create(HookSessions.OneAgentYaml, reply, store: store, conversationId: "conversation-1");
            _ = await first.RunTurnAsync("hi", Ct);
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            ConversationSession second = HookSessions.Create(HookSessions.OneAgentYaml, reply, store: store, conversationId: "conversation-1");
            store.Hold = true;
            Assert.True(second.EndConversation(ConversationEndReason.CallerHungUp));
            Exception? refused = await Record.ExceptionAsync(() => second.RunTurnAsync("are you there?", Ct));
            store.Release.SetResult();
            await second.FlushTranscriptAsync();

            _ = Assert.IsType<InvalidOperationException>(refused);
        }

        /// <summary>A store that holds every state-only write, once <see cref="Hold"/> is set, until <see cref="Release"/> completes.</summary>
        private sealed class HeldStateStore() : DelegatingConversationStore(new InMemoryConversationStore())
        {
            public bool Hold { get; set; }

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async ValueTask SaveStateAsync(
                string conversationId, ConversationSessionState state, CancellationToken cancellationToken = default)
            {
                if (Hold)
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }

                await base.SaveStateAsync(conversationId, state, cancellationToken);
            }
        }

        /// <summary>A store that holds the first turn's append until <see cref="Release"/> completes.</summary>
        private sealed class HeldAppendStore() : DelegatingConversationStore(new InMemoryConversationStore())
        {
            public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                if (messages.Count > 0 && Holding.TrySetResult())
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }

                return await base.AppendAsync(conversationId, messages, state, cancellationToken);
            }
        }
    }
}

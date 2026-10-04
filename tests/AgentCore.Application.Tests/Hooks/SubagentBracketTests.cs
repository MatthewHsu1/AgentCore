using System.Text.Json;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class SubagentBracketTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Every Started is closed by one Ended, wherever the run stops.
        [Fact]
        public async Task ASessionThatCannotBeCreatedStillClosesTheBracketAsFaulted()
        {
            (RecordingHook hook, SessionHooks hooks, TurnInvocation turn) = Open();
            SubagentAgent inner = new(createSession: () => throw new InvalidOperationException("no session"));

            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await DelegatedAgentRun.RunAsync(inner, "help", turn, tools: null, [], Ct));
            await hooks.FlushAsync();

            _ = Assert.Single(hook.Of<SubagentStarted>());
            Assert.Equal(SubagentOutcome.Faulted, Assert.Single(hook.Of<SubagentEnded>()).Outcome);
        }

        [Fact]
        public async Task ACancelDuringTheRunClosesTheBracketAsCancelled()
        {
            (RecordingHook hook, SessionHooks hooks, TurnInvocation turn) = Open();
            using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            SubagentAgent inner = new(run: async token =>
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            });

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await DelegatedAgentRun.RunAsync(inner, "help", turn, tools: null, [], cancel.Token));
            await hooks.FlushAsync();

            _ = Assert.Single(hook.Of<SubagentStarted>());
            Assert.Equal(SubagentOutcome.Cancelled, Assert.Single(hook.Of<SubagentEnded>()).Outcome);
        }

        [Fact]
        public async Task ACancelDuringSessionCreationClosesTheBracketAsCancelled()
        {
            (RecordingHook hook, SessionHooks hooks, TurnInvocation turn) = Open();
            using CancellationTokenSource cancel = new();
            await cancel.CancelAsync();
            SubagentAgent inner = new(createSession: () => throw new OperationCanceledException(cancel.Token));

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await DelegatedAgentRun.RunAsync(inner, "help", turn, tools: null, [], cancel.Token));
            await hooks.FlushAsync();

            Assert.Equal(SubagentOutcome.Cancelled, Assert.Single(hook.Of<SubagentEnded>()).Outcome);
        }

        private static (RecordingHook Hook, SessionHooks Hooks, TurnInvocation Turn) Open()
        {
            RecordingHook hook = new();
            HookRuntime runtime = HookRuntime.Create([hook], loggers: null);
            SessionHooks hooks = new(runtime, "conversation-1", "main", TimeProvider.System);
            TurnInvocation turn = new()
            {
                ConversationId = "conversation-1",
                TurnIndex = 0,
                Stage = string.Empty,
                Hooks = hooks,
                OuterCallId = "call-1",
            };

            return (hook, hooks, turn);
        }

        private sealed class SubagentAgent(Func<AgentSession>? createSession = null, Func<CancellationToken, Task>? run = null) : AIAgent
        {
            protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            {
                return new(createSession?.Invoke() ?? new StubSession());
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
                if (run is not null)
                {
                    await run(cancellationToken);
                }

                return new AgentResponse(new ChatMessage(ChatRole.Assistant, "done"));
            }

            protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
                IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}

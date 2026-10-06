using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class TurnGateTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ABlockedTurnSpeaksTheBlockAndNeverReachesTheModel()
        {
            RecordingConversationStore store = new();
            ToolCallingChatClient model = new("never");
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, model, [new Gate(gate => gate.Block("not today")), notices], store);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            Assert.Equal("not today", turn.ReplyText);
            Assert.Equal(0, model.Calls);
            Assert.Equal(TurnOutcome.Blocked, Assert.Single(notices.Of<TurnCompleted>()).Outcome);
            Assert.Contains(store.Live(session.ConversationId), row => row.Content.Role == ChatRole.Assistant && row.Content.Text == "not today");
        }

        [Fact]
        public async Task AReplacedInputIsWhatEveryoneDownstreamSees()
        {
            RecordingConversationStore store = new();
            RequestCapturingChatClient capture = new(new ScriptedChatClient("ok"));
            ScriptedModerationEvaluator moderation = ScriptedModerationEvaluator.Clean();
            RecordingHook notices = new();
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(HookSessions.OneAgentYaml),
                new AgentCompilationContext(new FakeChatClientFactory(capture))
                {
                    ConversationStore = store,
                    Moderation = new PromptModerator(moderation),
                    Hooks = [new Gate(gate => gate.ReplaceInput("my card is [redacted]")), notices],
                })["main"];
            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create();

            _ = await session.RunTurnAsync("my card is 4111", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            Assert.Contains(capture.Requests[0], message => message.Role == ChatRole.User && message.Text == "my card is [redacted]");
            Assert.DoesNotContain(capture.Requests[0], message => message.Text.Contains("4111", StringComparison.Ordinal));
            Assert.Equal(["my card is [redacted]"], moderation.Moderated);
            ConversationMessage user = Assert.Single(store.Live(session.ConversationId), row => row.Content.Role == ChatRole.User);
            Assert.Equal("my card is [redacted]", user.Content.Text);
            Assert.Equal("my card is [redacted]", Assert.Single(notices.Of<TurnCompleted>()).UserText);
        }

        [Fact]
        public async Task AddedContextReachesTheModelAndIsNeverStored()
        {
            RecordingConversationStore store = new();
            RequestCapturingChatClient capture = new(new ScriptedChatClient("ok"));
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, capture, [new Gate(gate => gate.AddContext("the caller is a VIP"))], store);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushTranscriptAsync();

            Assert.Contains(capture.Requests[0], message => message.Role == ChatRole.System && message.Text == "the caller is a VIP");
            Assert.DoesNotContain(store.Live(session.ConversationId), row => row.Content.Text.Contains("VIP", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ATurnWithNoDecisionRunsAsBefore()
        {
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, new ScriptedChatClient("hello"), [new Gate(_ => { })]);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);

            Assert.Equal("hello", turn.ReplyText);
        }

        [Fact]
        public async Task AHookThatThrowsFailsOpenAndTheTurnRuns()
        {
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, new ScriptedChatClient("hello"), [new Gate(_ => throw new InvalidOperationException("boom")), notices]);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal("hello", turn.ReplyText);
            Assert.Equal(FaultKind.HookFailed, Assert.Single(notices.Of<Fault>()).Kind);
        }

        [Fact]
        public async Task AHookThatFailsClosedBlocksTheTurnWithTheRefusalReply()
        {
            ToolCallingChatClient model = new("never");
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, model, [new ClosedGate(), notices]);

            TurnResult turn = await session.RunTurnAsync("my card is 4111", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal("I am sorry. I cannot help with that request.", turn.ReplyText);
            Assert.Equal(0, model.Calls);
            Assert.Equal(TurnOutcome.Blocked, Assert.Single(notices.Of<TurnCompleted>()).Outcome);
        }

        [Fact]
        public async Task AddedContextBelongsToItsTurnOnly()
        {
            RequestCapturingChatClient capture = new(new ScriptedChatClient("ok"));
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml,
                capture,
                [new Gate(gate => { if (gate.UserText == "first") { gate.AddContext("first-turn note"); } })]);

            _ = await session.RunTurnAsync("first", Ct);
            _ = await session.RunTurnAsync("second", Ct);

            Assert.Contains(capture.Requests[0], message => message.Text == "first-turn note");
            Assert.DoesNotContain(capture.Requests[1], message => message.Text == "first-turn note");
        }

        private sealed class ClosedGate : AgentHook
        {
            public override HookFailure FailureFor(GatePoint point)
            {
                return HookFailure.Closed;
            }

            public override ValueTask BeforeTurnAsync(TurnGate gate, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("boom");
            }
        }

        private sealed class Gate(Action<TurnGate> decide) : AgentHook
        {
            public override ValueTask BeforeTurnAsync(TurnGate gate, CancellationToken cancellationToken)
            {
                decide(gate);
                return default;
            }
        }
    }
}

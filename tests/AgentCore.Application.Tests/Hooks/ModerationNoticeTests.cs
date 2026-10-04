using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ModerationNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The verdict is a notice stamped when moderation decided, before the turn completes.
        [Fact]
        public async Task AFlaggedInputIsNamedBeforeItsBlockedTurnCompletes()
        {
            RecordingHook hook = new();
            ConversationSession session = Create(hook, ScriptedModerationEvaluator.Flagging("violence", "harassment"));

            _ = await session.RunTurnAsync("something awful", Ct);
            await session.FlushNoticesAsync();

            InputModerated moderated = Assert.Single(hook.Of<InputModerated>());
            TurnCompleted completed = Assert.Single(hook.Of<TurnCompleted>());
            Assert.Equal(InputVerdict.Flagged, moderated.Verdict);
            Assert.Equal(["violence", "harassment"], moderated.Categories);
            Assert.Null(moderated.Reason);
            Assert.Equal(TurnOutcome.Blocked, completed.Outcome);
            Assert.True(moderated.Scope.Sequence < completed.Scope.Sequence);
            Assert.Equal(0, moderated.Scope.TurnIndex);
        }

        [Fact]
        public async Task ACleanInputIsNamedClean()
        {
            RecordingHook hook = new();
            ConversationSession session = Create(hook, ScriptedModerationEvaluator.Clean());

            _ = await session.RunTurnAsync("hello", Ct);
            await session.FlushNoticesAsync();

            InputModerated moderated = Assert.Single(hook.Of<InputModerated>());
            Assert.Equal(InputVerdict.Clean, moderated.Verdict);
            Assert.Empty(moderated.Categories);
            Assert.Equal(TurnOutcome.Answered, hook.Of<TurnCompleted>().Single().Outcome);
        }

        // Unavailable carries why (TimedOut or Threw).
        [Fact]
        public async Task AModeratorThatThrowsIsUnavailableBecauseItThrew()
        {
            RecordingHook hook = new();
            ConversationSession session = Create(hook, ScriptedModerationEvaluator.Throwing(new InvalidOperationException("down")));

            _ = await session.RunTurnAsync("hello", Ct);
            await session.FlushNoticesAsync();

            InputModerated moderated = Assert.Single(hook.Of<InputModerated>());
            Assert.Equal((InputVerdict.Unavailable, ModerationUnavailableReason.Threw), (moderated.Verdict, moderated.Reason));
        }

        // ModerationAgent.DefaultTimeout is 2 s of wall time and the agent has no TimeProvider seam, so this test takes about 2 s.
        [Fact(Timeout = 30_000)]
        public async Task AModeratorThatPassesItsDeadlineIsUnavailableBecauseItTimedOut()
        {
            RecordingHook hook = new();
            ConversationSession session = Create(hook, ScriptedModerationEvaluator.Slow(TimeSpan.FromSeconds(10)));

            _ = await session.RunTurnAsync("hello", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(ModerationUnavailableReason.TimedOut, Assert.Single(hook.Of<InputModerated>()).Reason);
        }

        private static ConversationSession Create(RecordingHook hook, ScriptedModerationEvaluator moderation)
        {
            ScriptedChatClient reply = new("an answer");
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(HookSessions.OneAgentYaml),
                new AgentCompilationContext(new FakeChatClientFactory(reply))
                {
                    Moderation = new PromptModerator(moderation),
                    Hooks = [hook],
                })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create();
        }
    }
}

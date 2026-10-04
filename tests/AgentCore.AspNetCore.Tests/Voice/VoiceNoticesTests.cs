using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    public sealed class VoiceNoticesTests
    {
        // Party, old → new, for the agent and for the user.
        [Fact(Timeout = 10_000)]
        public async Task EachStateChangeIsANotice()
        {
            RecordingHook hook = new();
            SessionHooks hooks = new(HookRuntime.Create([hook], loggers: null), "c1", "main", TimeProvider.System);
            await using FakeConversationOutput output = new();
            VoiceSession session = new(output, new FakeTimeProvider(DateTimeOffset.UnixEpoch), NullLogger.Instance);
            VoiceNotices.Attach(session, () => hooks);

            session.SetAgentState(AgentState.Thinking);
            session.SetAgentState(AgentState.Speaking);
            session.SetUserState(UserState.Speaking);
            session.SetUserState(UserState.Away);
            await hooks.FlushAsync();

            Assert.Equal(
                [
                    (VoiceParty.Agent, VoiceState.Listening, VoiceState.Thinking),
                    (VoiceParty.Agent, VoiceState.Thinking, VoiceState.Speaking),
                    (VoiceParty.User, VoiceState.Listening, VoiceState.Speaking),
                    (VoiceParty.User, VoiceState.Speaking, VoiceState.Away),
                ],
                hook.Of<VoiceStateChanged>().Select(change => (change.Party, change.Old, change.New)));
        }
    }
}

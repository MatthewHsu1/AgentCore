using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>The filler each tool call opens in a parallel tool round of <see cref="PipelineReply"/>.</summary>
    public sealed class PipelineReplyFillerTests : IAsyncDisposable
    {
        private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

        private readonly FakeTimeProvider _time = new(Start);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly VoiceSession _session;

        public PipelineReplyFillerTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // Final review probe D (2026-09-23).
        [Fact(Timeout = 30_000)]
        public async Task AFastToolsFiller_StopsWhenItsOwnToolReturns_WhileASlowToolStillRuns()
        {
            VoiceActivity activity = Activity(new() { ["fast"] = new("still on the fast one", TimeSpan.FromSeconds(2)) });

            SpeechHandle handle = activity.GenerateReply("Where are my orders?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look.");
            await turn.CallAsync("c1", "fast");
            await turn.CallAsync("c2", "slow");
            await _time.WaitForTimersAsync(Start.AddSeconds(2), 1);

            await turn.ResultAsync("c1");
            await _time.WaitForTimersAsync(Start.AddSeconds(2), 0);
            _time.Advance(TimeSpan.FromSeconds(2));

            await turn.ResultAsync("c2");
            await turn.TextAsync("Both shipped.");
            turn.End();
            _ = await handle;

            Assert.Equal(["Let me look.", "Both shipped."], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ASlowToolsFiller_StillPlays_AfterAFastToolReturns()
        {
            VoiceActivity activity = Activity(new()
            {
                ["fast"] = new("still on the fast one", TimeSpan.FromSeconds(2)),
                ["slow"] = new("still on the slow one", TimeSpan.FromSeconds(3)),
            });

            SpeechHandle handle = activity.GenerateReply("Where are my orders?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look.");
            await turn.CallAsync("c1", "fast");
            await turn.CallAsync("c2", "slow");
            await _time.WaitForTimersAsync(Start.AddSeconds(2), 1);
            await _time.WaitForTimersAsync(Start.AddSeconds(3), 1);

            await turn.ResultAsync("c1");
            await _time.WaitForTimersAsync(Start.AddSeconds(2), 0);
            _time.Advance(TimeSpan.FromSeconds(3));
            await _output.WaitForLogAsync(4);

            await turn.ResultAsync("c2");
            await turn.TextAsync("Both shipped.");
            turn.End();
            _ = await handle;

            Assert.Equal(["Let me look.", "still on the slow one", "Both shipped."], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeInDuringTheRound_ClosesEveryScopeBeforeTheSpeechEnds()
        {
            VoiceActivity activity = Activity(new()
            {
                ["fast"] = new("still on the fast one", TimeSpan.FromSeconds(2)),
                ["slow"] = new("still on the slow one", TimeSpan.FromSeconds(2)),
                ["other"] = new("still on the other one", TimeSpan.FromSeconds(2)),
            });

            SpeechHandle handle = activity.GenerateReply("Where are my orders?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look.");
            await turn.CallAsync("c1", "fast");
            await turn.CallAsync("c2", "slow");
            await turn.CallAsync("c3", "other");
            await _time.WaitForTimersAsync(Start.AddSeconds(2), 3);
            await turn.ResultAsync("c1");

            await activity.InterruptByAudioActivity("Let me look.", TimeSpan.FromMilliseconds(900));
            _ = await handle;

            Assert.True(handle.IsInterrupted);
            Assert.True(_time.WaitForTimersAsync(Start.AddSeconds(2), 0).IsCompleted, "a filler was still armed when the speech ended.");
        }

        // A barge-in stops each filler on its own (filler_scheduler.py:104), so only a turn that ends on an
        // unanswered call needs the round's own close.
        [Fact(Timeout = 30_000)]
        public async Task ATurnThatEndsOnAnUnansweredCall_ClosesItsFillerBeforeTheSpeechEnds()
        {
            VoiceActivity activity = Activity(new() { ["slow"] = new("still on the slow one", TimeSpan.FromSeconds(2)) });

            SpeechHandle handle = activity.GenerateReply("Where are my orders?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look.");
            await turn.CallAsync("c1", "slow");
            await _time.WaitForTimersAsync(Start.AddSeconds(2), 1);

            turn.End();
            _ = await handle;

            Assert.True(_time.WaitForTimersAsync(Start.AddSeconds(2), 0).IsCompleted, "the filler was still armed when the speech ended.");
        }

        private VoiceActivity Activity(Dictionary<string, FillerOptions> fillers)
        {
            return new VoiceActivity(_session, _port, CancellationToken.None, fillers: fillers);
        }
    }
}

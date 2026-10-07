using AgentCore.Application.Runtime.Cut;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>What an interruption does to a reply whose every word was already out when it landed.</summary>
    public sealed class ReplyHearingTests
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly ScriptedVoicePort _port = new();

        // The interruption cut nothing, so the window waits for the transport's report as a done reply's does.
        [Fact(Timeout = 30_000)]
        public async Task AReplySpokenWhole_KeepsItsBargeWindowOpenThroughAFinalPromptsInterruption()
        {
            ReplyHearing hearing = await SpokenAsync("hello there caller");

            hearing.MarkSpokenWhole();
            hearing.ExpectBarge();
            hearing.MarkSpoken(interrupted: true);

            Assert.False(hearing.AwaitsBarge);
            Assert.False(hearing.BargeWindowClosed);
            Assert.Empty(_port.Cuts);
        }

        // The final prompt's interruption landed after the last word and kept the report for a recut that never runs.
        [Fact(Timeout = 30_000)]
        public async Task AReportKeptBeforeTheReplyWasMarkedSpokenWhole_CutsTheLineAndTheTurn()
        {
            ReplyHearing hearing = await SpokenAsync("hello there caller");
            hearing.ExpectBarge();
            Assert.True(hearing.RecordBarge("hello there", TimeSpan.FromMilliseconds(400)));

            hearing.MarkSpokenWhole();

            Assert.Equal("hello there", hearing.HeardText);
            Assert.True(hearing.BargeWindowClosed);
            Assert.Equal([(0, new TurnCut("hello there", TimeSpan.FromMilliseconds(400)))], _port.Cuts);
        }

        private async Task<ReplyHearing> SpokenAsync(string text)
        {
            EngineReplyStream stream = EngineReplyStream.Start(
                _port,
                "hi",
                Task.CompletedTask,
                SpeechHandle.Create(_time, NullLogger.Instance),
                new TurnMetrics(_time, userTurnEndedAt: null, static (_, _, _) => { }),
                NullLogger.Instance,
                CancellationToken.None);
            _ = await _port.Turn(1).Started;

            ReplyHearing hearing = new(stream, _time, VoiceOptions.DefaultHeardTextWait, CancellationToken.None);
            hearing.MarkFirstText();
            hearing.AddStep(new TextForwardingResult(text, TextPlayback.Full));
            return hearing;
        }
    }
}

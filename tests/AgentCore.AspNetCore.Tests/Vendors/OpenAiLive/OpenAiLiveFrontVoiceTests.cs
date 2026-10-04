using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.AspNetCore.Tests.Vendors.OpenAiLive.LiveSent;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// A real SIP call (2026-10-04): GPT-Live answered "who are you" and "what day is it" itself, the second one
    /// wrongly, then delegated "please end the call now". OpenAI's delegation guide: the delegation carries no text,
    /// and the backend request is built from both transcripts and the saved history. The back office must read both
    /// sides as the caller heard them, and answer only the latest request.
    /// </summary>
    public sealed class OpenAiLiveFrontVoiceTests
    {
        private const string LatestRequest = "Okay, uh please end the call now";

        private static readonly (string Role, string? Author, string Text)[] SaidBefore =
        [
            ("user", null, "Uh, who are you"),
            ("assistant", FrontVoice.AuthorName, "You're speaking with Sole Fitness's virtual assistant."),
            ("user", null, "Um, do you know what day it is today"),
            ("assistant", FrontVoice.AuthorName, "It's Wednesday."),
        ];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 30_000)]
        public async Task TheBackOfficeReadsBothSidesOfWhatGptLiveHandledThenOnlyTheLatestRequest()
        {
            StallOnCueChatClient model = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(model, []);

            JsonObject commentary = await AskAsync(running);

            Assert.Equal("reply to " + LatestRequest, (string?)commentary["content"]);
            Assert.Equal([.. SaidBefore, ("user", null, LatestRequest)], Spoken(Assert.Single(model.Requests)));
        }

        // The lines are words of the one delegated turn: one TurnStarted, one TurnCompleted, and the reply is the agent's.
        [Fact(Timeout = 30_000)]
        public async Task TheLinesRaiseNoTurnOfTheirOwnAndEveryLineIsSpokenOnce()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [hook]);

            _ = await AskAsync(running);
            await EndAsync(running);
            _ = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal(LatestRequest, Assert.Single(hook.Of<TurnStarted>()).UserText);
            TurnCompleted completed = Assert.Single(hook.Of<TurnCompleted>());
            Assert.Equal((LatestRequest, "reply to " + LatestRequest), (completed.UserText, completed.ReplyText));
            Assert.Equal(
                [
                    (Speaker.Caller, "Uh, who are you"),
                    (Speaker.Agent, "You're speaking with Sole Fitness's virtual assistant."),
                    (Speaker.Caller, "Um, do you know what day it is today"),
                    (Speaker.Agent, "It's Wednesday."),
                    (Speaker.Caller, LatestRequest),
                ],
                hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
        }

        [Fact(Timeout = 30_000)]
        public async Task AfterAReloadTheConversationHoldsBothSidesInSpeechOrder()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            ConversationSession during = running.Call.Session;

            _ = await AskAsync(running);
            await EndAsync(running);
            ConversationSession reloaded = await running.Harness.Sessions.GetOrOpenAsync("main", running.Call.ConversationId, state: null, Ct);
            _ = await reloaded.Ledger.OpenSessionAsync(Ct);

            Assert.NotSame(during, reloaded);
            Assert.Equal([.. SaidBefore, ("user", null, LatestRequest), ("assistant", "only", "reply to " + LatestRequest)], Spoken(reloaded.Transcript));
        }

        private static async Task<JsonObject> AskAsync(RunningLiveCall running)
        {
            running.Sideband.Push(
            [
                Heard(OpenAiLiveEvents.InputTranscriptDelta, " Uh, who are you", 1000, 2000),
                Heard(OpenAiLiveEvents.OutputTranscriptDelta, " You're speaking with Sole Fitness's", 2200, 3000),
                Heard(OpenAiLiveEvents.OutputTranscriptDelta, " virtual assistant.", 3000, 4000),
                Heard(OpenAiLiveEvents.InputTranscriptDelta, " Um, do you know what day it is today", 5000, 7000),
                Heard(OpenAiLiveEvents.OutputTranscriptDelta, " It's Wednesday.", 7200, 8000),
                Heard(OpenAiLiveEvents.InputTranscriptDelta, " Okay, uh please end the call now", 9000, 11000),
                new JsonObject
                {
                    ["type"] = OpenAiLiveEvents.DelegationCreated,
                    ["offset_ms"] = 10500,
                    ["delegation"] = new JsonObject { ["id"] = "item_end", ["type"] = "client", ["target"] = "backend" },
                }.ToJsonString(),
            ]);
            return await running.Sideband.WaitForSentAsync(IsCommentary);
        }

        private static async Task EndAsync(RunningLiveCall running)
        {
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            running.Sideband.Push([log[LiveLog.IndexOf(log, OpenAiLiveEvents.Closed)]]);
            await running.Loop;
        }

        private static string Heard(string type, string delta, int start, int end) =>
            new JsonObject { ["type"] = type, ["delta"] = delta, ["start_ms"] = start, ["end_ms"] = end }.ToJsonString();

        private static List<(string Role, string? Author, string Text)> Spoken(IEnumerable<ChatMessage> messages) =>
            [.. messages.Where(message => message.Role != ChatRole.System).Select(message => (message.Role.Value, message.AuthorName, message.Text))];
    }
}

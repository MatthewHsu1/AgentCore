using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// A delegation closes the caller's words by arrival order, and lines close at a
    /// speaker change or a delegation. An ask's words are the caller's latest request: the caller's line after the
    /// last thing GPT-Live said itself; the exchanges before it ride ahead of the ask. Expected values are the recorded
    /// logs and a real SIP call replayed under that rule.
    /// </summary>
    public sealed class LiveTranscriptLedgerTests
    {
        [Fact]
        public void TheP1CallSplitsIntoSixLinesAndOneAsk()
        {
            LiveReplay replay = LiveReplay.Of("p1-a");

            Assert.Equal(
                [("item_ESevHlqkAJkwt1wThn5gW", "Can you check the status of my order? The order number is A four four seven one")],
                replay.Asks);
            Assert.Equal(
                [new LiveLine(Speaker.Caller, "Hi there", 1200, 1800), new LiveLine(Speaker.Agent, "Hey! Thanks for calling Sole Fitness. How can I help?", 2000, 4800)],
                Assert.Single(replay.Before));
            Assert.Equal(
                [
                    new LiveLine(Speaker.Caller, "Hi there", 1200, 1800),
                    new LiveLine(Speaker.Agent, "Hey! Thanks for calling Sole Fitness. How can I help?", 2000, 4800),
                    new LiveLine(Speaker.Caller, "Can you check the status of my order? The order number is A four four seven one", 5600, 10600),
                    new LiveLine(Speaker.Agent, "Sure. Checking that now. Okay. That order shipped on September 24 with UPS. It should arrive on Tuesday, September 29.", 10800, 19000),
                    new LiveLine(Speaker.Caller, "Okay. Thanks. That's all", 28600, 31200),
                    new LiveLine(Speaker.Agent, "You're welcome. Take care!", 31000, 32000),
                ],
                replay.Lines);
            Assert.Equal("close_requested", replay.ClosedReason);
        }

        // A correction is a second delegation that carries only the correction's words.
        [Theory]
        [InlineData("p3-drop-0", "item_ESexRCCGscju2z3LSZNCl", "What is the maximum speed of the Sole F63 treadmill?", "item_ESexZMJMkWUoowj5D7061", "Oh, wait, sorry. I meant the F80, not the F63")]
        [InlineData("p3-stale-0", "item_ESexSsIl2XxGxcM1lsQFb", "What is the maximum speed of the Sole F63 treadmill", "item_ESexZiu0OmTOVioUO7wxZ", "Oh wait, sorry. I meant the F80 not the F63")]
        public void ACorrectionIsASecondAskWithOnlyTheNewWords(string fixture, string firstId, string firstWords, string secondId, string secondWords)
        {
            LiveReplay replay = LiveReplay.Of(fixture);

            Assert.Equal([(firstId, firstWords), (secondId, secondWords)], replay.Asks);
            Assert.All(replay.Before, Assert.Empty);
        }

        // The real call: GPT-Live answered two questions itself (one wrongly), then delegated the third.
        [Fact]
        public void TheExchangesGptLiveHandledItselfRideAheadOfTheLatestRequest()
        {
            LiveTranscriptLedger ledger = new();
            Say(ledger, Speaker.Caller, " Uh, who are you", 1000, 2000);
            Say(ledger, Speaker.Agent, " You're speaking with Sole Fitness's virtual assistant.", 2200, 4000);
            Say(ledger, Speaker.Caller, " Um, do you know what day it is today", 5000, 7000);
            Say(ledger, Speaker.Agent, " It's Wednesday.", 7200, 8000);
            Say(ledger, Speaker.Caller, " Okay, uh please end the call now", 9000, 11000);

            (IReadOnlyList<LiveLine> closed, IReadOnlyList<LiveLine> before, string words) = ledger.TakeForDelegation();

            Assert.Equal("Okay, uh please end the call now", words);
            Assert.Equal(
                [
                    new LiveLine(Speaker.Caller, "Uh, who are you", 1000, 2000),
                    new LiveLine(Speaker.Agent, "You're speaking with Sole Fitness's virtual assistant.", 2200, 4000),
                    new LiveLine(Speaker.Caller, "Um, do you know what day it is today", 5000, 7000),
                    new LiveLine(Speaker.Agent, "It's Wednesday.", 7200, 8000),
                ],
                before);
            Assert.Equal([new LiveLine(Speaker.Caller, "Okay, uh please end the call now", 9000, 11000)], closed);
        }

        // After a delegation GPT-Live speaks its answer (and its filler): that is the turn's own reply, stored already.
        [Fact]
        public void WhatGptLiveSaysBeforeTheCallerSpeaksAgainIsTheLastAnswerNotTheFrontVoice()
        {
            LiveTranscriptLedger ledger = new();
            Say(ledger, Speaker.Caller, " Is the F80 in stock?", 0, 900);
            _ = ledger.TakeForDelegation();
            Say(ledger, Speaker.Agent, " Checking that. Yes, it is in stock.", 1000, 3000);
            Say(ledger, Speaker.Caller, " Thanks. Who am I talking to?", 3500, 5000);
            Say(ledger, Speaker.Agent, " Sole Fitness's virtual assistant.", 5200, 6500);
            Say(ledger, Speaker.Caller, " How much is it?", 7000, 8000);

            (_, IReadOnlyList<LiveLine> before, string words) = ledger.TakeForDelegation();

            Assert.Equal("How much is it?", words);
            Assert.Equal(
                [new LiveLine(Speaker.Caller, "Thanks. Who am I talking to?", 3500, 5000), new LiveLine(Speaker.Agent, "Sole Fitness's virtual assistant.", 5200, 6500)],
                before);
        }

        // Before the first delegation GPT-Live answered nothing for us: its greeting is its own.
        [Fact]
        public void TheGreetingBeforeTheFirstDelegationRidesAheadOfTheAsk()
        {
            LiveTranscriptLedger ledger = new();
            Say(ledger, Speaker.Agent, " Thanks for calling Sole Fitness.", 0, 1500);
            Say(ledger, Speaker.Caller, " Where is my order?", 2000, 3000);

            (_, IReadOnlyList<LiveLine> before, string words) = ledger.TakeForDelegation();

            Assert.Equal("Where is my order?", words);
            Assert.Equal([new LiveLine(Speaker.Agent, "Thanks for calling Sole Fitness.", 0, 1500)], before);
        }

        // GPT-Live's words between the caller's request and the delegation are about the delegation itself.
        [Fact]
        public void WhatGptLiveSaidAfterTheLatestRequestIsNotPartOfTheAsk()
        {
            LiveTranscriptLedger ledger = new();
            Say(ledger, Speaker.Caller, " Hi", 0, 300);
            Say(ledger, Speaker.Agent, " Hello!", 400, 800);
            Say(ledger, Speaker.Caller, " Where is my order?", 1000, 2000);
            Say(ledger, Speaker.Agent, " Let me check.", 2100, 2600);

            (_, IReadOnlyList<LiveLine> before, string words) = ledger.TakeForDelegation();

            Assert.Equal("Where is my order?", words);
            Assert.Equal([new LiveLine(Speaker.Caller, "Hi", 0, 300), new LiveLine(Speaker.Agent, "Hello!", 400, 800)], before);
        }

        [Fact]
        public void TheP3DropCallSplitsIntoFourLines()
        {
            Assert.Equal(
                [
                    new LiveLine(Speaker.Caller, "What is the maximum speed of the Sole F63 treadmill?", 1200, 5200),
                    new LiveLine(Speaker.Agent, "Hmm, alright, I’ll go check that.", 5400, 7600),
                    new LiveLine(Speaker.Caller, "Oh, wait, sorry. I meant the F80, not the F63", 8600, 12800),
                    new LiveLine(Speaker.Agent, "Sure, I’ll check the F80. The Sole F80 goes up to 10.5 miles per hour.", 13000, 18600),
                ],
                LiveReplay.Of("p3-drop-0").Lines);
        }

        [Fact]
        public void ADelegationWithNoNewCallerWordsAsksWithNone()
        {
            LiveTranscriptLedger ledger = new();
            _ = ledger.Add(new LiveEvent.Transcript(Speaker.Caller, " Hello", 0, 200));
            _ = ledger.TakeForDelegation();
            _ = ledger.Add(new LiveEvent.Transcript(Speaker.Agent, " One moment.", 300, 900));

            (_, IReadOnlyList<LiveLine> before, string words) = ledger.TakeForDelegation();

            Assert.Equal(string.Empty, words);
            Assert.Empty(before);
        }

        [Fact]
        public void ADelegationClosesTheLineThatIsOpen()
        {
            LiveTranscriptLedger ledger = new();
            _ = ledger.Add(new LiveEvent.Transcript(Speaker.Caller, " Is it in stock?", 100, 900));

            (IReadOnlyList<LiveLine> closed, _, _) = ledger.TakeForDelegation();

            Assert.Equal([new LiveLine(Speaker.Caller, "Is it in stock?", 100, 900)], closed);
            Assert.Empty(ledger.Flush());
        }

        private static void Say(LiveTranscriptLedger ledger, Speaker speaker, string delta, int start, int end)
        {
            _ = ledger.Add(new LiveEvent.Transcript(speaker, delta, start, end));
        }
    }
}

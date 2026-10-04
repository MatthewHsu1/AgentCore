using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// What happens to the conversation after a barge-in: the interrupted turn is settled and the next
    /// turn answers normally.
    /// </summary>
    public sealed class TelnyxRelayBargeInRecoveryTests
    {
        // LiveKit's agent_activity.py:2774-2783: a second final prompt interrupts turn one, which keeps the text
        // that reached the relay, and turn two answers. Nothing is held.
        [Fact(Timeout = 30_000)]
        public async Task ASecondFinalPromptDuringATurn_InterruptsTheFirstAndTheSecondAnswers()
        {
            // BlockingChatClient's gate ignores cancellation, so a defect here would hang rather than fail red,
            // hence the deadline below.
            using BlockingChatClient reply = new("first reply");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply);
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            List<string> first;
            List<string> second;
            try
            {
                await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "conversation-second-prompt"));
                await relay.SendAsync(RelayFrames.Prompt("one", last: true));

                try
                {
                    await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);
                    await relay.SendAsync(RelayFrames.Prompt("two", last: true));

                    // Turn one closes its reply as it is interrupted, with only what reached the relay.
                    first = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    Assert.Fail("the second prompt never interrupted the first reply within ten seconds.");
                    throw;
                }
            }
            finally
            {
                reply.Release();
            }

            try
            {
                second = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the second prompt never got its answer within ten seconds.");
                throw;
            }

            ConversationSession? session = await host.FindSessionAsync("conversation-second-prompt");
            Assert.NotNull(session);
            await Poll.UntilAsync(() => session!.LastTurn is { TurnIndex: 1 });
            await session!.FlushTranscriptAsync();

            Assert.Equal("first reply", string.Concat(second));
            Assert.Equal(2, reply.Calls);
            Assert.Equal(
                ["one", string.Concat(first), "two", "first reply"],
                session.Transcript.Select(message => message.Text).Where(text => text.Length > 0));
        }

        [Fact(Timeout = 30_000)]
        public async Task ATurnAfterABargeIn_AnswersNormally()
        {
            // AfterAnInterrupt_NoFurtherTextFrameReachesTheRelay proves a barge-in ends one turn cleanly. The conversation
            // itself must also go on: nothing else here shows the caller can speak again and get a real answer rather
            // than silence or a leftover interrupted reply.
            using BlockingChatClient reply = new("hello there caller");
            EventObservedLoggerProvider capture = new("InterruptReceived");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture));
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            try
            {
                await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "conversation-continues"));
                await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

                try
                {
                    _ = await relay.ReadFrameAsync().WaitAsync(bounded.Token);
                    await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                    await relay.SendAsync(RelayFrames.Interrupt("hello", durationMs: 200));
                    await capture.Observed.WaitAsync(bounded.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    Assert.Fail("the connection never reported the barge-in within ten seconds.");
                }
            }
            finally
            {
                reply.Release();
            }

            ConversationSession? session = await host.FindSessionAsync("conversation-continues");
            Assert.NotNull(session);

            TurnResult firstTurn = await TelnyxRelayBargeInTestSupport.WaitForTurnAsync(session!);
            Assert.Equal(TimeSpan.FromMilliseconds(200), firstTurn.Cut);

            await relay.SendAsync(RelayFrames.Prompt("go on", last: true));

            try
            {
                List<string> tokens = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
                Assert.Equal("hello there caller", string.Concat(tokens));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the turn after the barge-in never finished within ten seconds.");
            }

            Assert.Null(session!.LastTurn!.Cut);
        }

        // Once a later turn has started, an earlier turn's reply never changes. Turn one's
        // reply is all on the wire and the relay is still playing it; turn two has streamed only content with no
        // words. A barge-in then reports what the caller heard of turn one, too late: the engine refuses the cut,
        // the refusal is logged, turn one keeps its reply, and turn two speaks on.
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptAfterTheNextTurnStarted_LeavesTheTurnTheCallerHeardAsItWas()
        {
            using HeldPromptChatClient reply = new("first reply", "second reply")
            {
                SecondTurnOpensWithUnspokenContent = true,
            };
            EventObservedLoggerProvider interrupted = new("InterruptReceived");
            EventObservedLoggerProvider refused = new("CutRefused");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(interrupted).AddProvider(refused));
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "conversation-thinking-turn"));
            await relay.SendAsync(RelayFrames.Prompt("one", last: true));
            reply.ReleaseFirstTurn();

            ConversationSession? session = null;
            try
            {
                Assert.Equal("first reply", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
                session = await host.FindSessionAsync("conversation-thinking-turn");
                Assert.NotNull(session);
                await Poll.UntilAsync(() => session.LastTurn is not null);

                await relay.SendAsync(RelayFrames.Prompt("two", last: true));

                // Turn two has thought aloud once. The caller has heard nothing of it: reasoning carries no text frame.
                await reply.SecondTurnStarted.Task.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the second turn never reached its first thought within ten seconds.");
            }

            TurnResult? first = session!.LastTurn;
            Assert.NotNull(first);
            Assert.Null(first!.Cut);

            try
            {
                await relay.SendAsync(RelayFrames.Interrupt("first", durationMs: 640));
                await interrupted.Observed.WaitAsync(bounded.Token);
                await refused.Observed.WaitAsync(bounded.Token);

                // Read before the gate opens, while turn one is still the turn that finished last.
                Assert.Equal(LogLevel.Information, refused.Level);
                Assert.Equal(first.TurnIndex, session.LastTurn!.TurnIndex);
                Assert.Equal("first reply", session.LastTurn.ReplyText);
                Assert.Null(session.LastTurn.Cut);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported the barge-in within ten seconds.");
            }
            finally
            {
                reply.ReleaseSecondTurn();
            }

            try
            {
                List<string> second = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
                Assert.Equal("second reply", string.Concat(second));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail(
                    "the second turn's answer never reached the relay within ten seconds; the barge-in "
                    + "cut a turn the caller had only heard silence from.");
            }

            // Turn two ran to its own end. Nothing wrote the heard text onto it.
            await Poll.UntilAsync(() => session.LastTurn!.TurnIndex == first.TurnIndex + 1);
            Assert.Equal("second reply", session.LastTurn!.ReplyText);
            Assert.Null(session.LastTurn.Cut);
        }
    }
}

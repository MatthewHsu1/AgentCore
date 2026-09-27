using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// Interrupts that change nothing: no turn running, or a malformed frame the vendor sent by mistake.
    /// </summary>
    public sealed class TelnyxRelayInterruptRefusalTests
    {
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptWithNoTurnRunning_ChangesNothing()
        {
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply);
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Interrupt("nothing played", durationMs: 10));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            try
            {
                Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the turn after the ignored interrupt never finished within ten seconds.");
            }
        }

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptWithNoUtteranceUntilInterrupt_IsRefusedAndTheConversationContinues()
        {
            // Section 7.1's rule for an unknown frame type applies here too: a frame the vendor got
            // wrong must not drop the conversation. RelayFrames.Interrupt always emits both fields, so a raw
            // frame is sent by hand here, missing utteranceUntilInterrupt entirely — the one shape
            // HandleInterrupt's own guard exists to survive, since System.Text.Json deserializes a
            // missing non-nullable string as null rather than throwing, and ConversationSession.Interrupt
            // itself would otherwise take an ArgumentNullException straight into the read loop.
            using FragmentingChatClient reply = new("hello");
            EventObservedLoggerProvider capture = new("MalformedInterruptFrame");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture));
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendRawAsync(/*lang=json,strict*/ """{"type":"interrupt","durationUntilInterruptMs":10}""");

            try
            {
                await capture.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported refusing the malformed interrupt within ten seconds.");
            }

            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            try
            {
                Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the turn after the refused interrupt never finished within ten seconds.");
            }
        }

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptWithANegativeDuration_IsRefusedAndTheConversationContinues()
        {
            // The same guard, the other malformed shape it exists for: System.Text.Json enforces no
            // range check on durationUntilInterruptMs, so a negative value would otherwise reach
            // ConversationSession.Interrupt's own ArgumentOutOfRangeException uncaught and take the read loop
            // down with it. D28 forbids clamping it into something plausible instead of refusing it.
            using FragmentingChatClient reply = new("hello");
            EventObservedLoggerProvider capture = new("MalformedInterruptFrame");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture));
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendRawAsync(
                                     /*lang=json,strict*/
                                     """{"type":"interrupt","utteranceUntilInterrupt":"hello","durationUntilInterruptMs":-5}""");

            try
            {
                await capture.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported refusing the malformed interrupt within ten seconds.");
            }

            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            try
            {
                Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the turn after the refused interrupt never finished within ten seconds.");
            }
        }
    }
}

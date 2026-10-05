using AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary><see cref="TelnyxRelayInput"/> on its own: one reader, and a read loop that is never left waiting
    /// on an event nobody will read.</summary>
    public sealed class TelnyxRelayInputTests
    {
        private const int MalformedInterruptFrame = 13;

        private static readonly RelayFrame Setup = new RelayFrame.Setup(
            "session-1", "call-1", "control-1", "conversation-1", "+15550100", "+15550199", CustomParameters: null);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // IConversationInputPort.ListenAsync: one consumer for the life of the port.
        [Fact]
        public void ASecondListenAsync_Throws()
        {
            TelnyxRelayInput input = NewInput();
            _ = input.ListenAsync(Ct);

            _ = Assert.Throws<InvalidOperationException>(() => input.ListenAsync(Ct));
        }

        // AcceptAsync: the read loop awaits each frame's task, so a reader that stops must release every one.
        [Fact(Timeout = 30_000)]
        public async Task AReaderThatStops_ReleasesTheFramesItNeverRead()
        {
            TelnyxRelayInput input = NewInput();
            Task setup = input.AcceptAsync(Setup);
            Task prompt = input.AcceptAsync(new RelayFrame.Prompt("hello", "en", Last: true));
            IAsyncEnumerator<ConversationInput> reader = input.ListenAsync(Ct).GetAsyncEnumerator(Ct);
            Assert.True(await reader.MoveNextAsync());

            await reader.DisposeAsync();

            Assert.True(setup.IsCompleted);
            Assert.True(prompt.IsCompleted);
            Assert.True(input.AcceptAsync(new RelayFrame.Prompt("anyone?", "en", Last: true)).IsCompleted);
        }

        [Fact]
        public void AFrameAfterTheStreamEnded_IsReleasedAtOnce()
        {
            TelnyxRelayInput input = NewInput();
            input.Complete();

            Assert.True(input.AcceptAsync(Setup).IsCompleted);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptBeforeTheSetupFrame_BecomesNoEvent()
        {
            TelnyxRelayInput input = NewInput();

            Task interrupt = input.AcceptAsync(new RelayFrame.Interrupt("hello", 300));
            Task setup = input.AcceptAsync(Setup);
            input.Complete();

            Assert.True(interrupt.IsCompleted);
            List<ConversationInput> heard = [];
            await foreach (ConversationInput item in input.ListenAsync(Ct))
            {
                heard.Add(item);
            }

            _ = Assert.IsType<ConversationInput.Started>(Assert.Single(heard));
            await setup;
        }

        [Fact]
        public void MalformedInterrupts_AreLoggedOncePerConversation()
        {
            RecordingLoggerFactory logs = new();
            TelnyxRelayInput input = new(logs.CreateLogger("relay"), () => "conversation-1");
            _ = input.AcceptAsync(Setup);

            _ = input.AcceptAsync(new RelayFrame.Interrupt("hello", -1));
            _ = input.AcceptAsync(new RelayFrame.Interrupt("hello", -5));

            _ = Assert.Single(logs.Of(MalformedInterruptFrame));
        }

        private static TelnyxRelayInput NewInput()
        {
            return new TelnyxRelayInput(NullLogger.Instance, () => "conversation-1");
        }
    }
}

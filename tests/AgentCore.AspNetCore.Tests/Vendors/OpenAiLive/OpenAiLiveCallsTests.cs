using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The registry of live calls: how a call's run ends, and how the host's stop waits for it.</summary>
    public sealed class OpenAiLiveCallsTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A fault in a call's run (here the sideband's dispose) is logged, never left unobserved.
        [Fact(Timeout = 30_000)]
        public async Task AFaultInACallsRunIsLogged()
        {
            using EventObservedLoggerProvider runFaulted = new("RunFaulted");
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), []);
            using OpenAiLiveCalls calls = new(new ServiceCollection().BuildServiceProvider());
            FakeSideband sideband = new() { DisposeFault = new IOException("The socket was already gone.") };

            calls.Run(await StartedCallAsync(harness, sideband, _ => Task.FromResult(true), runFaulted.CreateLogger("test")));
            await calls.StopAsync(Ct);

            Assert.Equal(1, runFaulted.Count);
            Assert.True(sideband.Disposed);
        }

        // The host's stop token bounds the wait for a hang-up that never returns.
        [Fact(Timeout = 30_000)]
        public async Task AStopWhoseTokenFiresWhileAHangUpHangsReturns()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), []);
            using OpenAiLiveCalls calls = new(new ServiceCollection().BuildServiceProvider());
            TaskCompletionSource<bool> hangUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource hangingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource stop = new();
            calls.Run(await StartedCallAsync(harness, new FakeSideband(), _ => { hangingUp.TrySetResult(); return hangUp.Task; }, null));

            Task stopping = calls.StopAsync(stop.Token);
            await hangingUp.Task;
            await stop.CancelAsync();
            Exception? stopped = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            _ = hangUp.TrySetResult(true);

            Assert.IsAssignableFrom<OperationCanceledException>(stopped);
        }

        // The host's stop ends a live call the way a normal end does, so the line the caller was still
        // speaking is closed and raised before the conversation ends.
        [Fact(Timeout = 30_000)]
        public async Task AStopRaisesTheLineStillOpen()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook]);
            using OpenAiLiveCalls calls = new(new ServiceCollection().BuildServiceProvider());
            FakeSideband sideband = new();
            JsonObject heard = new() { ["type"] = OpenAiLiveEvents.InputTranscriptDelta, ["delta"] = " I still need", ["start_ms"] = 1000, ["end_ms"] = 2000 };
            calls.Run(await StartedCallAsync(harness, sideband, _ => Task.FromResult(true), null));

            sideband.Push([heard.ToJsonString()]);
            await sideband.WaitForDrainAsync();
            await calls.StopAsync(Ct);
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();
            _ = await hook.WaitForAsync<ConversationUnloaded>();

            Assert.Contains((Speaker.Caller, "I still need"), hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
            Assert.Equal((ConversationEndReason.Faulted, OpenAiLiveCall.ShutdownCause), (ended.Reason, ended.Call!.Cause));
        }

        private static async Task<OpenAiLiveCall> StartedCallAsync(
            PhoneCallHarness harness, FakeSideband sideband, Func<CancellationToken, Task<bool>> hangUp, ILogger? logger)
        {
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            return new OpenAiLiveCall(call, new LiveChannel(call.CallId, NullLogger.Instance), sideband, hangUp, logger ?? NullLogger.Instance);
        }
    }
}

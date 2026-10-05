using AgentCore.Application.Hooks;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// One started call whose <see cref="OpenAiLiveCall"/> loop reads a <see cref="FakeSideband"/>, on a clock that
    /// stands still at <see cref="Noon"/> in UTC.
    /// </summary>
    internal sealed class RunningLiveCall : IAsyncDisposable
    {
        public static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public const string Greeting = "Greet the caller, then pause and listen.";

        private RunningLiveCall(PhoneCallHarness harness, FakeTimeProvider time, PhoneCall call, FakeSideband sideband)
        {
            Harness = harness;
            Time = time;
            Call = call;
            Sideband = sideband;
            Loop = Task.CompletedTask;
        }

        public PhoneCallHarness Harness { get; }

        public FakeTimeProvider Time { get; }

        public PhoneCall Call { get; }

        public FakeSideband Sideband { get; }

        public Task Loop { get; private set; }

        /// <summary>Gets the hang-up, completed with how many events the test had pushed when the call hung up.</summary>
        public TaskCompletionSource<int> HungUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<RunningLiveCall> StartAsync(
            IChatClient model, IReadOnlyList<AgentHook> hooks, string yaml = PhoneCallHarness.OneEntryYaml, ILogger? logger = null, FakeSideband? sideband = null,
            string? greeting = Greeting)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            FakeTimeProvider time = new(Noon) { Zone = TimeZoneInfo.Utc };
            PhoneCallHarness harness = PhoneCallHarness.Create(model, hooks, yaml, time: time);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), ct)).Call!;
            await call.StartAsync(ct);
            RunningLiveCall running = new(harness, time, call, sideband ?? new FakeSideband());
            OpenAiLiveCall live = new(call, running.Sideband, running.HangUpAsync, logger ?? NullLogger.Instance, greeting);
            running.Loop = live.RunAsync(ct);
            return running;
        }

        public async ValueTask DisposeAsync()
        {
            Sideband.Complete();
            await Loop.WaitAsync(TimeSpan.FromSeconds(10));
            Harness.Dispose();
        }

        private Task<bool> HangUpAsync(CancellationToken cancellationToken)
        {
            _ = HungUp.TrySetResult(Sideband.Pushed);
            return Task.FromResult(true);
        }
    }
}

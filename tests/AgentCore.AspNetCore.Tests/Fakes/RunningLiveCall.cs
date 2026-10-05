using AgentCore.Application.Configuration.Schema;
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

        /// <summary>Gets each refer the call sent, with its target. It completes on the first.</summary>
        public TaskCompletionSource<Uri> Referred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Starts the call. With <paramref name="refers"/> unset the call cannot transfer, as on a host that wired no
        /// refer; set, each refer answers it.
        /// </summary>
        public static async Task<RunningLiveCall> StartAsync(
            IChatClient model, IReadOnlyList<AgentHook> hooks, string yaml = PhoneCallHarness.OneEntryYaml, ILogger? logger = null, FakeSideband? sideband = null,
            string? greeting = Greeting, Func<ToolConfiguration, AITool?>? tools = null, bool? refers = null)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            FakeTimeProvider time = new(Noon) { Zone = TimeZoneInfo.Utc };
            PhoneCallHarness harness = PhoneCallHarness.Create(model, hooks, yaml, time: time, tools: tools);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), ct)).Call!;
            await call.StartAsync(ct);
            RunningLiveCall running = new(harness, time, call, sideband ?? new FakeSideband());
            Func<Uri, CancellationToken, Task<bool>>? refer = refers is { } answer ? (target, _) => running.ReferAsync(target, answer) : null;
            OpenAiLiveCall live = new(call, running.Sideband, running.HangUpAsync, logger ?? NullLogger.Instance, greeting, refer);
            running.Loop = live.RunAsync(ct);
            return running;
        }

        public async ValueTask DisposeAsync()
        {
            Sideband.Complete();
            await Loop.WaitAsync(TimeSpan.FromSeconds(10));
            Harness.Dispose();
        }

        private Task<bool> ReferAsync(Uri target, bool answer)
        {
            _ = Referred.TrySetResult(target);
            return Task.FromResult(answer);
        }

        private Task<bool> HangUpAsync(CancellationToken cancellationToken)
        {
            _ = HungUp.TrySetResult(Sideband.Pushed);
            return Task.FromResult(true);
        }
    }
}

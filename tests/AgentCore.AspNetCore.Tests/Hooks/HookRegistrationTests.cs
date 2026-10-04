using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.DependencyInjection;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.Hooks
{
    public sealed class HookRegistrationTests
    {
        private static readonly DateTimeOffset FixedStart = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // UseHooks<T> resolves the hook once from DI; one instance serves every conversation.
        [Fact(Timeout = 60_000)]
        public async Task AHookTypeIsTheInstanceTheContainerHolds()
        {
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            _ = builder.Services.AddSingleton<RecordingHook>();
            ConfigureServices(builder.Services, OneAgentYaml, options => options.UseHooks<RecordingHook>());
            using StartedHost provider = await StartAsync(builder.Build());

            ConversationSession first = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("c1");
            ConversationSession second = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("c2");
            _ = await first.RunTurnAsync("hi", Ct);
            _ = await second.RunTurnAsync("hi", Ct);
            await first.FlushNoticesAsync();
            await second.FlushNoticesAsync();

            RecordingHook hook = provider.GetRequiredService<RecordingHook>();
            Assert.Equal(["c1", "c2"], hook.Of<ConversationStarted>().Select(n => n.Scope.ConversationId).Order(StringComparer.Ordinal));
        }

        [Fact(Timeout = 60_000)]
        public async Task UseHooksTwiceKeepsBothInOrder()
        {
            RecordingHook a = new();
            RecordingHook b = new();
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseHooks(a).UseHooks(b));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("c1");
            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            _ = Assert.Single(a.Of<ConversationStarted>());
            _ = Assert.Single(b.Of<ConversationStarted>());
        }

        // The host says it started and is stopping, stamped by the clock the options name.
        [Fact(Timeout = 60_000)]
        public async Task TheHostAnnouncesItsStartAndStop()
        {
            FakeTimeProvider clock = new(FixedStart);
            RecordingHook hook = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(builder.Services, OneAgentYaml, options =>
            {
                options.TimeProvider = clock;
                _ = options.UseHooks(hook);
            });
            IHost host = builder.Build();

            await host.StartAsync(Ct);
            HostStarted started = await hook.WaitForAsync<HostStarted>().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            clock.Advance(TimeSpan.FromMinutes(3));
            await host.StopAsync(Ct);
            host.Dispose();

            Assert.Equal(["main"], started.Entries);
            Assert.Equal(0, started.ToolCount);
            Assert.Null(started.Scope.ConversationId);
            Assert.Equal(FixedStart, started.Scope.OccurredAt);
            HostStopping stopping = Assert.Single(hook.Of<HostStopping>());
            Assert.Equal(FixedStart + TimeSpan.FromMinutes(3), stopping.Scope.OccurredAt);
        }

        // A host whose boot failed still stops: the boot's own error is the one the host sees.
        [Fact(Timeout = 60_000)]
        public async Task AHostWhoseBootFailedStillStops()
        {
            const string StoredYaml = $$"""
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only, instructions: "I answer everything" }
            {{MinimalProviders}}
              conversations: { kind: test }
            entries:
              main:
                agent: only
            """;
            RecordingHook hook = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(builder.Services, StoredYaml, options =>
            {
                _ = options.UseConversationStores(new DownStore());
                _ = options.UseHooks(hook);
            });
            using IHost host = builder.Build();

            InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Ct));
            await host.StopAsync(Ct);

            Assert.Equal(DownStore.Message, failed.Message);
            Assert.Empty(hook.Notices);
        }

        // The hourly sweep reports how many rows it deleted.
        [Fact(Timeout = 60_000)]
        public async Task TheRetentionSweepReportsWhatItDeleted()
        {
            const string SweptYaml = $$"""
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only, instructions: "I answer everything" }
            {{MinimalProviders}}
              conversations: { kind: test }
            entries:
              main:
                agent: only
            """;
            FakeTimeProvider clock = new(FixedStart);
            RecordingHook hook = new();
            using StartedHost provider = await BuildAsync(SweptYaml, options =>
            {
                _ = options.UseConversationStores(new SevenRowSweep(clock));
                options.TimeProvider = clock;
                options.ResponseRetention = TimeSpan.FromDays(30);
                _ = options.UseHooks(hook);
            });

            await clock.WaitForTimersAsync(clock.GetUtcNow() + TimeSpan.FromHours(1), 1);
            clock.Advance(TimeSpan.FromHours(1));
            RetentionSwept swept = await hook.WaitForAsync<RetentionSwept>().WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal(7, swept.Deleted);
            Assert.Equal(FixedStart + TimeSpan.FromHours(1), swept.Scope.OccurredAt);
        }

        // The container does not close until the queued host notices are delivered or the host's
        // shutdown timeout runs out.
        [Fact(Timeout = 30_000)]
        public async Task TheDrainWaitsForAHookUntilTheHostShutdownTimeout()
        {
            FakeTimeProvider clock = new(FixedStart);
            HoldingHook hook = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            _ = builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            ConfigureServices(builder.Services, OneAgentYaml, options =>
            {
                options.TimeProvider = clock;
                _ = options.UseHooks(hook);
            });
            IHost host = builder.Build();
            await host.StartAsync(Ct);
            DateTimeOffset stopped = clock.GetUtcNow();

            await host.StopAsync(Ct);
            await hook.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Task disposing = ((IAsyncDisposable)host).DisposeAsync().AsTask();
            await clock.WaitForTimersAsync(stopped + TimeSpan.FromSeconds(5), 1).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.False(disposing.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(5));
            await disposing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        // Flushing the audit queue first waits for every notice already raised to the hooks.
        [Fact(Timeout = 30_000)]
        public async Task FlushingTheAuditQueueWaitsForTheHooksFirst()
        {
            TurnHoldingHook hook = new();
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseHooks(hook));
            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("c1");
            _ = await session.RunTurnAsync("hi", Ct);
            await hook.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Task flushing = AddAgentCoreAuditTests.Queue(provider).FlushAsync(Ct).AsTask();
            bool waitedForTheHook = !flushing.IsCompleted;
            hook.Release();
            await flushing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.True(waitedForTheHook);
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted],
                AddAgentCoreAuditTests.Sink(provider).EventsOf("c1").Select(row => row.Kind));
        }

        private sealed class TurnHoldingHook : AgentHook
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => _entered.Task;

            public override TimeSpan? NoticeTimeout => null;

            public void Release() => _release.TrySetResult();

            public override async ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken)
            {
                _ = _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
        }

        private sealed class HoldingHook : AgentHook
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => _entered.Task;

            public override TimeSpan? NoticeTimeout => null;

            public override async ValueTask OnHostStoppingAsync(HostStopping notice, CancellationToken cancellationToken)
            {
                _ = _entered.TrySetResult();
                await new TaskCompletionSource().Task.ConfigureAwait(false);
            }
        }

        private sealed class DownStore : IConversationStoreAdapter
        {
            internal const string Message = "The conversation store is down.";

            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(Message);
            }
        }

        private sealed class SevenRowSweep(TimeProvider clock)
            : DelegatingConversationStore(new InMemoryConversationStore(clock)), IConversationStoreAdapter
        {
            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IConversationStore>(this);
            }

            public override ValueTask<int> SweepAsync(TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(7);
            }
        }
    }
}

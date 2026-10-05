using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.Application.Runtime.Session;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>The audit sink the composition root opens, queues, and drains on shutdown.</summary>
    public sealed class AddAgentCoreAuditTests
    {
        // The audit chain shuts down with the host. An event is ACCEPTED when AppendAsync returns, so a
        // stop that does not drain the queue loses every row still in it.
        [Fact]
        public async Task AddAgentCore_ClosesTheAuditStoreOnlyAfterTheQueueHasDrained()
        {
            (IHost? host, ClosingAuditSink? store) = await BuildAuditHostAsync();

            await host.StartAsync(TestContext.Current.CancellationToken);

            await host.Services
                .GetRequiredService<IAuditSinkPort>()
                .AppendAsync(AuditRow(1), TestContext.Current.CancellationToken);

            await host.StopAsync(TestContext.Current.CancellationToken);

            // The container closes what it resolved before it closes the boot that still owns the store
            // behind the queue. Closing that store first would hand the drain a store which can no
            // longer accept the rows it already promised to keep.
            host.Dispose();

            Assert.True(store.Closed);
            Assert.Equal(1, store.WrittenWhenClosed);
        }


        // The same agent, and a document that names the built-in memory kind on purpose.
        private const string MemoryAuditYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          audit: { kind: memory }
        entries:
          main:
            agent: only
        """;

        // The same agent, served by an audit vendor the host registers itself.
        private const string VendorAuditYaml =
            $$"""
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        {{MinimalProviders}}
          audit: { kind: test }
        entries:
          main:
            agent: only
        """;

        [Fact]
        public async Task TheDefaultAuditSink_ReachesTheTurnLoopAndTheChainVerifies()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The queue is what keeps the append off the turn, so the rows land on a thread of their own
            // and a reader that wants them now asks for them now.
            await Queue(provider).FlushAsync(TestContext.Current.CancellationToken);

            IReadOnlyList<AuditEvent> events = Sink(provider).EventsOf("conversation-1");
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted],
                events.Select(item => item.Kind).ToArray());
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        [Fact]
        public async Task ADocumentThatNamesNoAuditProvider_StillOpensTheMemorySink()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            // The turn loop produces audit events whatever a document says, so the seam that receives
            // them has a working default rather than a null. That is what lets every reading of a conversation be
            // unconditional, and what lets a first run and a test work with no database.
            Assert.NotNull(provider.GetService<IAuditSinkPort>());
            _ = Assert.IsType<InMemoryAuditSink>(provider.GetRequiredService<QueuedAuditSink>().Store);
        }

        [Fact]
        public async Task ADocumentThatNamesTheMemoryKind_OpensTheSameSinkAsNamingNothing()
        {
            using StartedHost provider = await BuildAsync(MemoryAuditYaml);

            // memory is this library's own name and it needs no registered vendor, so writing it says out
            // loud what leaving the block out does quietly. The startup warning is the difference.
            _ = Assert.IsType<InMemoryAuditSink>(provider.GetRequiredService<QueuedAuditSink>().Store);
        }

        [Fact]
        public async Task AnAuditVendorTheDocumentNames_IsTheStoreBehindTheQueue()
        {
            RecordingAuditSink store = new();
            using StartedHost provider = await BuildAsync(
                VendorAuditYaml,
                options => options.UseAuditSinks(new TestAuditSinkAdapter(store)));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            await Queue(provider).FlushAsync(TestContext.Current.CancellationToken);

            // The host lists its vendors once and providers.audit.kind picks one, exactly as the five
            // seams beside it. Nothing but the document decides which store the chain lands in.
            Assert.Same(store, provider.GetRequiredService<QueuedAuditSink>().Store);
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted],
                store.Events.Select(item => item.Kind).ToArray());
        }

        [Fact]
        public async Task AnAuditKindThisHostDoesNotRegister_FailsTheStart()
        {
            // A document that asked for something this host cannot give fails while the host starts, and
            // never on a conversation. The message names the kind, exactly as every other vendor seam.
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(VendorAuditYaml));

            Assert.Contains("test", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheAuditSink_IsWrappedInTheQueueThatKeepsItOffTheTurn()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            // A durable insert costs about 13 ms p50 against 91 nanoseconds to enqueue, so the queue is applied
            // once, here, to whatever the document opened. An adapter that blocks on its database is
            // therefore correct, and no adapter carries a queue of its own.
            _ = Assert.IsType<QueuedAuditSink>(provider.GetRequiredService<IAuditSinkPort>());
        }

        /// <summary>Reads back the queue the composition root put in front of the document's store.</summary>
        internal static QueuedAuditSink Queue(IServiceProvider provider)
        {
            return Assert.IsType<QueuedAuditSink>(provider.GetRequiredService<IAuditSinkPort>());
        }

        /// <summary>Reads back the store itself, which is registered under its own concrete type.</summary>
        internal static InMemoryAuditSink Sink(IServiceProvider provider)
        {
            return Assert.IsType<InMemoryAuditSink>(provider.GetRequiredService<QueuedAuditSink>().Store);
        }

        private static async Task<(IHost Host, ClosingAuditSink Store)> BuildAuditHostAsync()
        {
            ClosingAuditSink store = new();
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(
                builder.Services,
                VendorAuditYaml,
                options => options.UseAuditSinks(new TestAuditSinkAdapter(store)));

            return (builder.Build(), store);
        }

        /// <summary>One well-formed event, which is all a drain has to carry.</summary>
        /// <param name="secondsPastEpoch">Seconds past the epoch the event occurred at, so callers can order rows.</param>
        /// <returns>The event.</returns>
        private static AuditEvent AuditRow(long secondsPastEpoch)
        {
            return new()
            {
                ConversationId = "conversation-1",
                EventId = Guid.CreateVersion7(),
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(secondsPastEpoch),
            };
        }
    }
}

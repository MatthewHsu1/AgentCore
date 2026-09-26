using System.Net.WebSockets;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Sessions
{
    /// <summary>
    /// What the relay socket asks of <see cref="IConversationSessions"/> over the life of one conversation.
    /// </summary>
    /// <remarks>
    /// The unit tests of the store itself live in AgentCore.Application.Tests beside the store. This
    /// file holds only what needs a real socket to prove.
    /// </remarks>
    public sealed class ConversationSessionsRelayTests
    {
        private const string TwoEntryPolicyYaml =
            """
        apiVersion: agentcore/v1
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller" }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        entries:
          main:
            agent: greeter
          other:
            agent: greeter
        """;

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        // If TelnyxRelayConnection ever hard-coded "main" in place of the route's own entry, this would
        // open under "main" instead and the assertion below would fail.
        [Fact(Timeout = 30_000)]
        public async Task ARequestOnAnEntryOtherThanMainOpensItsSessionUnderThatEntry()
        {
            using FragmentingChatClient reply = new("hello");
            List<string> openedUnder = [];

            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TwoEntryPolicyYaml,
                reply,
                configure: options => options.UseConversationSessions(factories => new EntryRecordingConversationSessions(
                    new InMemoryConversationSessions(factories, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System),
                    openedUnder)),
                entry: "other");

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-other-entry"));
            harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));
            harness.Socket.QueueClose();
            await harness.Connection.WaitAsync(Token);

            Assert.Contains("other", openedUnder);
            Assert.DoesNotContain("main", openedUnder);
        }

        [Fact(Timeout = 30_000)]
        public async Task ASetupNamingAnIdAnotherEntryHoldsClosesAsAPolicyViolationAndLeavesThatSessionUntouched()
        {
            using FragmentingChatClient reply = new("hello");
            EventObservedLoggerProvider inUse = new("ConversationInUse");
            EventObservedLoggerProvider faulted = new("ReadLoopFaulted");

            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TwoEntryPolicyYaml,
                reply,
                logging: builder => builder.AddProvider(inUse).AddProvider(faulted),
                entry: "other");

            IConversationSessions sessions = harness.Services.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession held = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-held", null, Token);

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-held"));
            await harness.Connection.WaitAsync(Token);

            Assert.Equal(WebSocketCloseStatus.PolicyViolation, harness.Socket.CloseSent?.Status);
            Assert.Equal(1, inUse.Count);
            Assert.Equal(LogLevel.Warning, inUse.Level);
            Assert.Contains("conversation-held", inUse.Message, StringComparison.Ordinal);
            Assert.Equal(0, faulted.Count);

            QueuedAuditSink queue = Assert.IsType<QueuedAuditSink>(harness.Services.GetRequiredService<IAuditSinkPort>());
            await queue.FlushAsync(Token);
            IReadOnlyList<AuditEvent> chain = Assert.IsType<InMemoryAuditSink>(queue.Store).EventsOf("conversation-held");
            _ = Assert.Single(chain, item => item.Kind == AuditEventKind.ConversationStarted);
            Assert.DoesNotContain(chain, item => item.Kind == AuditEventKind.ConversationEnded);

            Assert.Same(held, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-held", Token));
            Assert.False(held.IsComplete);
        }

        [Fact(Timeout = 30_000)]
        public async Task ARefusedReopenAfterClosingTheReplacedSessionLeavesTheOtherEntrysSessionUntouched()
        {
            using FragmentingChatClient reply = new("hello");
            string root = Path.Combine(Path.GetTempPath(), "agentcore-session-race-" + Guid.NewGuid().ToString("N"));

            try
            {
                await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                    TwoEntryPolicyYaml,
                    reply,
                    configure: options =>
                    {
                        _ = options.UseWorkspace(root);
                        _ = options.UseConversationSessions(factories => new ForcedReopenConversationSessions(
                            new InMemoryConversationSessions(factories, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System),
                            closingEntry: "main",
                            racingEntry: "other"));
                    },
                    entry: "main");

                // Two setup frames for the same id: the loop closes its own hold, and in that window this
                // forces "other" to open the id before the loop's reopen is attempted.
                harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-race"));
                harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-race"));
                await harness.Connection.WaitAsync(Token);

                Assert.Equal(WebSocketCloseStatus.PolicyViolation, harness.Socket.CloseSent?.Status);

                ForcedReopenConversationSessions sessions = Assert.IsType<ForcedReopenConversationSessions>(
                    harness.Services.GetRequiredService<EntryRegistry>().Sessions);
                ConversationSession racedInto = sessions.RacedInto ?? throw new InvalidOperationException("the race never fired.");

                Assert.Same(racedInto, await sessions.TryGetAsync("other", "conversation-race", Token));
                Assert.False(racedInto.IsComplete);
                Assert.True(Directory.Exists(racedInto.Workspace));
                Assert.True(File.Exists(Path.Combine(racedInto.Workspace!, "sentinel.txt")));

                QueuedAuditSink queue = Assert.IsType<QueuedAuditSink>(harness.Services.GetRequiredService<IAuditSinkPort>());
                await queue.FlushAsync(Token);
                IReadOnlyList<AuditEvent> chain = Assert.IsType<InMemoryAuditSink>(queue.Store).EventsOf("conversation-race");
                Assert.DoesNotContain(chain, item => item.Kind == AuditEventKind.ConversationEnded);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact(Timeout = 30_000)]
        public async Task ATurnTellsTheStoreItsConversationIsStillBeingHad()
        {
            // The relay opens its session once and holds it for the whole conversation, so nothing else reads
            // it back. A read is the one sign a store gets that a conversation is still live, so without one
            // the idle timeout drops a long call out from under the turn about to run.
            using FragmentingChatClient reply = new("your order ships Friday");
            ConversationSessionFactory factory = Factory(TelnyxRelayTurnTests.PolicyYaml, reply);
            CountingConversationSessions sessions = new(factory);

            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                configure: options => options.UseConversationSessions(_ => sessions));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-live"));
            harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));

            for (int attempt = 0; attempt < 400 && sessions.Reads == 0; attempt++)
            {
                await Task.Delay(10, Token);
            }

            Assert.True(sessions.Reads > 0, "the turn never told the store its conversation is still being had.");
        }

        private static ConversationSessionFactory Factory(string yaml, IChatClient reply)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(chatClients))[SingleEntrySessionFactories.MainEntry];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients));
        }

        private sealed class CountingConversationSessions(IConversationSessionFactory factory) : IConversationSessions, IDisposable
        {
            private readonly InMemoryConversationSessions _inner = new(
                SingleEntrySessionFactories.Of(factory),
                InMemoryConversationSessions.DefaultIdleTimeout,
                TimeProvider.System);

            public void Dispose()
            {
                _inner.Dispose();
            }

            private int _reads;

            public int Reads => Volatile.Read(ref _reads);

            public ValueTask<ConversationSession> GetOrOpenAsync(string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
            {
                return _inner.GetOrOpenAsync(entry, conversationId, state, cancellationToken);
            }

            public ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                _ = Interlocked.Increment(ref _reads);
                return _inner.TryGetAsync(entry, conversationId, cancellationToken);
            }

            public ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return _inner.CloseAsync(entry, conversationId, cancellationToken);
            }
        }

        /// <summary>Sessions that record the entry every open named, so a test can read what the route actually sent.</summary>
        private sealed class EntryRecordingConversationSessions(InMemoryConversationSessions inner, List<string> openedUnder)
            : IConversationSessions, IDisposable
        {
            public void Dispose()
            {
                inner.Dispose();
            }

            public ValueTask<ConversationSession> GetOrOpenAsync(string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
            {
                lock (openedUnder)
                {
                    openedUnder.Add(entry);
                }

                return inner.GetOrOpenAsync(entry, conversationId, state, cancellationToken);
            }

            public ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return inner.TryGetAsync(entry, conversationId, cancellationToken);
            }

            public ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return inner.CloseAsync(entry, conversationId, cancellationToken);
            }
        }

        /// <summary>
        /// Forces the race a refused re-open can land in: once <paramref name="closingEntry"/>'s hold of an id is
        /// released, opens that same id under <paramref name="racingEntry"/> before the caller's own reopen runs,
        /// and drops a sentinel file into its workspace so a test can tell whether a later teardown reached it.
        /// </summary>
        private sealed class ForcedReopenConversationSessions(InMemoryConversationSessions inner, string closingEntry, string racingEntry)
            : IConversationSessions, IDisposable
        {
            public ConversationSession? RacedInto { get; private set; }

            public void Dispose()
            {
                inner.Dispose();
            }

            public ValueTask<ConversationSession> GetOrOpenAsync(string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
            {
                return inner.GetOrOpenAsync(entry, conversationId, state, cancellationToken);
            }

            public ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return inner.TryGetAsync(entry, conversationId, cancellationToken);
            }

            public async ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                await inner.CloseAsync(entry, conversationId, cancellationToken).ConfigureAwait(false);

                if (RacedInto is not null || !string.Equals(entry, closingEntry, StringComparison.Ordinal))
                {
                    return;
                }

                ConversationSession raced = await inner.GetOrOpenAsync(racingEntry, conversationId, null, cancellationToken).ConfigureAwait(false);
                if (raced.Workspace is { } path)
                {
                    File.WriteAllText(Path.Combine(path, "sentinel.txt"), "held by " + racingEntry);
                }

                RacedInto = raced;
            }
        }
    }
}

using AgentCore.TestSupport;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Conversation.Memory;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// How the chain of one conversation closes when the socket ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Section 11, item 6 and T55/T56: every conversation writes hash-chained events ending in
    /// <c>conversation.ended</c>, and the reason is one member of the closed set
    /// <see cref="ConversationEndReason"/> names. The turn loop closes its own chain when the stage machine
    /// reaches a terminal stage, and that is the only ending the core can see. Every other ending is
    /// the adapter's to write, because only the adapter sees the socket end — which is what
    /// <see cref="ConversationSession.EndConversation(ConversationEndReason)"/> says in its own remarks.
    /// </para>
    /// <para>
    /// Every test here drives <c>TelnyxRelayConnection.RunAsync</c> over <see cref="FakeWebSocket"/>
    /// rather than over a real port. The close of a conversation is exactly the moment a real socket stops
    /// being observable — <see cref="FakeRelayClient"/> aborts its own socket on the way out, so a
    /// graceful vendor close cannot be scripted over the wire at all — and the fake is what lets a test
    /// script the vendor's own close frame, a faulting write loop, and a host that stops, each on its
    /// own. Everything else about the connection is the real thing, including the session factory, the
    /// store, the observers, and the audit queue <c>AddAgentCore</c> registers.
    /// </para>
    /// <para>
    /// Every test here runs offline against a fake model. There is no Telnyx account, no network conversation,
    /// and no API key anywhere in this file. That is T59.
    /// </para>
    /// </remarks>
    public sealed class TelnyxRelayConversationEndTests
    {
        /// <summary>A document whose first turn moves the machine into a terminal stage.</summary>
        /// <remarks>
        /// The transition carries no guard, so one turn is enough to reach <c>close</c> and the turn
        /// loop closes the chain itself with <c>agent.completed</c>. That is the one ending teardown
        /// must not write over.
        /// </remarks>
        private const string TerminalStageYaml =
            """
          apiVersion: agentcore/v1
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller" }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close } ] }
                  - { id: close,    agent: closer,  terminal: true }
          providers:
            conversation:   { kind: telnyx-relay }
            speech:
              stt: { kind: telnyx-relay }
              tts: { kind: telnyx-relay }
            llm:
              - { kind: openai, model: gpt-4.1-mini, as: reply }
          """;

        [Fact(Timeout = 30_000)]
        public async Task ASocketTheRelayEnds_WritesOneConversationEndedThatNamesTheCallerHangup()
        {
            // The ordinary end of a conversation. The read loop sees the vendor's own close frame, teardown
            // picks NormalClosure, and the chain has to close on the reason a report counts years
            // later — not stop mid-chain with no terminal event at all.
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply);

            // One channel, read in order, and DispatchAsync awaits StartConversationAsync: the session exists
            // before the read loop ever sees the close behind it, so nothing here has to poll for it.
            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-hangup"));
            harness.Socket.QueueClose();

            await WaitForTeardownAsync(harness);

            IReadOnlyList<AuditEvent> events = await ReadChainAsync(harness, "conversation-hangup");

            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.ConversationEnded],
                events.Select(item => item.Kind).ToArray());
            Assert.Equal("caller.hangup", EndReasonOf(events));
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        [Fact(Timeout = 30_000)]
        public async Task AConnectionWhoseWriteLoopFaulted_WritesOneConversationEndedThatNamesTheFault()
        {
            // Nothing on a healthy socket makes a send throw, so the fault is injected. The write loop
            // faults, teardown picks InternalServerError, and the ending recorded must be the fault
            // rather than a hang-up nobody performed.
            using FragmentingChatClient reply = new("hello there caller");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply);

            harness.Socket.FailEverySend(new InvalidOperationException("the send failed."));
            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-write-faulted"));
            harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

            await WaitForTeardownAsync(harness);

            IReadOnlyList<AuditEvent> events = await ReadChainAsync(harness, "conversation-write-faulted");

            Assert.Equal(AuditEventKind.ConversationEnded, events[^1].Kind);
            _ = Assert.Single(events, item => item.Kind == AuditEventKind.ConversationEnded);
            Assert.Equal("conversation.faulted", EndReasonOf(events));
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        [Fact(Timeout = 30_000)]
        public async Task AHostThatStopsUnderALiveConversation_WritesOneConversationEndedThatNamesTheFault()
        {
            // The caller did not hang up: the process went away underneath them. The closed set of
            // section 4 holds four endings and this is not one of the other three, so the honest one is
            // the fault. Recording it as caller.hangup would have a report count a shutdown as a caller
            // choosing to leave.
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply);

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-host-stopping"));
            await WaitForSessionAsync(harness, "conversation-host-stopping");
            harness.StopApplication();

            await WaitForTeardownAsync(harness);

            IReadOnlyList<AuditEvent> events = await ReadChainAsync(harness, "conversation-host-stopping");

            Assert.Equal(AuditEventKind.ConversationEnded, events[^1].Kind);
            Assert.Equal("conversation.faulted", EndReasonOf(events));
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        [Fact(Timeout = 30_000)]
        public async Task AConversationThatAlreadyReachedItsTerminalStage_GetsNoSecondTerminalEvent()
        {
            // The turn loop closed this chain itself, with agent.completed and the stage that ended it.
            // EndConversation is idempotent, and this is the proof that the idempotence really holds through
            // the adapter's path: one conversation.ended in the chain, and the reason is the agent's, not the
            // socket's.
            using FragmentingChatClient reply = new("goodbye then");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(TerminalStageYaml, reply);

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-completed"));
            harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

            // The session's own completion flag, and never the last frame on the wire: the reply's
            // closing frame leaves before the turn loop commits the turn, so a test that closed the
            // socket on that frame would race the very event it is about to assert on.
            ConversationSession session = await WaitForCompletedConversationAsync(harness, "conversation-completed");
            Assert.True(session.IsComplete);

            harness.Socket.QueueClose();
            await WaitForTeardownAsync(harness);

            IReadOnlyList<AuditEvent> events = await ReadChainAsync(harness, "conversation-completed");

            _ = Assert.Single(events, item => item.Kind == AuditEventKind.ConversationEnded);
            Assert.Equal("agent.completed", EndReasonOf(events));
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        [Fact(Timeout = 30_000)]
        public async Task ASocketThatEndedBeforeTheSetupFrame_WritesNothingAndTearsDownCleanly()
        {
            // No setup frame ever arrived, so there is no conversation, no session, and nothing to close. A
            // chain with a conversation.ended and no conversation.started would be a record of a conversation that never
            // happened, and teardown must not throw its way out of the request handler either.
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply);

            harness.Socket.QueueClose();

            await WaitForTeardownAsync(harness);

            Assert.True(harness.Connection.IsCompletedSuccessfully);
            await Queue(harness).FlushAsync(TestContext.Current.CancellationToken);
            Assert.Empty(Sink(harness).Events);
        }

        [Fact(Timeout = 30_000)]
        public async Task AClockThatThrowsWhileTheChainCloses_StillTearsDownAndStillReleasesTheSession()
        {
            // Section 7.1: teardown never throws out of the request handler. The one input the closing
            // event reads that can throw is the clock, so it is the one a test can make throw. A throw
            // here must cost the chain its last event and nothing else — never the session close behind
            // it, which is what waits for the words the conversation's last turn still owed store 1.
            FaultingClock clock = new();
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                configure: options => options.TimeProvider = clock);

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-broken-clock"));
            await WaitForSessionAsync(harness, "conversation-broken-clock");

            // Armed only once the session exists: the session reads the clock as it is built, and a
            // clock that failed that read would end the test before the path it is meant to reach.
            clock.FailFromNowOn();
            harness.Socket.QueueClose();

            await WaitForTeardownAsync(harness);

            Assert.True(harness.Connection.IsCompletedSuccessfully);
            Assert.Null(await Sessions(harness).TryGetAsync("conversation-broken-clock", TestContext.Current.CancellationToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task EndConversation_HangUp_FlushesTranscriptBeforeSessionRemoval()
        {
            // Arrange
            //
            // Store 1 is written off the turn, so a conversation can end with its last words still in flight.
            // The session is the only thing that can wait for them, so once it leaves the store nothing
            // can: the record of that call would lose the turn the caller just had, and no error would
            // say so. Same shape as the teardown that removed a session without closing its chain.
            ParkingConversationStore transcript = new();
            using FragmentingChatClient reply = new("your order ships Friday");
            ConversationSessionFactory factory = TranscriptBackedSessions(TelnyxRelayTurnTests.PolicyYaml, reply, transcript);
            OrderedConversationSessions sessions = new(factory, () => transcript.Landed);
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                relay: options => options.CloseTimeout = TimeSpan.FromSeconds(30),
                configure: options => options.UseConversationSessions((_, _) => sessions));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-flush"));
            harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));
            await transcript.Parked.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            // Act
            harness.Socket.QueueClose();

            // The write is still parked, so a close that waits for it cannot return and this wait runs
            // out. A close that does not wait returns at once and the wait ends early — which is the
            // failure this test exists to catch, and why the release below comes after the wait.
            await WaitQuietlyAsync(sessions.Closed, TimeSpan.FromSeconds(2));
            transcript.Release();
            await WaitForTeardownAsync(harness);

            // Assert
            Assert.True(
                sessions.TranscriptLandedAtClose,
                "the close returned while store 1 still owed the conversation its last turn.");
            Assert.Null(await sessions.TryGetAsync("conversation-flush", TestContext.Current.CancellationToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task StartConversation_SecondSetupFrame_FlushesTheReplacedTranscriptBeforeDroppingIt()
        {
            // Arrange
            //
            // A second setup frame replaces the session rather than refusing the socket, and the first
            // one is dropped there and then. It is dropped by the same rule teardown obeys: the session
            // is the only thing that can wait for the words it queued, so the first conversation's record would
            // lose its last turn.
            ParkingConversationStore transcript = new();
            using FragmentingChatClient reply = new("your order ships Friday");
            ConversationSessionFactory factory = TranscriptBackedSessions(TelnyxRelayTurnTests.PolicyYaml, reply, transcript);
            OrderedConversationSessions sessions = new(factory, () => transcript.Landed);
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                relay: options => options.CloseTimeout = TimeSpan.FromSeconds(30),
                configure: options => options.UseConversationSessions((_, _) => sessions));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-first"));
            harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));
            await transcript.Parked.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            // Act
            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-second"));

            // Read as in EndConversation_HangUp_FlushesTranscriptBeforeSessionRemoval: the wait runs out while
            // the write is parked, and ends early when the drop did not wait for it.
            await WaitQuietlyAsync(sessions.Closed, TimeSpan.FromSeconds(2));
            transcript.Release();

            // Assert
            await WaitForSessionAsync(harness, "conversation-second");
            Assert.True(
                sessions.TranscriptLandedAtClose,
                "the close returned while store 1 still owed its call the last turn.");
        }

        [Fact(Timeout = 30_000)]
        public async Task EndConversation_StoreThatThrowsOnTheClose_LogsItAndStillTearsDown()
        {
            // Arrange
            //
            // IConversationSessions is a public seam, and the close is the last thing teardown does. The
            // in-memory store never fails, so a store held over the network is the only one that can
            // fail here — and its failure must not leave the request handler, which section 7.1 forbids.
            EventObservedLoggerProvider capture = new("ConversationCloseFaulted");
            using FragmentingChatClient reply = new("your order ships Friday");
            FaultingConversationSessions sessions = new(
                SessionsFor(TelnyxRelayTurnTests.PolicyYaml, reply),
                new InvalidOperationException("the store is gone."));
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture),
                configure: options => options.UseConversationSessions((_, _) => sessions));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-store-faulted"));

            // Act
            harness.Socket.QueueClose();
            await WaitForTeardownAsync(harness);

            // Assert
            //
            // WaitForTeardownAsync is what proves the throw stayed inside. The line is what proves the
            // failure was reported rather than swallowed into silence: nothing retries the removal, so
            // that session is in the store for the rest of the process and an operator has to hear it.
            await capture.Observed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(LogLevel.Error, capture.Level);
            Assert.True(sessions.CloseAttempted, "teardown never reached the close at all.");
        }

        [Fact(Timeout = 30_000)]
        public async Task EndConversation_StoreThatNeverAnswersTheClose_TimesOutRatherThanWedgingTeardown()
        {
            // Arrange
            //
            // The other half of the same seam. A store that stops answering is not a store that throws:
            // an unbounded wait here would hold this connection, its Kestrel request, and its
            // registration on ApplicationStopping for the life of the process, and every conversation after it
            // would do the same.
            EventObservedLoggerProvider capture = new("TeardownTimedOut");
            using FragmentingChatClient reply = new("your order ships Friday");
            HangingConversationSessions sessions = new(SessionsFor(TelnyxRelayTurnTests.PolicyYaml, reply));
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture),
                relay: options => options.CloseTimeout = TimeSpan.FromSeconds(1),
                configure: options => options.UseConversationSessions((_, _) => sessions));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-store-hung"));

            try
            {
                // Act
                harness.Socket.QueueClose();
                await WaitForTeardownAsync(harness);

                // Assert
                //
                // The text is read rather than the line alone: TeardownTimedOut is written about
                // whichever task ran out of time, and nothing else in this conversation is slow, so a match on
                // the name alone would still pass if the close were left unbounded and some other wait
                // timed out instead.
                await capture.Observed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Contains("the session close", capture.Message);
            }
            finally
            {
                // The close is still parked, and disposal below waits on the connection.
                sessions.Release();
            }
        }

        /// <summary>The same factory over the memory store, for a test with no store 1 of its own.</summary>
        /// <param name="yaml">The document to compile.</param>
        /// <param name="reply">The model behind every reference in it.</param>
        /// <returns>The factory.</returns>
        private static ConversationSessionFactory SessionsFor(string yaml, IChatClient reply)
        {
            return TranscriptBackedSessions(yaml, reply, new InMemoryConversationStore());
        }

        /// <summary>Builds the session factory of one document over a given store 1.</summary>
        /// <param name="yaml">The document, as YAML.</param>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="transcript">Where the words of a conversation are written.</param>
        /// <returns>The factory, ready to register over the one <c>AddAgentCore</c> built.</returns>
        /// <remarks>
        /// A document that names no providers.conversations compiles onto the memory store, so this is the
        /// only seam a test has for putting a slow store behind a conversation. It is why the whole factory is
        /// rebuilt rather than decorated: the store is compiled into the agent, and the factory holds
        /// the compiled agent.
        /// </remarks>
        private static ConversationSessionFactory TranscriptBackedSessions(
            string yaml, IChatClient reply, IConversationStore transcript)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { ConversationStore = transcript })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients));
        }

        /// <summary>Waits for one task, and treats running out of time as an answer rather than a fault.</summary>
        /// <param name="task">What to wait for.</param>
        /// <param name="bound">How long to give it.</param>
        /// <returns>A task that completes either way.</returns>
        private static async Task WaitQuietlyAsync(Task task, TimeSpan bound)
        {
            try
            {
                await task.WaitAsync(bound, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                // The wait running out is what this caller is asking about.
            }
        }

        /// <summary>Waits for one connection to finish its own teardown.</summary>
        /// <param name="harness">The running connection.</param>
        /// <returns>A task that completes once teardown has run to its end.</returns>
        /// <remarks>
        /// The connection task is awaited through a guard rather than directly, so a teardown that
        /// throws — which section 7.1 forbids — fails here by name instead of surfacing as whatever
        /// assertion happened to run next.
        /// </remarks>
        private static async Task WaitForTeardownAsync(RelayConnectionHarness harness)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            try
            {
                await harness.Connection.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never tore down within twenty seconds.");
            }
            catch (Exception fault)
            {
                Assert.Fail($"teardown threw out of the request handler, which section 7.1 forbids: {fault}");
            }
        }

        /// <summary>Waits until the store holds one conversation.</summary>
        /// <param name="harness">The running connection.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <returns>A task that completes once the session appears.</returns>
        private static async Task WaitForSessionAsync(RelayConnectionHarness harness, string conversationId)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (await Sessions(harness).TryGetAsync(conversationId, TestContext.Current.CancellationToken) is not null)
                {
                    return;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Fail($"the session of conversation '{conversationId}' never appeared.");
        }

        /// <summary>Waits until one conversation has closed its own chain from the terminal stage.</summary>
        /// <param name="harness">The running connection.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <returns>The completed session.</returns>
        private static async Task<ConversationSession> WaitForCompletedConversationAsync(RelayConnectionHarness harness, string conversationId)
        {
            for (int attempt = 0; attempt < 400; attempt++)
            {
                if (await Sessions(harness).TryGetAsync(conversationId, TestContext.Current.CancellationToken)
                    is { IsComplete: true } session)
                {
                    return session;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Fail($"the conversation '{conversationId}' never reached its terminal stage.");
            throw new InvalidOperationException("unreachable: Assert.Fail always throws.");
        }

        /// <summary>Flushes the queue and reads back the chain of one conversation.</summary>
        /// <param name="harness">The connection that has already torn down.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <returns>The events of that conversation, oldest first.</returns>
        /// <remarks>
        /// The queue is what keeps the append off the turn, so a reader that wants the rows now asks
        /// for them now. Nothing here waits on the chain being non-empty: every test that calls this
        /// has already waited for the teardown that writes the last event.
        /// </remarks>
        private static async Task<IReadOnlyList<AuditEvent>> ReadChainAsync(
            RelayConnectionHarness harness,
            string conversationId)
        {
            await Queue(harness).FlushAsync(TestContext.Current.CancellationToken);
            return Sink(harness).EventsOf(conversationId);
        }

        /// <summary>Reads the end reason of the last event of one chain.</summary>
        /// <param name="events">The chain.</param>
        /// <returns>The wire token under <see cref="AuditPayloadKeys.EndReason"/>.</returns>
        private static string EndReasonOf(IReadOnlyList<AuditEvent> events)
        {
            AuditEvent ended = Assert.Single(events, item => item.Kind == AuditEventKind.ConversationEnded);
            return ended.Payload[AuditPayloadKeys.EndReason];
        }

        /// <summary>Reads back the queue the composition root put in front of the store.</summary>
        private static QueuedAuditSink Queue(RelayConnectionHarness harness)
        {
            return Assert.IsType<QueuedAuditSink>(harness.Services.GetRequiredService<IAuditSinkPort>());
        }

        /// <summary>Reads back the store the chain lands in.</summary>
        private static InMemoryAuditSink Sink(RelayConnectionHarness harness)
        {
            return Assert.IsType<InMemoryAuditSink>(harness.Services.GetRequiredService<QueuedAuditSink>().Store);
        }

        /// <summary>Reads back the live sessions.</summary>
        private static IConversationSessions Sessions(RelayConnectionHarness harness)
        {
            return harness.Services.GetRequiredService<EntryRegistry>().ForSessions("main");
        }
    }

    /// <summary>
    /// A clock a test breaks on demand.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider.GetUtcNow"/> is the one call <c>ConversationSession.EndConversation</c> makes that can
    /// throw at all: the reason is a member of a closed set the connection picks itself, and the
    /// dispatcher behind the event swallows everything an observer raises. Breaking the clock is
    /// therefore the only way a test can reach the guard that keeps section 7.1's promise — teardown
    /// never throws out of the request handler.
    /// </remarks>
    internal sealed class FaultingClock : TimeProvider
    {
        private volatile bool _failing;

        /// <summary>Makes every later reading of the wall clock throw.</summary>
        public void FailFromNowOn()
        {
            _failing = true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Only this reading fails. The timestamps a turn measures with and the timers the pump's idle
        /// deadline runs on are left alone, so a broken clock ends nothing but the one event under test.
        /// </remarks>
        public override DateTimeOffset GetUtcNow()
        {
            return _failing
                        ? throw new InvalidOperationException("the clock failed.")
                        : base.GetUtcNow();
        }
    }

    /// <summary>
    /// The live sessions, with a note of what store 1 had done by the time a close returned.
    /// </summary>
    internal sealed class OrderedConversationSessions(IConversationSessionFactory factory, Func<bool> transcriptLanded)
        : IConversationSessions
    {
        private readonly InMemoryConversationSessions _inner =
            new(factory, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);

        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes when a session has finished closing.</summary>
        public Task Closed => _closed.Task;

        /// <summary>Gets whether store 1 had written the conversation's words by the time the close returned.</summary>
        public bool? TranscriptLandedAtClose { get; private set; }

        /// <inheritdoc />
        public ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default)
        {
            return _inner.OpenAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return _inner.TryGetAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            await _inner.CloseAsync(conversationId, cancellationToken);
            TranscriptLandedAtClose ??= transcriptLanded();
            _ = _closed.TrySetResult();
        }
    }

    /// <summary>Sessions that open normally and fail only when asked to close one.</summary>
    /// <param name="factory">Builds the sessions this holds.</param>
    /// <param name="fault">What the close throws.</param>
    /// <remarks>
    /// It throws before its first await rather than from inside an async body, which is the harder of
    /// the two for teardown to catch: a synchronous throw out of a <see cref="ValueTask"/> method lands
    /// at the conversation site, not on the returned task.
    /// </remarks>
    internal sealed class FaultingConversationSessions(IConversationSessionFactory factory, Exception fault) : IConversationSessions
    {
        private readonly InMemoryConversationSessions _inner =
            new(factory, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);

        /// <summary>Gets whether teardown ever reached the close.</summary>
        public bool CloseAttempted { get; private set; }

        /// <inheritdoc />
        public ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default)
        {
            return _inner.OpenAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return _inner.TryGetAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            CloseAttempted = true;
            throw fault;
        }
    }

    /// <summary>Sessions whose close never answers until a test releases it.</summary>
    internal sealed class HangingConversationSessions(IConversationSessionFactory factory) : IConversationSessions
    {
        private readonly InMemoryConversationSessions _inner =
            new(factory, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default)
        {
            return _inner.OpenAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return _inner.TryGetAsync(conversationId, cancellationToken);
        }

        /// <inheritdoc />
        /// <remarks>
        /// It ignores <paramref name="cancellationToken"/> on purpose. Teardown passes
        /// <see cref="CancellationToken.None"/> there, so a store that honoured a token would prove
        /// nothing about the bound teardown puts on the wait itself.
        /// </remarks>
        public async ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            await _release.Task;
            await _inner.CloseAsync(conversationId, CancellationToken.None);
        }

        /// <summary>Lets the parked close finish.</summary>
        public void Release()
        {
            _ = _release.TrySetResult();
        }
    }
}

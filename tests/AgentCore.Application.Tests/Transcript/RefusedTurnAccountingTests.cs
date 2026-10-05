using System.Diagnostics.Metrics;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// A turn the store refused leaves the same failure counter and log line as a turn that committed: the run that
    /// faulted or answered nothing is counted and logged once, built-in hooks and all. The counter is process-wide, so
    /// the class runs alone.
    /// </summary>
    [Collection(TelemetryHookSuite.Name)]
    public sealed class RefusedTurnAccountingTests
    {
        private const string ConversationId = "c-refused-accounting";
        private const string FailureInstrument = "agentcore.turn.failures";
        private const string FailureKey = "agentcore.failure.kind";

        // EventIds from Diagnostics/Log.cs.
        private const int EmptyReplyEvent = 3;
        private const int TurnRunFaultedEvent = 28;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 30_000)]
        public async Task ARefusedTurnWhoseRunFaulted_IsCountedAndLoggedAsARunFaultOnce()
        {
            HeldFirstTurnChatClient reply = new("I am Alice") { FaultsHeldRequest = true };
            using RecordingLoggerFactory logs = new();
            using FailureCounter counter = new();

            Exception? refused = await RunRefusedTurnAsync(reply, logs);

            _ = Assert.IsType<ConversationTurnConflictException>(refused);
            Assert.Equal(1, counter.Of("run"));
            Assert.Equal(0, counter.Of("empty_reply"));
            CapturedLine line = Assert.Single(logs.Of(TurnRunFaultedEvent));
            Assert.Equal(LogLevel.Error, line.Level);
            _ = Assert.IsType<HttpRequestException>(line.Exception);
        }

        [Fact(Timeout = 30_000)]
        public async Task ARefusedTurnThatAnsweredNothing_IsCountedAndLoggedAsAnEmptyReplyOnce()
        {
            HeldFirstTurnChatClient reply = new("I am Alice") { EmptiesHeldRequest = true };
            using RecordingLoggerFactory logs = new();
            using FailureCounter counter = new();

            Exception? refused = await RunRefusedTurnAsync(reply, logs);

            _ = Assert.IsType<ConversationTurnConflictException>(refused);
            Assert.Equal(1, counter.Of("empty_reply"));
            Assert.Equal(0, counter.Of("run"));
            _ = Assert.Single(logs.Of(EmptyReplyEvent));
        }

        [Fact(Timeout = 30_000)]
        public async Task ACommittedTurnWhoseRunFaulted_IsStillCountedAndLoggedOnce()
        {
            using DownModelChatClient model = new();
            using RecordingLoggerFactory logs = new();
            using FailureCounter counter = new();
            ConversationSession session = TurnObservabilityHarness.Build(
                ConversationSessionTestSupport.OneAgentYaml, model, null, logger: logs.CreateLogger("session")).Create(ConversationId);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(1, counter.Of("run"));
            _ = Assert.Single(logs.Of(TurnRunFaultedEvent));
        }

        private static async Task<Exception?> RunRefusedTurnAsync(HeldFirstTurnChatClient reply, RecordingLoggerFactory logs)
        {
            ILogger logger = logs.CreateLogger("session");
            ConversationSessionFactory factory = ConversationSessionRefusedCatchUpTests.Build(
                reply,
                new ConversationSessionRefusedCatchUpTests.UnmarkableStore(new InMemoryConversationStore()),
                logger,
                BuiltInHooks.Create(new InMemoryAuditSink(), logger));
            ConversationSession a = factory.Create(ConversationId);
            ConversationSession b = factory.Create(ConversationId);

            Task<TurnResult> alice = a.RunTurnAsync("I am Alice", Ct);
            await reply.Held.Task.WaitAsync(Ct);
            _ = await b.RunTurnAsync("I am Bob", Ct);
            reply.Release();
            Exception? refused = await Record.ExceptionAsync(() => alice);
            await a.FlushNoticesAsync();
            await b.FlushNoticesAsync();
            return refused;
        }

        /// <summary>Counts <c>agentcore.turn.failures</c> by kind from construction on.</summary>
        private sealed class FailureCounter : IDisposable
        {
            private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
            private readonly MeterListener _listener = new();

            public FailureCounter()
            {
                _listener.InstrumentPublished = (instrument, active) =>
                {
                    if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal)
                        && string.Equals(instrument.Name, FailureInstrument, StringComparison.Ordinal))
                    {
                        active.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
                {
                    KeyValuePair<string, object?>[] copy = tags.ToArray();
                    string kind = copy.First(tag => string.Equals(tag.Key, FailureKey, StringComparison.Ordinal)).Value?.ToString() ?? string.Empty;
                    lock (_counts)
                    {
                        _counts[kind] = _counts.GetValueOrDefault(kind) + value;
                    }
                });
                _listener.Start();
            }

            public long Of(string kind)
            {
                lock (_counts)
                {
                    return _counts.GetValueOrDefault(kind);
                }
            }

            public void Dispose()
            {
                _listener.Dispose();
            }
        }
    }
}

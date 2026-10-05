using System.Net.WebSockets;
using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>How <c>TelnyxRelayConnection</c> tears down when the voice loop, the relay, or the close itself fails.</summary>
    public sealed class TelnyxRelayConnectionFaultTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // TelnyxRelayConnection.DetermineCloseStatus: a fault in the voice loop closes the socket as a read-loop fault does.
        [Fact(Timeout = 30_000)]
        public async Task AVoiceLoopThatFaults_ClosesWithInternalServerError()
        {
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                configure: options => options.UseConversationSessions(_ => new UnopenableConversationSessions()));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-unopenable"));
            await harness.Connection.WaitAsync(Ct);

            Assert.Equal(WebSocketCloseStatus.InternalServerError, harness.Socket.CloseSent?.Status);
        }

        // TelnyxRelayConnection.ClassifyTelnyxFault: a relay that drops with no close frame is the vendor's doing, so the
        // conversation ends as a hang-up and no defect is logged.
        [Fact(Timeout = 30_000)]
        public async Task ARelayThatDropsWithNoCloseFrame_EndsAsACallerHangUpAndLogsNoDefect()
        {
            using FragmentingChatClient reply = new("hello");
            EventObservedLoggerProvider defect = new("ReadLoopFaulted");
            EventObservedLoggerProvider dropped = new("ConversationDroppedWithNoCloseFrame");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(defect).AddProvider(dropped));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-dropped"));
            harness.Socket.FailReceive(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
            await harness.Connection.WaitAsync(Ct);

            Assert.Equal(WebSocketCloseStatus.NormalClosure, harness.Socket.CloseSent?.Status);
            Assert.Equal("caller.hangup", await EndReasonAsync(harness, "conversation-dropped"));
            Assert.True(dropped.Observed.IsCompleted);
            Assert.False(defect.Observed.IsCompleted, "a dropped relay was logged as a read-loop defect.");
        }

        // A drop, or a close handshake that already ran, can reach the write loop before the read loop: it ends as the read
        // loop's drop does, as a hang-up with no defect logged. A send that fails any other way stays a fault.
        [Theory(Timeout = 30_000)]
        [InlineData(WebSocketState.Aborted)]
        [InlineData(WebSocketState.Closed)]
        public async Task ASendThatFindsTheSocketGone_EndsAsACallerHangUpAndLogsNoDefect(WebSocketState gone)
        {
            using FragmentingChatClient reply = new("hello there caller");
            EventObservedLoggerProvider defect = new("WriteLoopFaulted");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(defect));
            harness.Socket.GoneOnSend(gone);

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-gone-on-send"));
            harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));
            await harness.Connection.WaitAsync(Ct);

            Assert.Equal("caller.hangup", await EndReasonAsync(harness, "conversation-gone-on-send"));
            Assert.False(defect.Observed.IsCompleted, "a socket that was already gone was logged as a write-loop defect.");
        }

        // A send the teardown cancelled aborts a managed socket under the close that follows. There is nothing left to
        // close, and that is not a fault.
        [Fact(Timeout = 30_000)]
        public async Task ACloseThatFindsTheSocketAbortedUnderIt_LogsNoFault()
        {
            using FragmentingChatClient reply = new("hello");
            EventObservedLoggerProvider fault = new("CloseFaulted");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(fault));
            harness.Socket.AbortDuringClose();

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-aborted-close"));
            harness.Socket.QueueClose();
            await harness.Connection.WaitAsync(Ct);

            Assert.Equal("caller.hangup", await EndReasonAsync(harness, "conversation-aborted-close"));
            Assert.False(fault.Observed.IsCompleted, "a close on a socket already aborted was logged as a fault.");
        }

        // The relay stops reading, so a reply fills the outbound queue, and then the relay drops. Teardown must not
        // wait on a speech blocked on that queue: the socket is gone and nothing queued can be sent. The voice clock is
        // fake, so the interruption backstop never frees it; only the real close timeout would.
        [Fact(Timeout = 30_000)]
        public async Task ADropWhileAReplyIsBlockedOnAFullQueue_TearsDownWithoutWaitingOnThatReply()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using FragmentingChatClient reply = new(string.Join(' ', Enumerable.Range(0, 600).Select(word => "w" + word)));
            EventObservedLoggerProvider timedOut = new("TeardownTimedOut");
            EventObservedLoggerProvider stuck = new("SpeechNotDoneAfterInterruption");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(timedOut).AddProvider(stuck),
                configure: options => options.TimeProvider = clock);
            harness.Socket.StallSends();

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-stalled-drop"));
            harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

            // 600 words against a queue of 256 and one stalled send: the reply is blocked on the full queue by now.
            await harness.Socket.SendStalled.WaitAsync(Ct);
            await reply.Streamed.WaitAsync(Ct);

            harness.Socket.FailReceive(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
            await harness.Connection.WaitAsync(Ct);

            Assert.False(timedOut.Observed.IsCompleted, "teardown waited out its close timeout on a reply blocked on the queue.");
            Assert.False(stuck.Observed.IsCompleted);
            Assert.Equal("caller.hangup", await EndReasonAsync(harness, "conversation-stalled-drop"));
        }

        // Teardown never throws out of the request handler, so a close that fails still ends the conversation.
        [Fact(Timeout = 30_000)]
        public async Task ACloseThatThrows_StillEndsTheConversationAndReleasesItsSession()
        {
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply);
            harness.Socket.FailClose(new InvalidOperationException("the close failed."));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-close-faulted"));
            harness.Socket.QueueClose();
            await harness.Connection.WaitAsync(Ct);

            Assert.Equal("caller.hangup", await EndReasonAsync(harness, "conversation-close-faulted"));
            Assert.Null(await harness.Services.GetRequiredService<EntryRegistry>().Sessions
                .TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-close-faulted", Ct));
        }

        // VoiceConversationLoop.KeepSessionAliveAsync: a store that cannot be read on a turn is logged, and the turn still runs.
        [Fact(Timeout = 30_000)]
        public async Task AStoreThatCannotBeTouchedOnATurn_LeavesTheConversationRunning()
        {
            using FragmentingChatClient reply = new("hello");
            EventObservedLoggerProvider touch = new("SessionTouchFaulted");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(touch),
                configure: options => options.UseConversationSessions(
                    factories => new UntouchableConversationSessions(factories[SingleEntrySessionFactories.MainEntry])));

            harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-untouchable"));
            harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));
            harness.Socket.QueueClose();
            await harness.Connection.WaitAsync(Ct);

            Assert.True(touch.Observed.IsCompleted);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, harness.Socket.CloseSent?.Status);
        }

        private static async Task<string> EndReasonAsync(RelayConnectionHarness harness, string conversationId)
        {
            QueuedAuditSink queue = Assert.IsType<QueuedAuditSink>(harness.Services.GetRequiredService<IAuditSinkPort>());
            await queue.FlushAsync(Ct);
            InMemoryAuditSink sink = Assert.IsType<InMemoryAuditSink>(harness.Services.GetRequiredService<QueuedAuditSink>().Store);
            AuditEvent ended = Assert.Single(
                sink.EventsOf(conversationId), item => item.Kind == AuditEventKind.ConversationEnded);
            return ended.Payload[AuditPayloadKeys.EndReason];
        }

        /// <summary>Sessions that open and close normally, and throw on every read.</summary>
        private sealed class UntouchableConversationSessions(IConversationSessionFactory factory) : IConversationSessions, IDisposable
        {
            private readonly InMemoryConversationSessions _inner = new(
                SingleEntrySessionFactories.Of(factory),
                InMemoryConversationSessions.DefaultIdleTimeout,
                TimeProvider.System);

            public void Dispose()
            {
                _inner.Dispose();
            }

            public ValueTask<ConversationSession> GetOrOpenAsync(string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
            {
                return _inner.GetOrOpenAsync(entry, conversationId, state, cancellationToken);
            }

            public ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromException<ConversationSession?>(new InvalidOperationException("the store is gone."));
            }

            public ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return _inner.CloseAsync(entry, conversationId, cancellationToken);
            }
        }

        /// <summary>Sessions a conversation can never be opened in.</summary>
        private sealed class UnopenableConversationSessions : IConversationSessions
        {
            public ValueTask<ConversationSession> GetOrOpenAsync(string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromException<ConversationSession>(new InvalidOperationException("the store is gone."));
            }

            public ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<ConversationSession?>(null);
            }

            public ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}

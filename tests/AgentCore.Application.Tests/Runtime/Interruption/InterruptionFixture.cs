using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.InterruptionSessions;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Drives one streaming turn on a background task, far enough to interrupt it.
    /// </summary>
    internal sealed class InterruptionFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly IReadOnlyList<IDisposable> _disposables;
        private readonly Action _openGate;
        private readonly TaskCompletionSource _firstUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _run;

        private InterruptionFixture(ConversationSession session, IChatClient reply, Action openGate, CancellationToken hostToken)
        {
            Session = session;
            _disposables = [reply];
            _openGate = openGate;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            // The run reads its own token, _cancellation.Token, from inside PumpAsync, so the token
            // Task.Run offers here is deliberately unused.
            _run = Task.Run(PumpAsync, CancellationToken.None);
        }

        /// <summary>Builds a fixture over a turn that runs directly, with no streaming pump behind it.</summary>
        private InterruptionFixture(ConversationSession session, IReadOnlyList<IDisposable> disposables, CancellationToken hostToken)
        {
            Session = session;
            _disposables = disposables;
            _openGate = static () => { };
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            // RunTurnAsync drives the turn itself, on the caller's own conversation. There is no round to
            // stream and gate, so there is nothing here for a background pump to do.
            _run = Task.CompletedTask;
        }

        /// <summary>Gets the session the turn runs on.</summary>
        public ConversationSession Session { get; }

        /// <summary>Starts a turn over a plain text reply, gated after its first fragment.</summary>
        public static InterruptionFixture Start(string reply)
        {
            ScriptedChatClient client = new(reply.Split(' ')) { GateAfterFirstFragment = true };
            ConversationSession session = CreateSession(NoToolYaml, client);
            return new InterruptionFixture(session, client, client.OpenGate, TestContext.Current.CancellationToken);
        }

        /// <summary>Starts a turn whose tool call and result finish before the reply that follows them.</summary>
        public static InterruptionFixture StartWithFinishedTool(string reply)
        {
            GatedToolThenReplyChatClient client = new(reply);
            ConversationSession session = CreateSession(ToolYaml, client, new StubToolBuilder(/*lang=json,strict*/ """{ "price": 50 }""").Create);
            return new InterruptionFixture(session, client, client.OpenGate, TestContext.Current.CancellationToken);
        }

        /// <summary>Starts a turn whose tool round also carries the prose the model spoke before the call.</summary>
        public static InterruptionFixture StartWithProseBesideTool(string reply)
        {
            ProseBesideToolChatClient client = new(reply);
            ConversationSession session = CreateSession(ToolYaml, client, new StubToolBuilder(/*lang=json,strict*/ """{ "price": 50 }""").Create);
            return new InterruptionFixture(session, client, client.OpenGate, TestContext.Current.CancellationToken);
        }

        /// <summary>Builds a session whose extractor model never answers, so the deadline of §8.7 must act.</summary>
        public static InterruptionFixture StartWithHangingExtractor(string reply)
        {
            ScriptedChatClient replyClient = new(reply.Split(' '));
            HangingChatClient extractorClient = new();
            RoutingChatClientFactory chatClients = new RoutingChatClientFactory(replyClient).Route("fill", extractorClient);

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ExtractorYaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(chatClients))["main"];
            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients));

            return new InterruptionFixture(
                factory.Create(),
                [replyClient, extractorClient],
                TestContext.Current.CancellationToken);
        }

        /// <summary>Runs one turn directly on the session, the way a host that never streams drives it.</summary>
        public Task<TurnResult> RunTurnAsync(string userInput)
        {
            return Session.RunTurnAsync(userInput, _cancellation.Token);
        }

        /// <summary>Gets the extraction failure the last finished turn recorded, or <see langword="null"/>.</summary>
        public string? LastExtractionFailure => Session.LastTurn?.ExtractionFailure;

        /// <summary>Waits for the first update, interrupts with what the caller heard, and returns the finished turn.</summary>
        public async Task<TurnResult> InterruptAfterFirstUpdateAsync(string heard)
        {
            await _firstUpdate.Task.WaitAsync(_cancellation.Token).ConfigureAwait(false);

            // Section 7.1: the relay reports both the heard text and the played duration together.
            _ = Session.Cut(0, new TurnCut(heard, TimeSpan.FromMilliseconds(1820)));
            _openGate();

            await _run.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            Assert.NotNull(Session.LastTurn);
            return Session.LastTurn;
        }

        public async ValueTask DisposeAsync()
        {
            // A fact that stops before it interrupts must not leave the background task hanging.
            _openGate();
            _ = _firstUpdate.TrySetResult();

            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The host token already ended the run. Nothing here is left to observe.
            }

            _cancellation.Dispose();
            foreach (IDisposable disposable in _disposables)
            {
                disposable.Dispose();
            }
        }

        private async Task PumpAsync()
        {
            await foreach (ChatResponseUpdate? update in Session.RunTurnStreamingAsync("hi", _cancellation.Token)
                .ConfigureAwait(false))
            {
                // The tool-call and tool-result updates carry no TextContent, so only the reply
                // itself opens the window in which a test may interrupt.
                if (update.Contents.OfType<TextContent>().Any(text => text.Text.Length > 0))
                {
                    _ = _firstUpdate.TrySetResult();
                }
            }
        }
    }
}

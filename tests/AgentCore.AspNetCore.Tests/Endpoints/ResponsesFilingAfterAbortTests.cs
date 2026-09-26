using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// Filing after the client aborts runs on no host token, so it needs a bound of its own: the one the
    /// rest of the work after the reply runs under (design section 7, item 4).
    /// </summary>
    public sealed class ResponsesFilingAfterAbortTests
    {
        private static readonly TimeSpan FailureGuard = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task AFilingThatNeverEndsIsStoppedByTheBoundAndLogged()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            using RecordingLoggerFactory logs = new();
            TaskCompletionSource never = new();

            Task filing = ResponsesTurnStream.FileAfterAbortAsync(
                _ => never.Task, "conv-f5", time, logs.CreateLogger("test"));

            time.Advance(ConversationSession.TurnCompletionTimeout - TimeSpan.FromTicks(1));
            Assert.False(filing.IsCompleted);

            time.Advance(TimeSpan.FromTicks(1));
            await filing.WaitAsync(FailureGuard, TestContext.Current.CancellationToken);

            CapturedLine line = Assert.Single(logs.Of(1));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal("conv-f5", line.Field<string>("ConversationId"));
        }

        // Design section 3: the turn commits even when the host cancels mid-stream, so the session is still
        // filed, on a token that outlives the abort. A store that honours the aborted token would refuse it.
        [Fact]
        public async Task AStreamTheClientAborted_StillFilesTheSession()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            TokenHonouringStore store = new();
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(TwoStepChatClient.Yaml),
                new AgentCompilationContext(new RoutingChatClientFactory(new StallingChatClient())) { ConversationStore = store })["main"];
            InMemoryConversationSessions sessions = new(
                new Dictionary<string, IConversationSessionFactory>(StringComparer.Ordinal)
                {
                    ["main"] = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)),
                },
                TimeSpan.FromMinutes(30),
                TimeProvider.System);
            AgentCoreAgent agent = new(sessions, "main");
            AgentSession session = await agent.CreateSessionAsync(ct);
            ResponsesTurn turn = new(
                agent,
                new AgentCoreAgentSessionStore(store),
                session,
                session.GetService<ConversationSession>()!,
                new ChatMessage(ChatRole.User, "hi"),
                Origin: null,
                ResponseId: "resp-aborted",
                ConversationId: null);
            DefaultHttpContext http = new() { RequestServices = new ServiceCollection().BuildServiceProvider() };
            http.Response.Body = new MemoryStream();
            using CancellationTokenSource aborted = new();
            await aborted.CancelAsync();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ResponsesTurnStream.WriteAsync(http, turn, dialect: false, aborted.Token));

            Assert.NotNull(await store.GetContinuationAsync("resp-aborted", ct));
        }

        [Fact]
        public async Task AFilingThatEndsInTimeLogsNothing()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            using RecordingLoggerFactory logs = new();

            await ResponsesTurnStream.FileAfterAbortAsync(
                _ => Task.CompletedTask, "conv-f5", time, logs.CreateLogger("test"));

            Assert.Empty(logs.Lines);
        }

        private sealed class TokenHonouringStore() : DelegatingConversationStore(new InMemoryConversationStore())
        {
            public override ValueTask SaveContinuationAsync(
                string continuationId, string conversationId, JsonElement envelope, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return base.SaveContinuationAsync(continuationId, conversationId, envelope, cancellationToken);
            }
        }
    }
}

using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A model that does not answer is a fault of the run, not of a tool: the fallback layer catches both, and only
    /// a fault a tool threw spends a tool's budget.
    /// </summary>
    public sealed class ConversationSessionModelFaultTests
    {
        private const string ConversationId = "c-model-down";

        /// <summary>The event id of <c>Log.ToolBudgetSpent</c>.</summary>
        private const int ToolBudgetSpentEvent = 2;

        /// <summary>The event id of <c>Log.TurnRunFaulted</c>.</summary>
        private const int TurnRunFaultedEvent = 28;

        [Fact(Timeout = 30_000)]
        public async Task AModelThatIsDown_BeforeAnyToolCall_IsReportedAsARunFault()
        {
            using DownModelChatClient model = new();
            InMemoryAuditSink sink = new();
            RecordingHook observed = new();
            using RecordingLoggerFactory logs = new();
            ConversationSession session = Build(model, sink, observed, logs.CreateLogger("session"));

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            await session.FlushNoticesAsync();

            Assert.Equal("the turn's run faulted, so it spoke the fallback. 503 from the model endpoint", turn.Failure);
            Assert.DoesNotContain(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ToolFailed);
            TurnCompleted completed = Assert.Single(observed.Of<TurnCompleted>());
            Assert.Equal((TurnOutcome.Fallback, false), (completed.Outcome, completed.FailedInTool));
            Assert.Empty(logs.Of(ToolBudgetSpentEvent));
            CapturedLine line = Assert.Single(logs.Of(TurnRunFaultedEvent));
            Assert.Equal(LogLevel.Error, line.Level);
            _ = Assert.IsType<HttpRequestException>(line.Exception);
        }

        private static ConversationSession Build(
            IChatClient model, InMemoryAuditSink sink, RecordingHook observed, ILogger logger)
        {
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(InterruptionSessions.NoToolYaml),
                new AgentCompilationContext(new FakeChatClientFactory(model)))["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                logger: logger,
                hooks: [.. BuiltInHooks.Create(sink, logger), observed]).Create(ConversationId);
        }
    }
}

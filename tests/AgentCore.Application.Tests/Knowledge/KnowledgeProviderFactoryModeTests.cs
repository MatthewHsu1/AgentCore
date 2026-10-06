using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    /// <summary>
    /// The layer that decides, per agent, how retrieval reaches the model — and the only layer that can
    /// enforce the two per-agent settings the one-method port cannot carry.
    /// </summary>
    public sealed class KnowledgeProviderFactoryModeTests
    {
        [Fact]
        public async Task Create_PrefetchMode_RetrievesBeforeTheModelIsCalled()
        {
            StubKnowledgePort port = new([Card("a"), Card("b")]);

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch));
            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            Assert.Null(context.Tools);
            Assert.Equal("the screen says e33", port.LastQuery);
            Assert.Contains("card a", Merged(context), StringComparison.Ordinal);
            Assert.Contains("card b", Merged(context), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_ToolMode_OffersASearchToolInstead()
        {
            StubKnowledgePort port = new([Card("a")]);

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Tool));
            AIContext context = await provider.InvokingAsync(
                Invoking("hello", new StubSession()), TestContext.Current.CancellationToken);

            Assert.NotNull(context.Tools);
            _ = Assert.Single(context.Tools);
            Assert.Equal(0, port.Calls);
        }

        [Fact]
        public async Task Create_PortThrows_InjectsANoticeAndDoesNotFailTheTurn()
        {
            // A16. The framework's own behaviour here is SILENT fail-open: a throwing delegate
            // injects nothing at all and logs nothing, and the model then answers "E03 means
            // overheating" from its own weights. So the delegate must catch and say so.
            AIContextProvider provider = Provider(
                new ThrowingKnowledgePort(new InvalidOperationException("qdrant is down")),
                Resolved(KnowledgeMode.Prefetch));

            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            string text = Merged(context);
            Assert.Contains("knowledge base", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unreachable", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not answer from memory", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_TheStoresOwnDeadlineFires_InjectsTheNoticeAndWritesTheCause()
        {
            // A16 asks this delegate to catch every exception AND its own deadline. The store links that
            // deadline into the caller's token, and the channel reports a cancelled gRPC call as
            // OperationCanceledException -- so a hung Qdrant arrives as the same type a caller cancel
            // does. Excluding the type outright let the deadline straight through: no notice, no record,
            // no log line, and a model answering "E33 means the incline motor" from its own weights.
            RecordingLoggerFactory loggers = new();

            AIContextProvider provider = KnowledgeProviderFactory.Create(
                new HangingKnowledgePort(TimeSpan.FromMilliseconds(20)),
                Resolved(KnowledgeMode.Prefetch),
                "resolver",
                new SourceLocatorCitationFormatter(),
                loggers);

            AIContext context = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            string text = Merged(context);
            Assert.Contains("unreachable", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not answer from memory", text, StringComparison.OrdinalIgnoreCase);

            CapturedLine line = Assert.Single(loggers.Of(12));
            Assert.Equal(LogLevel.Error, line.Level);
            Assert.Equal("resolver", line.Field<string>("Agent"));
            _ = Assert.IsType<OperationCanceledException>(line.Exception, exactMatch: false);
        }

        [Fact]
        public async Task Create_TheCallerCancels_IsNotDressedUpAsARetrievalFailure()
        {
            // The other half of the same classifier, and the reason it cannot simply catch the type: a
            // hung-up caller produces no answer, so a notice has nowhere to land and an Error row would
            // fire on every abandoned turn of every agent. The port raises its exception carrying the
            // LINKED token here, exactly as the real store does, so nothing but the caller's own token
            // separates this case from the deadline above.
            RecordingLoggerFactory loggers = new();
            using CancellationTokenSource caller = new();

            AIContextProvider provider = KnowledgeProviderFactory.Create(
                new HangingKnowledgePort(TimeSpan.FromMinutes(5)),
                Resolved(KnowledgeMode.Prefetch),
                "resolver",
                new SourceLocatorCitationFormatter(),
                loggers);

            caller.CancelAfter(TimeSpan.FromMilliseconds(20));

            StubSession callerSession = new();
            TurnRegistry.Set(callerSession, PrefetchTurn());

            // The framework swallows whatever escapes the delegate, so "propagated" is read off what did
            // NOT happen: no notice, and neither the success row nor the failure row. A delegate that
            // returned normally would have written row 11, and one that took the failure path row 12.
            AIContext context = await provider.InvokingAsync(Invoking("the screen says e33", callerSession), caller.Token);

            Assert.DoesNotContain("unreachable", Merged(context), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(loggers.Of(11));
            Assert.Empty(loggers.Of(12));
        }

        [Fact]
        public async Task Create_PortReturnsNothing_InjectsNothing()
        {
            // The score floor is the gate. An empty list must not produce an empty "Additional
            // Context" block that costs tokens and says nothing.
            AIContextProvider provider = Provider(
                new StubKnowledgePort([]), Resolved(KnowledgeMode.Prefetch));

            AIContext context = await InvokePrefetchAsync(provider, "hello", PrefetchTurn(), new StubSession());

            Assert.Equal(["hello"], Texts(context));
        }
    }
}

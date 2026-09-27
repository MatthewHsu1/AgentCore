using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Knowledge.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class KnowledgeProviderFactoryQueryTests
    {
        [Fact]
        public async Task Create_WhatAnEarlierProviderInjected_IsNotPartOfTheQuery()
        {
            // The knowledge provider is bound second, after TurnContextProvider, and the framework
            // hands each provider what the one before it produced. The turn's own instructions are not
            // a query, and a search over them retrieves the wrong cards.
            StubKnowledgePort port = new([]);
            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch));
            StubSession session = new();
            TurnRegistry.Set(session, PrefetchTurn());

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            AIContextProvider.InvokingContext context = new(
                StubAgent.Instance,
                session,
                new AIContext
                {
                    Messages =
                    [
                        new ChatMessage(ChatRole.User, "the screen says e33"),
                        new ChatMessage(ChatRole.System, "ask the caller for the machine model")
                            .WithAgentRequestMessageSource(
                                AgentRequestMessageSourceType.AIContextProvider, "TurnContextProvider"),
                    ],
                });
#pragma warning restore MAAI001

            _ = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

            Assert.Equal("the screen says e33", port.LastQuery);
        }

        [Fact]
        public async Task Create_ASecondTurn_SearchesWithWhatTheCallerSaidBefore()
        {
            // RecentMessageMemoryLimit defaults to 0, and at 0 turn two's query is the bare new
            // message: "does it need a part?" resolves its pronoun against nothing.
            StubKnowledgePort port = new([Card("a")]);
            StubSession session = new();

            AIContextProvider provider = Provider(port, Resolved(KnowledgeMode.Prefetch));
            TurnRegistry.Set(session, PrefetchTurn());
            _ = await provider.InvokingAsync(
                Invoking("the screen says e33", session), TestContext.Current.CancellationToken);
            await provider.InvokedAsync(
                Invoked("the screen says e33", session), TestContext.Current.CancellationToken);
            _ = await provider.InvokingAsync(
                Invoking("does it need a part?", session), TestContext.Current.CancellationToken);

            Assert.Contains("the screen says e33", port.LastQuery!, StringComparison.Ordinal);
            Assert.Contains("does it need a part?", port.LastQuery!, StringComparison.Ordinal);
        }

        /// <summary>Closes a turn, so the provider stores what it should remember of it.</summary>
        private static AIContextProvider.InvokedContext Invoked(string text, AgentSession session)
        {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            return new(
                StubAgent.Instance,
                session,
                [new ChatMessage(ChatRole.User, text)],
                [new ChatMessage(ChatRole.Assistant, "let me look")]);
#pragma warning restore MAAI001
        }
    }
}

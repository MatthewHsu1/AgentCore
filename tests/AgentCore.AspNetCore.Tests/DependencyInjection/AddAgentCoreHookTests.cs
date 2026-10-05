using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>The host's own hooks, bound through the composition root.</summary>
    public sealed class AddAgentCoreHookTests
    {
        [Fact]
        public async Task AHostHook_HearsTheFactsOfATurn()
        {
            RecordingHook first = new();
            RecordingHook second = new();
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseHooks(first, second));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            // Every hook of a conversation hears the same facts, and the library's own are neither replaced nor bypassed.
            _ = Assert.Single(first.Of<ConversationStarted>());
            _ = Assert.Single(first.Of<TurnCompleted>());
            _ = Assert.Single(second.Of<ConversationStarted>());
            _ = Assert.Single(second.Of<TurnCompleted>());
        }

        [Fact]
        public async Task AHostHookThatThrows_CostsNeitherTheTurnNorTheChain()
        {
            using StartedHost provider = await BuildAsync(
                OneAgentYaml,
                options => options.UseHooks(new ThrowingHook()));

            ConversationSession session = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("conversation-1");
            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // A notice hook records the conversation and is never a part of it. That holds for the host's own, and
            // it holds for the readings registered beside it: a broken host hook does not cost the
            // audit chain a single row.
            Assert.Equal("hello", turn.ReplyText);

            await AddAgentCoreAuditTests.Queue(provider).FlushAsync(TestContext.Current.CancellationToken);

            IReadOnlyList<AuditEvent> events = AddAgentCoreAuditTests.Sink(provider).EventsOf("conversation-1");
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted],
                events.Select(item => item.Kind).ToArray());
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        /// <summary>A hook that refuses every notice it is offered, so the isolation of the seam is observable.</summary>
        private sealed class ThrowingHook : AgentHook
        {
            public override ValueTask OnConversationStartedAsync(ConversationStarted notice, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("the host's hook is broken");
            }

            public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("the host's hook is broken");
            }
        }
    }
}

using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>The idle timeout of the sessions a host gets when it binds none of its own.</summary>
    public sealed class DefaultSessionExpiryTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task TheDefaultSessionsUnloadAConversationLeftIdleOnTheHostClock()
        {
            // Without this the text path holds every conversation a caller walked away from for the life of
            // the process. A caller who simply stops replying reaches no terminal stage, so the idle timer is
            // the only thing that ever frees the session; the conversation itself stays open.
            FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.TimeProvider = clock);
            InMemoryConversationSessions sessions = Assert.IsType<InMemoryConversationSessions>(
                provider.GetRequiredService<EntryRegistry>().Sessions);
            _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            clock.Advance(InMemoryConversationSessions.DefaultIdleTimeout - TimeSpan.FromSeconds(1));
            Assert.Equal(1, sessions.Count);

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(0, sessions.Count);
        }

        [Fact]
        public async Task StoppingTheHostStopsTheIdleTimers()
        {
            FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
            StartedHost provider = await BuildAsync(OneAgentYaml, options => options.TimeProvider = clock);
            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            provider.Dispose();
            clock.Advance(InMemoryConversationSessions.DefaultIdleTimeout);

            Assert.Same(session, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
        }
    }
}

using AgentCore.TestSupport;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Domain;
using Xunit;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// The lifecycle of one conversation's session: opened, found again, and closed.
    /// </summary>
    public sealed class ConversationSessionsTests
    {
        [Fact]
        public async Task ASessionThatWasOpenedIsFoundAgainUnderItsConversationId()
        {
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), TimeSpan.FromMinutes(30), Clock());

            ConversationSession opened = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            Assert.Same(opened, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
        }

        [Fact]
        public async Task OpeningTheSameIdAgainUnderTheSameEntryReturnsTheLiveSessionAndOneCloseReleasesIt()
        {
            // Two opens of the same id under the same entry share one build and return the same session.
            using GatedSessionFactory factory = new(Factory());
            factory.Release();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(factory), TimeSpan.FromMinutes(30), Clock());

            ConversationSession first = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);
            ConversationSession second = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            Assert.Same(first, second);
            Assert.Equal(1, factory.Calls);

            await sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token);

            Assert.Equal(0, sessions.Count);
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => first.RunTurnAsync("hello", Token));
        }

        [Fact]
        public async Task ClosingAConversationWaitsForTheWordsItStillOwes()
        {
            // A real store answers over a network, so a write outlives the turn that queued it. This
            // session is the only thing that can wait for it, and closing is the last moment anything can.
            ParkingConversationStore transcript = new();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory(transcript)), TimeSpan.FromMinutes(30), Clock());
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            Task<TurnResult> turn = session.RunTurnAsync("hello", Token);
            await transcript.Parked;

            Task closing = sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token).AsTask();
            bool returnedEarly = await Task.WhenAny(closing, Task.Delay(200, Token)) == closing;
            Assert.False(returnedEarly);

            transcript.Release();
            await closing;

            Assert.True(transcript.Landed);
            Assert.Null(await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
            _ = await turn;
        }

        [Fact]
        public async Task ASecondCloseOnTheSameIdWaitsForTheFirstsTeardownBeforeReturning()
        {
            ParkingConversationStore transcript = new();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory(transcript)), TimeSpan.FromMinutes(30), Clock());
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            Task<TurnResult> turn = session.RunTurnAsync("hello", Token);
            await transcript.Parked;

            Task first = sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token).AsTask();
            Task second = sessions.CloseAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token).AsTask();

            bool secondReturnedEarly = await Task.WhenAny(second, Task.Delay(200, Token)) == second;
            Assert.False(secondReturnedEarly, "the second close must wait for the first's teardown, not return on its own.");

            transcript.Release();
            await first;
            await second;
            _ = await turn;

            Assert.True(transcript.Landed, "the second close returned before the teardown it waited for was done.");
            Assert.Null(await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
        }
    }
}

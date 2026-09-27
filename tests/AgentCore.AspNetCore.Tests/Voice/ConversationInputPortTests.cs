using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// The two promises <see cref="IConversationInputPort"/> makes that a signature cannot: one consumer for
    /// the life of the port, and a cancelled read that throws rather than passing for the end of a conversation.
    /// </summary>
    public sealed class ConversationInputPortTests
    {
        [Fact(Timeout = 30_000)]
        public async Task ASecondListenAsync_ThrowsRatherThanGivingEachReaderHalfTheConversation()
        {
            FakeConversationInput input = new(
                new ConversationInput.Utterance("hello", "en", IsFinal: true),
                new ConversationInput.Keypress("1"),
                new ConversationInput.Barge("hel", TimeSpan.FromMilliseconds(240)));

            List<ConversationInput> heard = [];
            await foreach (ConversationInput item in input.ListenAsync(TestContext.Current.CancellationToken))
            {
                heard.Add(item);
            }

            // One ordered stream carries every kind, and the reader takes them in the order they
            // happened rather than one stream per kind.
            Assert.Equal(3, heard.Count);
            Assert.Equal(new ConversationInput.Utterance("hello", "en", IsFinal: true), heard[0]);
            Assert.Equal(new ConversationInput.Keypress("1"), heard[1]);
            Assert.Equal(new ConversationInput.Barge("hel", TimeSpan.FromMilliseconds(240)), heard[2]);

            // Thrown by the conversation itself, and not by enumerating what it returns: an iterator's body
            // does not run until something reads it, so a guard that waited for the first MoveNext
            // would let a second reader walk away holding a stream that never says no.
            _ = Assert.Throws<InvalidOperationException>(
                () => input.ListenAsync(TestContext.Current.CancellationToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task ACancelledRead_ThrowsRatherThanPassingForTheEndOfTheConversation()
        {
            // The difference a consumer cannot see for itself: an await foreach that simply ends means
            // the conversation is over, so a port that swallowed its own consumer's cancellation would report a
            // conversation that ended when the conversation is still up.
            FakeConversationInput input = new(new ConversationInput.Keypress("1"));
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (ConversationInput item in input.ListenAsync(cancellation.Token))
                {
                    Assert.Fail($"a cancelled read yielded {item}.");
                }
            });
        }
    }
}

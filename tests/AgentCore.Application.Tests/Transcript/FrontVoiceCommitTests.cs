using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.AgentCoreChatHistoryProviderTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// A phone turn that carries what the caller and the vendor's own voice said ahead of it: those lines are the
    /// turn's first rows, in speech order, and never the agent's reply. Words from a real GPT-Live call.
    /// </summary>
    public sealed class FrontVoiceCommitTests
    {
        private static readonly IReadOnlyList<ChatMessage> SaidBefore =
        [
            new(ChatRole.User, "Uh, who are you"),
            FrontVoice.Line("You're speaking with Sole Fitness's virtual assistant."),
            new(ChatRole.User, "Um, do you know what day it is today"),
            FrontVoice.Line("It's Wednesday."),
        ];

        [Fact]
        public async Task CommitTurn_WritesTheLinesSaidBeforeAheadOfTheUser_AndNamesTheUserRow()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession session) = await NewConversation();
            provider.BeginTurn(session, 0);

            TurnWrite? written = provider.CommitTurn(session, EndTheCall());
            await provider.DrainAsync(session);

            Assert.Equal(
                [
                    ("user", null, "Uh, who are you"),
                    ("assistant", FrontVoice.AuthorName, "You're speaking with Sole Fitness's virtual assistant."),
                    ("user", null, "Um, do you know what day it is today"),
                    ("assistant", FrontVoice.AuthorName, "It's Wednesday."),
                    ("user", null, "Okay, uh please end the call now"),
                    ("assistant", null, "Today is Sunday. Goodbye!"),
                ],
                store.Rows.Select(row => (row.Content.Role.Value, row.Content.AuthorName, row.Content.Text)));
            Assert.Equal(1, store.Appends);
            Assert.Equal(("d3", store.Rows[^1].MessageId), (written!.UserMessageId, written.ReplyMessageId));
            Assert.Equal("d3", store.Rows[4].MessageId);
        }

        // The turn's reply hash and TurnCompleted's reply are the agent's words only.
        [Fact]
        public async Task CommitTurn_SpokenHoldsOnlyTheAgentsReply()
        {
            (AgentCoreChatHistoryProvider provider, _, StubSession session) = await NewConversation();
            provider.BeginTurn(session, 0);

            TurnWrite? written = provider.CommitTurn(session, EndTheCall());

            Assert.Equal("Today is Sunday. Goodbye!", written!.Spoken);
        }

        // A cut of the agent's reply lays the heard words over the agent's own rows, never over a line already spoken.
        [Fact]
        public async Task RewriteReply_LeavesTheFrontVoiceLinesAsTheyWere()
        {
            (AgentCoreChatHistoryProvider provider, RecordingConversationStore store, StubSession session) = await NewConversation();
            provider.BeginTurn(session, 0);
            _ = provider.CommitTurn(session, EndTheCall());

            bool rewritten = provider.RewriteReply(session, 0, "Today is");
            await provider.DrainAsync(session);

            Assert.True(rewritten);
            Assert.Equal(
                ["Uh, who are you", "You're speaking with Sole Fitness's virtual assistant.", "Um, do you know what day it is today", "It's Wednesday.", "Okay, uh please end the call now", "Today is"],
                store.Live(ConversationId).Select(row => row.Content.Text));
        }

        private static TurnCommit EndTheCall()
        {
            return new(new ChatMessage(ChatRole.User, "Okay, uh please end the call now"))
            {
                Before = SaidBefore,
                Seen = new AgentResponse(new ChatMessage(ChatRole.Assistant, "Today is Sunday. Goodbye!")),
                UserMessageId = "d3",
            };
        }
    }
}

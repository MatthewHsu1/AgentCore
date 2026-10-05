using AgentCore.Application.Conversation.Actions;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Application.Tools;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    public sealed class ConversationActionsTests
    {
        private static readonly Uri Staff = new("tel:+15550002222");

        [Fact]
        public async Task AnEndActionEndsTheConversationWithItsReason()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            Assert.Equal(ConversationActionResult.Scheduled, session.Request(new EndConversationAction(ConversationEndReason.TransferredToHuman)));
            await session.FlushNoticesAsync();

            Assert.Equal(ConversationEndReason.TransferredToHuman, Assert.Single(hook.Of<ConversationEnded>()).Reason);
        }

        [Fact]
        public void AnEndingConversationTakesNoActionAndNeverAsksTheChannel()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);
            RecordingChannel channel = new(ConversationActionResult.Scheduled);
            session.Actions.Attach(channel);
            _ = session.EndConversation(ConversationEndReason.CallerHungUp);

            Assert.Equal(ConversationActionResult.Ending, session.Request(new EndConversationAction(ConversationEndReason.AgentCompleted)));
            Assert.Equal(ConversationActionResult.Ending, session.Request(new TransferAction(Staff)));
            Assert.Empty(channel.Seen);
        }

        [Fact]
        public void ATransferWithNoChannelIsNotSupportedAndLeavesTheConversationOpen()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);

            Assert.Equal(ConversationActionResult.NotSupported, session.Request(new TransferAction(Staff)));
            Assert.False(session.IsComplete);
        }

        [Fact]
        public void TheAttachedChannelTakesATransfer()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);
            RecordingChannel channel = new(ConversationActionResult.Scheduled);
            TransferAction transfer = new(Staff);

            session.Actions.Attach(channel);
            Assert.Equal(ConversationActionResult.Scheduled, session.Request(transfer));
            Assert.Same(transfer, Assert.Single(channel.Seen));
        }

        // A newer call that takes the conversation over carries its actions from then on.
        [Fact]
        public void ANewerChannelReplacesTheOlderOne()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);
            RecordingChannel older = new(ConversationActionResult.Scheduled);
            RecordingChannel newer = new(ConversationActionResult.Scheduled);
            session.Actions.Attach(older);
            session.Actions.Attach(newer);

            Assert.Equal(ConversationActionResult.Scheduled, session.Request(new TransferAction(Staff)));
            Assert.Single(newer.Seen);
            Assert.Empty(older.Seen);
        }

        [Fact]
        public void AScopeBuiltOutsideATurnCanDoNothing()
        {
            ToolCallScope scope = new("c-1", 0, string.Empty);

            Assert.Equal(ConversationActionResult.NotSupported, scope.Conversation.Request(new EndConversationAction(ConversationEndReason.AgentCompleted)));
        }

        [Theory]
        [InlineData("tel:+15550002222")]
        [InlineData("sip:agent@example.com")]
        [InlineData("sips:agent@example.com")]
        public void ATransferTakesTelSipAndSipsTargets(string target)
        {
            Assert.Equal(new Uri(target), new TransferAction(new Uri(target)).Target);
        }

        [Theory]
        [InlineData("https://example.com/staff")]
        [InlineData("mailto:staff@example.com")]
        public void ATransferRefusesAnyOtherTarget(string target)
        {
            _ = Assert.Throws<ArgumentException>(() => new TransferAction(new Uri(target)));
        }

        private sealed class RecordingChannel(ConversationActionResult answer) : IConversationChannel
        {
            public List<ConversationAction> Seen { get; } = [];

            public ConversationActionResult Request(ConversationAction action)
            {
                Seen.Add(action);
                return answer;
            }
        }
    }
}

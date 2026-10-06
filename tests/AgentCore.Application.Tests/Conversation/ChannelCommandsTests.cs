using AgentCore.Application.Conversation.Commands;
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
    public sealed class ChannelCommandsTests
    {
        private static readonly Uri Staff = new("tel:+15550002222");

        [Fact]
        public async Task AnEndCommandEndsTheConversationWithItsReason()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            Assert.Equal(ChannelCommandResult.Scheduled, session.Send(new EndCommand(ConversationEndReason.TransferredToHuman)));
            await session.FlushNoticesAsync();

            Assert.Equal(ConversationEndReason.TransferredToHuman, Assert.Single(hook.Of<ConversationEnded>()).Reason);
        }

        [Fact]
        public void AnEndingConversationTakesNoCommandAndNeverAsksTheChannel()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);
            session.Commands.Attach(channel);
            _ = session.EndConversation(ConversationEndReason.CallerHungUp);

            Assert.Equal(ChannelCommandResult.Ending, session.Send(new EndCommand(ConversationEndReason.AgentCompleted)));
            Assert.Equal(ChannelCommandResult.Ending, session.Send(new TransferCommand(Staff)));
            Assert.Empty(channel.Seen);
        }

        [Fact]
        public void ATransferWithNoChannelIsNotSupportedAndLeavesTheConversationOpen()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);

            Assert.Equal(ChannelCommandResult.NotSupported, session.Send(new TransferCommand(Staff)));
            Assert.False(session.IsComplete);
        }

        [Fact]
        public void TheAttachedChannelTakesATransfer()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);
            TransferCommand transfer = new(Staff);

            session.Commands.Attach(channel);
            Assert.Equal(ChannelCommandResult.Scheduled, session.Send(transfer));
            Assert.Same(transfer, Assert.Single(channel.Seen));
        }

        // A newer call that takes the conversation over carries its commands from then on.
        [Fact]
        public void ANewerChannelReplacesTheOlderOne()
        {
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply);
            RecordingChannel older = new(ChannelCommandResult.Scheduled);
            RecordingChannel newer = new(ChannelCommandResult.Scheduled);
            session.Commands.Attach(older);
            session.Commands.Attach(newer);

            Assert.Equal(ChannelCommandResult.Scheduled, session.Send(new TransferCommand(Staff)));
            _ = Assert.Single(newer.Seen);
            Assert.Empty(older.Seen);
        }

        [Fact]
        public void AScopeBuiltOutsideATurnCanDoNothing()
        {
            ToolCallScope scope = new("c-1", 0, string.Empty);

            Assert.Equal(ChannelCommandResult.NotSupported, scope.Channel.Send(new EndCommand(ConversationEndReason.AgentCompleted)));
        }

        [Theory]
        [InlineData("tel:+15550002222")]
        [InlineData("sip:agent@example.com")]
        [InlineData("sips:agent@example.com")]
        public void ATransferTakesTelSipAndSipsTargets(string target)
        {
            Assert.Equal(new Uri(target), new TransferCommand(new Uri(target)).Target);
        }

        [Theory]
        [InlineData("https://example.com/staff")]
        [InlineData("mailto:staff@example.com")]
        public void ATransferRefusesAnyOtherTarget(string target)
        {
            _ = Assert.Throws<ArgumentException>(() => new TransferCommand(new Uri(target)));
        }

        // GPT-Live takes at most 500 tokens in one update.
        [Fact]
        public void AVoiceFactIsCutToItsFirstThousandCharacters()
        {
            Assert.Equal(new string('a', 1000), new AddVoiceContextCommand(new string('a', 1000) + "b").Text);
        }

        private sealed class RecordingChannel(ChannelCommandResult answer) : IConversationChannel
        {
            public List<ChannelCommand> Seen { get; } = [];

            public ChannelCommandResult Send(ChannelCommand command)
            {
                Seen.Add(command);
                return answer;
            }
        }
    }
}

using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>The shown text laid back over the messages of one reply, by the rule <see cref="ShownWords.Lay"/> documents.</summary>
    public sealed class ShownWordsTests
    {
        [Fact]
        public void Lay_ShownRunsPastTheLastMessage_TheLastMessageWithTextTakesTheRest()
        {
            ChatMessage stepOne = new(ChatRole.Assistant, [new TextContent("Let me check."), new FunctionCallContent("call_1", "look_it_up")]);
            ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("call_1", "42")]);
            ChatMessage stepTwo = new(ChatRole.Assistant, "Your order");

            List<ChatMessage> laid = ShownWords.Lay([stepOne, result, stepTwo], "Let me check.Your order ships today");

            Assert.Equal(["Let me check.", string.Empty, "Your order ships today"], laid.Select(message => message.Text));
            Assert.Same(stepOne, laid[0]);
        }
    }
}

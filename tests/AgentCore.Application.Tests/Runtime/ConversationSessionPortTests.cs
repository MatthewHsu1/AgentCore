using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>D4: the inbound port.</summary>
    public sealed class ConversationSessionPortTests
    {
        [Fact]
        public async Task TheSession_SatisfiesTheInboundPort()
        {
            using SequencedChatClient reply = new("hello there.", "still here.");
            using SequencedChatClient fill = new(StayingNull);
            IConversationPort port = Assert.IsType<IConversationPort>(Build(PolicyYaml, reply, fill).Create("conversation-9"), exactMatch: false);

            TurnResult turn = await port.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The relay shim and the SSE endpoint both drive this contract and reach past it for nothing.
            Assert.Equal("conversation-9", port.ConversationId);
            Assert.Equal("greeting", port.Stage);
            Assert.False(port.IsComplete);
            Assert.Same(turn, port.LastTurn);

            // A cut that lands after the turn ended is recorded, not ignored: the port amends the finished turn.
            Assert.True(port.Cut(turn.TurnIndex, new TurnCut("nothing played", TimeSpan.Zero)));

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in port.RunTurnStreamingAsync("still there?", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            Assert.Equal("still here.", string.Concat(updates.Select(update => update.Text)));
        }
    }
}

using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A turn that withdraws a tool turn keeps its call and result only when it resends
    /// the same user message (regenerate/retry). An edit — a new message id — re-runs tools.
    /// </summary>
    public sealed class ConversationSessionCarryTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static (ConversationSession Session, RequestCapturingChatClient Model) Create(StubToolBuilder tools)
        {
            RequestCapturingChatClient model = new(new ToolCallingChatClient("final"));
            return (HookSessions.Create(HookSessions.ToolAgentYaml, model, tools: tools.Create), model);
        }

        [Fact]
        public async Task Regenerate_SameMessageId_CarriesWithdrawnToolResult_ToolRunsOnce()
        {
            StubToolBuilder tools = new("{}");
            (ConversationSession session, RequestCapturingChatClient model) = Create(tools);

            _ = await session.RunTurnAtOriginAsync("how much?", new ConversationTurnOrigin("m-1", null) { NamesParent = true }, Ct);
            Assert.Single(tools.Called);

            TurnResult again = await session.RunTurnAtOriginAsync("how much?", new ConversationTurnOrigin("m-1", null) { NamesParent = true }, Ct);

            Assert.Null(again.Failure);
            Assert.Single(tools.Called);
            Assert.Contains(model.Requests[2], HasResult);
        }

        [Fact]
        public async Task Edit_NewMessageId_ReRunsTools_ToolRunsTwice()
        {
            StubToolBuilder tools = new("{}");
            (ConversationSession session, RequestCapturingChatClient model) = Create(tools);

            _ = await session.RunTurnAtOriginAsync("how much?", new ConversationTurnOrigin("m-1", null) { NamesParent = true }, Ct);
            Assert.Single(tools.Called);

            TurnResult edited = await session.RunTurnAtOriginAsync("how much, exactly?", new ConversationTurnOrigin("m-2", null) { NamesParent = true }, Ct);

            Assert.Null(edited.Failure);
            Assert.Equal(2, tools.Called.Count);
            Assert.DoesNotContain(model.Requests[2], HasResult);
        }

        [Fact]
        public async Task EditWithNoMessageId_ReRunsTools()
        {
            StubToolBuilder tools = new("{}");
            (ConversationSession session, RequestCapturingChatClient model) = Create(tools);

            _ = await session.RunTurnAtOriginAsync("how much?", new ConversationTurnOrigin("m-1", null) { NamesParent = true }, Ct);
            Assert.Single(tools.Called);

            TurnResult edited = await session.RunTurnAtOriginAsync("how much, exactly?", new ConversationTurnOrigin(null, null) { NamesParent = true }, Ct);

            Assert.Null(edited.Failure);
            Assert.Equal(2, tools.Called.Count);
            Assert.DoesNotContain(model.Requests[2], HasResult);
        }

        private static bool HasResult(ChatMessage message)
        {
            return message.Contents.OfType<FunctionResultContent>().Any();
        }
    }
}

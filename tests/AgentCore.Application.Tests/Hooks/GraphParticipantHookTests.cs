using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>
    /// A graph row runs each participant on a session of its own, with no run options. Every turn-scoped
    /// hook point and tool binding must still find the turn that runs the graph.
    /// </summary>
    public sealed class GraphParticipantHookTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Inside a graph: the researcher makes two rounds (the tool call, then its answer), the responder one.
        [Fact]
        public async Task ABeforeModelHookSeesEveryRoundOfEveryParticipant()
        {
            List<(string? Conversation, int? Turn, int Round)> seen = [];
            BeforeGates hook = new(model: gate => seen.Add((gate.Scope.ConversationId, gate.Scope.TurnIndex, gate.Round)));
            ConversationSession session = HookSessions.Create(
                HookSessions.GraphYaml, new ToolCallingChatClient("final"), [hook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal([(session.ConversationId, 0, 0), (session.ConversationId, 0, 1), (session.ConversationId, 0, 2)], seen);
        }

        [Fact]
        public async Task AParticipantsToolCallPassesTheToolGateAndRaisesOneToolCalled()
        {
            List<(string Tool, string? Conversation, int? Turn)> gated = [];
            BeforeGates hook = new(tool: gate => gated.Add((gate.ToolName, gate.Scope.ConversationId, gate.Scope.TurnIndex)));
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.GraphYaml, new ToolCallingChatClient("final"), [hook, notices], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal([("price_lookup", session.ConversationId, 0)], gated);
            ToolCalled called = Assert.Single(notices.Of<ToolCalled>());
            Assert.Equal(("price_lookup", 0, ToolOutcome.Ok), (called.ToolName, called.Scope.TurnIndex, called.Outcome));
        }

        [Fact]
        public async Task ABoundToolInAGraphReadsTheConversationAndTurnFromItsScope()
        {
            List<(string ConversationId, int TurnIndex)?> seen = [];
            ConversationSession session = HookSessions.Create(
                HookSessions.GraphYaml,
                new ToolCallingChatClient("final"),
                tools: tool => AIFunctionFactory.Create(
                    (AIFunctionArguments arguments) =>
                    {
                        seen.Add(TurnInvocation.FiledIn(arguments) is { } turn
                            ? (ToolCallScopes.From(turn).ConversationId, ToolCallScopes.From(turn).TurnIndex)
                            : null);
                        return "ok";
                    },
                    tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal([(session.ConversationId, 0)], seen);
        }

        // A handoff row runs its participants with options of its own, which carry the handoff tool. The
        // researcher hands off only if that tool still reaches the model, and the turn rides those options too.
        [Fact]
        public async Task AHandoffParticipantKeepsItsHandoffToolAndCarriesTheTurn()
        {
            List<(string AgentId, bool Nested, int? Turn)> seen = [];
            BeforeGates hook = new(run: gate => seen.Add((gate.AgentId, gate.Nested, gate.Scope.TurnIndex)));
            RequestsSeen model = new(new ScriptedToolCallingChatClient(("handoff_to_1", "{}")) { FinalText = "done" });
            ConversationSession session = HookSessions.Create(HookSessions.HandoffGraphYaml, model, [hook]);

            _ = await session.RunTurnAsync("hi", Ct);

            Assert.Equal([("researcher", true, 0), ("responder", true, 0)], seen);
            Assert.Equal([session.ConversationId, session.ConversationId], model.Requests.Select(static request => request.Stamp));
        }

        // The compiled participants are shared by every conversation; each run must find its own conversation's turn.
        [Fact]
        public async Task TwoConversationsOnOneCompiledGraphEachSeeTheirOwnTurn()
        {
            List<(string? Conversation, string AgentId, int? Turn)> seen = [];
            BeforeGates hook = new(run: gate => seen.Add((gate.Scope.ConversationId, gate.AgentId, gate.Scope.TurnIndex)));
            CompiledAgent compiled = HookSessions.Compile(HookSessions.KeptGraphYaml, new ScriptedChatClient("done"), [hook])["main"];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
            ConversationSession first = factory.Create("conversation-a");
            ConversationSession second = factory.Create("conversation-b");

            _ = await first.RunTurnAsync("one", Ct);
            _ = await second.RunTurnAsync("one", Ct);
            _ = await first.RunTurnAsync("two", Ct);

            Assert.Equal(
                [
                    ("conversation-a", "researcher", 0), ("conversation-a", "responder", 0),
                    ("conversation-b", "researcher", 0), ("conversation-b", "responder", 0),
                    ("conversation-a", "researcher", 1), ("conversation-a", "responder", 1),
                ],
                seen);
        }

        // The prompt cache key: every participant's request names the conversation, as a single agent's does.
        [Fact]
        public async Task EveryParticipantsRequestCarriesTheConversationId()
        {
            RequestsSeen model = new(new ScriptedChatClient("done"));
            ConversationSession session = HookSessions.Create(HookSessions.GraphYaml, model, tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("hi", Ct);

            Assert.Equal([session.ConversationId, session.ConversationId], model.Requests.Select(static request => request.Stamp));
        }

        // A context note lands on the turn's runs, and on a graph row each participant's run is one.
        [Fact]
        public async Task ATurnNoteReachesEveryParticipantOnce()
        {
            RequestsSeen model = new(new ScriptedChatClient("done"));
            BeforeGates hook = new(turn: gate => gate.AddContext("per-turn note"));
            ConversationSession session = HookSessions.Create(HookSessions.GraphYaml, model, [hook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("hi", Ct);

            Assert.Equal([1, 1], model.Requests.Select(static request => request.Messages.Count(static text => text == "per-turn note")));
        }
    }
}

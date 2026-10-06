using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>A call's brief reaches the engine as instructions, verbatim.</summary>
    public sealed class CallBriefHookTests
    {
        // Leading and trailing spaces: a trim or a wrapper would change the brief, and the whole-part check sees it.
        private const string Brief = "  Earlier summary, for context only.\nAsk whether this call is about it. \"Do not\" read details out.  ";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The agent's own instructions come first and the brief follows on a new line, so a brief that arrives
        // untouched, with its edge spaces, is exactly what the instructions end with after that line break.
        private static bool EndsWithTheBrief(string instructions)
        {
            return instructions.EndsWith("\n" + Brief, StringComparison.Ordinal);
        }

        // Instructions a run gate adds reach every round of the run.
        [Fact]
        public async Task ABriefReachesEveryRoundOfItsCallsRunVerbatim()
        {
            CallBriefHook briefs = new();
            RequestsSeen model = new(new ToolCallingChatClient("final"));
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, model, [briefs], tools: new StubToolBuilder("{}").Create, conversationId: "call-a");
            briefs.Set("call-a", Brief);

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal(2, model.Requests.Count);
            Assert.All(model.Requests, request => Assert.True(EndsWithTheBrief(request.Instructions), request.Instructions));
        }

        [Fact]
        public async Task ABriefReachesEveryTurnOfTheCallNotJustTheFirst()
        {
            CallBriefHook briefs = new();
            RequestsSeen model = new(new ToolCallingChatClient("final"));
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, model, [briefs], tools: new StubToolBuilder("{}").Create, conversationId: "call-a");
            briefs.Set("call-a", Brief);

            _ = await session.RunTurnAsync("first", Ct);
            int afterFirst = model.Requests.Count;
            _ = await session.RunTurnAsync("second", Ct);

            Assert.True(model.Requests.Count > afterFirst);
            Assert.All(model.Requests.Skip(afterFirst), request => Assert.True(EndsWithTheBrief(request.Instructions), request.Instructions));
        }

        // An agent-as-tool child is a nested run of the same conversation, and it gets the brief too.
        [Fact]
        public async Task AnAgentToolChildOfTheCallsRunGetsTheBriefToo()
        {
            CallBriefHook briefs = new();
            RequestsSeen model = new(new ToolCallingChatClient("done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "help me" }));
            ConversationSession session = HookSessions.Create(HookSessions.DelegatingYaml, model, [briefs], conversationId: "call-a");
            briefs.Set("call-a", Brief);

            _ = await session.RunTurnAsync("go", Ct);

            Assert.Contains(model.Requests, request => request.Instructions.Contains("\nhelp\n", StringComparison.Ordinal));
            Assert.All(model.Requests, request => Assert.True(EndsWithTheBrief(request.Instructions), request.Instructions));
        }

        // A vendor that speaks for itself puts its own lines in the history; every run is told they were already heard.
        [Fact]
        public async Task AFrontVoiceCallGetsTheFrontVoiceNoteAfterItsBriefOnEveryRun()
        {
            CallBriefHook briefs = new();
            using ScriptedChatClient scripted = new("ok");
            RequestsSeen model = new(scripted);
            ConversationSession call = HookSessions.Create(HookSessions.OneAgentYaml, model, [briefs], conversationId: "call-a");
            briefs.Set("call-a", Brief, frontVoice: true);

            _ = await call.RunTurnAsync("first", Ct);
            briefs.Set("call-a", brief: null, frontVoice: true);
            _ = await call.RunTurnAsync("second", Ct);

            Assert.EndsWith("\n" + Brief + "\n\n" + CallBriefHook.FrontVoiceNote, model.Requests[0].Instructions, StringComparison.Ordinal);
            Assert.EndsWith("\n" + CallBriefHook.FrontVoiceNote, model.Requests[1].Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain(Brief, model.Requests[1].Instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnotherConversationAndAClearedCallSeeNoBrief()
        {
            CallBriefHook briefs = new();
            using ScriptedChatClient scripted = new("ok");
            RequestsSeen model = new(scripted);
            briefs.Set("call-a", Brief);

            ConversationSession web = HookSessions.Create(HookSessions.OneAgentYaml, model, [briefs], conversationId: "web-b");
            _ = await web.RunTurnAsync("hi", Ct);
            Assert.DoesNotContain(Brief, model.Requests[^1].Instructions, StringComparison.Ordinal);

            briefs.Clear("call-a");
            ConversationSession call = HookSessions.Create(HookSessions.OneAgentYaml, model, [briefs], conversationId: "call-a");
            _ = await call.RunTurnAsync("hi", Ct);
            Assert.DoesNotContain(Brief, model.Requests[^1].Instructions, StringComparison.Ordinal);
        }
    }
}

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools.Binding;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The turn reaches its tools: the screen the loop builds arriving at the tool that publishes.
    /// </summary>
    public sealed class TurnScreenTests
    {
        private const string DelegationYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller", tools: [ ask_specialist ] }
            - { id: specialist, model: { ref: specialist }, instructions: "answer the greeter" }
        entries:
          main:
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: greeter, terminal: true }
        """;

        [Fact]
        public async Task ATurnThatDoesNotStream_ShowsItsToolsTheScreen()
        {
            (ConversationSession? session, ScreenProbe? probe) = NewConversation();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The screen a tool finds must be the SAME recorder the turn publishes into, not merely
            // some TurnRenders or other: a regression that handed the tool a different instance would
            // still publish, and would still lose the render, since the drain reads the turn's own.
            _ = Assert.IsType<TurnRenders>(probe.Seen);
            Assert.Same(probe.TurnRenders, probe.Seen);
        }

        [Fact]
        public async Task ATurnThatStreams_ShowsItsToolsTheScreenToo()
        {
            // The hazard this pins. An async iterator restores its caller's execution context at every
            // yield, and the framework streams the tool-call update BEFORE it invokes the function, so a
            // scope opened once reaches no round at all. Only the per-round re-entry in
            // RunTurnStreamingAsync keeps the screen alive across it.
            (ConversationSession? session, ScreenProbe? probe) = NewConversation();

            await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
            }

            _ = Assert.IsType<TurnRenders>(probe.Seen);
            Assert.Same(probe.TurnRenders, probe.Seen);
        }

        [Fact]
        public async Task AConversationThatWasGivenNoScreen_ShowsItsToolsNone()
        {
            // The voice path. The tool reads the null and tells the model it cannot show anything,
            // rather than claiming a picture a telephone caller will never see.
            (ConversationSession? session, ScreenProbe? probe) = NewConversation(withScreen: false);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.True(probe.Ran);
            Assert.Null(probe.Seen);
        }

        [Fact]
        public async Task AScreenTakenBackBeforeATurn_LeavesTheToolWithNone()
        {
            // A host sets this per request, and the whole-reply branch of the chat endpoint sets none.
            // Taking it back has to reach the tool, or a session that streamed once keeps a screen the
            // next answer has nowhere to write.
            (ConversationSession? session, ScreenProbe? probe) = NewConversation();

            session.SetHasScreen(false);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.True(probe.Ran);
            Assert.Null(probe.Seen);
        }

        /// <summary>Opens a conversation whose specialist is handed one tool that reports the screen it found.</summary>
        private static (ConversationSession Session, ScreenProbe Probe) NewConversation(bool withScreen = true)
        {
            ToolCallingChatClient greeter = new(
                "hello there.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "check the order system" });
            ToolCallingChatClient specialist = new("the specialist answer");

            RoutingChatClientFactory chatClients = new(greeter);
            _ = chatClients.Route("reply", greeter);
            _ = chatClients.Route("specialist", specialist);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(DelegationYaml), new AgentCompilationContext(chatClients))["main"];

            ConversationSession session = new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null).Create();

            ScreenProbe probe = new();

            session.SetHasScreen(withScreen);
            session.SetDelegatedTools("ask_specialist", [probe.Tool]);

            return (session, probe);
        }

        /// <summary>A tool that reports the screen and the recorder it found, from wherever it ran.</summary>
        private sealed class ScreenProbe
        {
            public ScreenProbe()
            {
                Tool = AIFunctionFactory.Create(
                            (TurnInvocation? turn) =>
                            {
                                Ran = true;
                                Seen = turn?.Screen;
                                TurnRenders = turn?.Renders;
                                return "shown.";
                            },
                            new AIFunctionFactoryOptions
                            {
                                Name = "build_ui",
                                Description = "Show something on the caller's screen.",
                                ConfigureParameterBinding = ToolParameterBindings.For,
                            });
            }

            public AIFunction Tool { get; }

            public bool Ran { get; private set; }

            public IRenderPort? Seen { get; private set; }

            /// <summary>What the turn drew into at the same moment <see cref="Seen"/> was read.</summary>
            public TurnRenders? TurnRenders { get; private set; }
        }
    }
}

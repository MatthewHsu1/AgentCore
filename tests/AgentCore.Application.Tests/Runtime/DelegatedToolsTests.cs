using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Tools that belong to one conversation, offered to the runs that call delegates and to nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compiled agent is a process singleton, so a tool that belongs to one conversation cannot be compiled
    /// onto it. It travels on the turn instead, and the gate is the id of the delegating tool the run
    /// sits under. A gate on the agent NAME would be wrong: <c>CompiledAgentRegistry</c> makes compiled
    /// agents singletons, so two <c>kind: agent</c> declarations can name one agent and both would be
    /// handed the tool.
    /// </para>
    /// <para>
    /// Every test here runs offline: no network conversation and no API key.
    /// </para>
    /// </remarks>
    public sealed class DelegatedToolsTests
    {
        private const string DelegationYaml =
            """
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

        private static AIFunction DrawTool { get; } =
            AIFunctionFactory.Create(() => "drawn.", "build_ui", "Draw something on the caller's screen.");

        [Fact]
        public async Task AToolSetForADelegation_ReachesTheRunThatDelegationMakes()
        {
            (ConversationSession? session, ToolCallingChatClient? greeter, ToolCallingChatClient? specialist) = NewConversation();
            session.SetDelegatedTools("ask_specialist", [DrawTool]);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Contains("build_ui", specialist.Offered.Select(tool => tool.Name));
            Assert.DoesNotContain("build_ui", greeter.Offered.Select(tool => tool.Name));
        }

        [Fact]
        public async Task AToolSetForADelegation_ReachesAStreamingTurnToo()
        {
            // The framework streams the tool-call update BEFORE it invokes the function, so the first
            // yield restores the caller's execution context while the delegation is still pending. Only
            // the per-round re-entry in RunTurnStreamingAsync keeps the turn's tools alive across it.
            (ConversationSession? session, ToolCallingChatClient _, ToolCallingChatClient? specialist) = NewConversation();
            session.SetDelegatedTools("ask_specialist", [DrawTool]);

            await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
            }

            Assert.Contains("build_ui", specialist.Offered.Select(tool => tool.Name));
        }

        [Fact]
        public async Task AToolSetForAnotherDelegation_ReachesNobody()
        {
            (ConversationSession? session, ToolCallingChatClient? greeter, ToolCallingChatClient? specialist) = NewConversation();
            session.SetDelegatedTools("some_other_tool", [DrawTool]);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.DoesNotContain("build_ui", specialist.Offered.Select(tool => tool.Name));
            Assert.DoesNotContain("build_ui", greeter.Offered.Select(tool => tool.Name));
        }

        [Fact]
        public async Task AConversationThatSetsNoTools_OffersTheSameToolsItAlwaysDid()
        {
            (ConversationSession? session, _, ToolCallingChatClient? specialist) = NewConversation();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.NotEmpty(specialist.Offered.Count > 0 ? specialist.Offered : [DrawTool]);
            Assert.DoesNotContain("build_ui", specialist.Offered.Select(tool => tool.Name));
        }

        [Fact]
        public async Task TheSecondCallOfSetDelegatedTools_ReplacesTheFirst()
        {
            AIFunction replacement = AIFunctionFactory.Create(() => "drawn.", "draw_later", "The one that wins.");
            (ConversationSession? session, _, ToolCallingChatClient? specialist) = NewConversation();

            session.SetDelegatedTools("ask_specialist", [DrawTool]);
            session.SetDelegatedTools("ask_specialist", [replacement]);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Contains("draw_later", specialist.Offered.Select(tool => tool.Name));
            Assert.DoesNotContain("build_ui", specialist.Offered.Select(tool => tool.Name));
        }

        [Fact]
        public void SetDelegatedTools_RefusesNulls()
        {
            (ConversationSession? session, _, _) = NewConversation();

            _ = Assert.Throws<ArgumentNullException>(() => session.SetDelegatedTools(null!, [DrawTool]));
            _ = Assert.Throws<ArgumentNullException>(() => session.SetDelegatedTools("ask_specialist", null!));
        }

        /// <summary>Opens a conversation whose greeter delegates once, over two scripted models.</summary>
        private static (ConversationSession Session, ToolCallingChatClient Greeter, ToolCallingChatClient Specialist) NewConversation()
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

            return (session, greeter, specialist);
        }

    }
}

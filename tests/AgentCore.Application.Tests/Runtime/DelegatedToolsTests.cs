using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Tools that belong to one call, offered to the runs that call delegates and to nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The compiled agent is a process singleton, so a tool that belongs to one call cannot be compiled
/// onto it. It travels on the turn instead, and the gate is the id of the delegating tool the run
/// sits under. A gate on the agent NAME would be wrong: <c>CompiledAgentRegistry</c> makes compiled
/// agents singletons, so two <c>kind: agent</c> declarations can name one agent and both would be
/// handed the tool.
/// </para>
/// <para>
/// Every test here runs offline: no network call and no API key.
/// </para>
/// </remarks>
public sealed class DelegatedToolsTests
{
    private const string DelegationYaml =
        """
        apiVersion: agentcore/v1
        name: delegated-tools
        tools:
          - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller", tools: [ ask_specialist ] }
            - { id: specialist, model: { ref: specialist }, instructions: "answer the greeter" }
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
        var (session, greeter, specialist) = NewCall();
        session.SetDelegatedTools("ask_specialist", [DrawTool]);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Contains("build_ui", specialist.Offered.Select(tool => tool.Name));
        Assert.DoesNotContain("build_ui", greeter.Offered.Select(tool => tool.Name));
    }

    [Fact]
    public async Task AToolSetForADelegation_ReachesAStreamingTurnToo()
    {
        // The framework streams the tool-call update BEFORE it invokes the function, so the first
        // yield restores the caller's execution context while the delegation is still pending. Only
        // the per-round re-entry in RunTurnStreamingAsync keeps the turn's tools alive across it.
        var (session, _, specialist) = NewCall();
        session.SetDelegatedTools("ask_specialist", [DrawTool]);

        await foreach (var _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
        {
        }

        Assert.Contains("build_ui", specialist.Offered.Select(tool => tool.Name));
    }

    [Fact]
    public async Task AToolSetForAnotherDelegation_ReachesNobody()
    {
        var (session, greeter, specialist) = NewCall();
        session.SetDelegatedTools("some_other_tool", [DrawTool]);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("build_ui", specialist.Offered.Select(tool => tool.Name));
        Assert.DoesNotContain("build_ui", greeter.Offered.Select(tool => tool.Name));
    }

    [Fact]
    public async Task ACallThatSetsNoTools_OffersTheSameToolsItAlwaysDid()
    {
        var (session, _, specialist) = NewCall();

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.NotEmpty(specialist.Offered.Count > 0 ? specialist.Offered : [DrawTool]);
        Assert.DoesNotContain("build_ui", specialist.Offered.Select(tool => tool.Name));
    }

    [Fact]
    public async Task TheSecondCallOfSetDelegatedTools_ReplacesTheFirst()
    {
        var replacement = AIFunctionFactory.Create(() => "drawn.", "draw_later", "The one that wins.");
        var (session, _, specialist) = NewCall();

        session.SetDelegatedTools("ask_specialist", [DrawTool]);
        session.SetDelegatedTools("ask_specialist", [replacement]);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Contains("draw_later", specialist.Offered.Select(tool => tool.Name));
        Assert.DoesNotContain("build_ui", specialist.Offered.Select(tool => tool.Name));
    }

    [Fact]
    public void ARunUnderNoDelegation_IsOfferedNothing()
    {
        // This is the outer agent's own run, where FunctionInvokingChatClient.CurrentContext is null.
        // The id gate shuts it out for nothing, so no session check is needed to keep the tool in.
        using var scope = TurnContextScope.Enter(new TurnContext
        {
            Session = new StubSession(),
            Tools = [DrawTool],
            ToolsFor = "ask_specialist",
        });

        Assert.Null(TurnContextScope.ToolsFor(null));
        Assert.Null(TurnContextScope.ToolsFor("something_else"));
        Assert.Equal([DrawTool], TurnContextScope.ToolsFor("ask_specialist"));
    }

    [Fact]
    public void SetDelegatedTools_RefusesNulls()
    {
        var (session, _, _) = NewCall();

        Assert.Throws<ArgumentNullException>(() => session.SetDelegatedTools(null!, [DrawTool]));
        Assert.Throws<ArgumentNullException>(() => session.SetDelegatedTools("ask_specialist", null!));
    }

    /// <summary>Opens a call whose greeter delegates once, over two scripted models.</summary>
    private static (CallSession Session, ToolCallingChatClient Greeter, ToolCallingChatClient Specialist) NewCall()
    {
        ToolCallingChatClient greeter = new(
            "hello there.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "check the order system" });
        ToolCallingChatClient specialist = new("the specialist answer");

        RoutingChatClientFactory chatClients = new(greeter);
        chatClients.Route("reply", greeter);
        chatClients.Route("specialist", specialist);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(DelegationYaml), new AgentCompilationContext(chatClients));

        var session = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null).Create();

        return (session, greeter, specialist);
    }

    private sealed class StubSession : Microsoft.Agents.AI.AgentSession;
}

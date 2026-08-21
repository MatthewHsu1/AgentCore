using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The screen of one call, found by a tool that was compiled once for the whole process.
/// </summary>
/// <remarks>
/// <para>
/// A tool that draws cannot hold the screen it draws on: the compiled agent is a process singleton
/// and the screen belongs to one call. It reads the ambient scope instead, exactly as a guarded
/// graph edge reads <see cref="CallStateScope"/>.
/// </para>
/// <para>
/// Every test here runs offline: no network call and no API key.
/// </para>
/// </remarks>
public sealed class CallRenderScopeTests
{
    private const string DelegationYaml =
        """
        apiVersion: agentcore/v1
        name: render-scope
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

    [Fact]
    public void WithNoScopeOpen_TheScreenIsNullRatherThanAThrow()
    {
        // Unlike CallStateScope, which throws. A guarded edge that quietly became unconditional is a
        // silent failure; a call that genuinely has no screen — the telephone — is not.
        Assert.Null(CallRenderScope.Current);
    }

    [Fact]
    public void ClosingAScope_PutsBackTheOneThatWasOpenBefore()
    {
        RecordingRenderPort outer = new();
        RecordingRenderPort inner = new();

        using (CallRenderScope.Enter(outer))
        {
            Assert.Same(outer, CallRenderScope.Current);

            using (CallRenderScope.Enter(inner))
            {
                Assert.Same(inner, CallRenderScope.Current);
            }

            Assert.Same(outer, CallRenderScope.Current);
        }

        Assert.Null(CallRenderScope.Current);
    }

    [Fact]
    public void DisposingTwice_DoesNotPutAnOlderScreenOverANewerScope()
    {
        RecordingRenderPort first = new();
        RecordingRenderPort second = new();

        var scope = CallRenderScope.Enter(first);
        scope.Dispose();

        using var later = CallRenderScope.Enter(second);
        scope.Dispose();

        Assert.Same(second, CallRenderScope.Current);
    }

    [Fact]
    public async Task ATurnThatDoesNotStream_ShowsItsToolsTheScreen()
    {
        var (session, screen, probe) = NewCall();

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Same(screen, probe.Seen);
        Assert.Single(screen.Published);
    }

    [Fact]
    public async Task ATurnThatStreams_ShowsItsToolsTheScreenToo()
    {
        // The hazard this pins. An async iterator restores its caller's execution context at every
        // yield, and the framework streams the tool-call update BEFORE it invokes the function, so a
        // scope opened once reaches no round at all. Only the per-round re-entry in
        // RunTurnStreamingAsync keeps the screen alive across it.
        var (session, screen, probe) = NewCall();

        await foreach (var _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
        {
        }

        Assert.Same(screen, probe.Seen);
        Assert.Single(screen.Published);
    }

    [Fact]
    public async Task ACallThatWasGivenNoScreen_ShowsItsToolsNone()
    {
        // The voice path. The tool reads the null and tells the model it cannot draw, rather than
        // claiming a picture a telephone caller will never see.
        var (session, _, probe) = NewCall(withScreen: false);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(probe.Ran);
        Assert.Null(probe.Seen);
    }

    [Fact]
    public async Task AScreenTakenBackBeforeATurn_LeavesTheToolWithNone()
    {
        // A host sets this per request, and the whole-reply branch of the chat endpoint sets none.
        // Taking it back has to reach the tool, or a session that streamed once keeps a screen the
        // next answer has nowhere to write.
        var (session, screen, probe) = NewCall();

        session.SetRenderPort(null);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(probe.Ran);
        Assert.Null(probe.Seen);
        Assert.Empty(screen.Published);
    }

    /// <summary>Opens a call whose specialist is handed one tool that reports the screen it found.</summary>
    private static (CallSession Session, RecordingRenderPort Screen, ScreenProbe Probe) NewCall(
        bool withScreen = true)
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

        ScreenProbe probe = new();
        RecordingRenderPort screen = new();

        if (withScreen)
        {
            session.SetRenderPort(screen);
        }

        session.SetDelegatedTools("ask_specialist", [probe.Tool]);

        return (session, screen, probe);
    }

    /// <summary>A tool that reports the screen it found when it ran, from wherever it ran.</summary>
    private sealed class ScreenProbe
    {
        public ScreenProbe()
            => Tool = AIFunctionFactory.Create(
                () =>
                {
                    Ran = true;
                    Seen = CallRenderScope.Current;
                    Seen?.Publish("generative-ui", new { drawn = true });
                    return "drawn.";
                },
                "build_ui",
                "Draw something on the caller's screen.");

        public AIFunction Tool { get; }

        public bool Ran { get; private set; }

        public IRenderPort? Seen { get; private set; }
    }

    private sealed class RecordingRenderPort : IRenderPort
    {
        public List<(string Name, object Data)> Published { get; } = [];

        public void Publish(string name, object data) => Published.Add((name, data));
    }
}

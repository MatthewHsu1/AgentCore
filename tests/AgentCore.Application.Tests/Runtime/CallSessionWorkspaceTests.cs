using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The call workspace as a host sees it: bound (or not) through <see cref="CallSessionFactory"/>,
/// deleted when the call ends, and visible to a bound tool through <see cref="ToolCallScope"/>.
/// </summary>
/// <remarks>
/// Every test here runs offline. There is no network call and no API key anywhere in this file.
/// </remarks>
public sealed class CallSessionWorkspaceTests : IDisposable
{
    private const string SimpleYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

    private const string TerminalYaml =
        """
          apiVersion: agentcore/v1
          state:
            callerSaidGoodbye:
              type: boolean
              default: false
              writer: extractor
              description: whether the caller said goodbye
          guards:
            saidGoodbye: { var: callerSaidGoodbye }
          extractor:
            model: { ref: fill }
            when: after_reply
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller" }
              - { id: closer,  instructions: "close the call" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - id: greeting
                    agent: greeter
                    to: [ { stage: close, when: saidGoodbye } ]
                  - id: close
                    agent: closer
                    terminal: true
          """;

      private const string ScopeYaml =
          """
        apiVersion: agentcore/v1
        tools:
          - { id: request_human, kind: binding, binds: RequestHuman, description: "Ask a human to take the call." }
        agents:
          items:
            - { id: only, instructions: "help the caller", tools: [ request_human ] }
        entries:
          main:
            agent: only
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ARootBound_CreatesTheCallsFolder()
    {
        using SequencedChatClient reply = new("hi there.");
        var factory = Build(SimpleYaml, reply, fill: null, _root);

        var session = factory.Create("call-1");

        Assert.Equal(Path.Combine(_root, "call-1"), session.Workspace);
        Assert.True(Directory.Exists(session.Workspace));
    }

    [Fact]
    public void NoRootBound_LeavesWorkspaceNull_AndCreatesNothing()
    {
        using SequencedChatClient reply = new("hi there.");
        var factory = Build(SimpleYaml, reply, fill: null, workspaceRoot: null);

        var session = factory.Create("call-1");

        Assert.Null(session.Workspace);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void EndCall_DeletesTheFolder_AndIsSafeToCallTwice()
    {
        using SequencedChatClient reply = new("hi there.");
        var factory = Build(SimpleYaml, reply, fill: null, _root);
        var session = factory.Create("call-1");
        var path = session.Workspace!;

        session.EndCall(CallEndReason.CallerHungUp);

        Assert.False(Directory.Exists(path));
        Assert.False(session.EndCall(CallEndReason.CallerHungUp));
    }

    [Fact]
    public async Task ATurnThatReachesATerminalStage_AlsoDeletesTheFolder()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new("""{ "callerSaidGoodbye": true }""");
        var factory = Build(TerminalYaml, reply, fill, _root);
        var session = factory.Create("call-1");
        var path = session.Workspace!;

        var turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

        Assert.True(turn.IsTerminal);
        Assert.True(session.IsComplete);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ABoundTool_SeesTheRunningCallsWorkspace()
    {
        List<ToolCallScope> captured = [];
        ToolBindingRegistry bindings = new();
        bindings.Register("RequestHuman", (string reason, ToolCallScope scope) => captured.Add(scope));

        var document = ConfigurationLoader.LoadYaml(ScopeYaml);
        var chatClients = new RoutingChatClientFactory(
            new ToolCallingChatClient(
                "connecting you now.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = "the caller wants a person" }));
        var compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = await ToolRegistryBuilder.BuildAsync(
                    [new BindingToolSource(bindings)],
                    new ToolSourceContext(document),
                    TestContext.Current.CancellationToken),
            })["main"];

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            workspaceRoot: _root);
        var session = factory.Create("call-1");

        await session.RunTurnAsync("I need a person", TestContext.Current.CancellationToken);

        var scope = Assert.Single(captured);
        Assert.Equal(session.Workspace, scope.Workspace);
        Assert.NotNull(scope.Workspace);
    }

    private static CallSessionFactory Build(
        string yaml,
        IChatClient reply,
        IChatClient? fill,
        string? workspaceRoot)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new RoutingChatClientFactory(reply);
        if (fill is not null)
        {
            chatClients.Route("fill", fill);
        }

        var compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = TestToolRegistry.From(document, builder: null, TestContext.Current.CancellationToken),
            })["main"];

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            workspaceRoot: workspaceRoot);
    }
}

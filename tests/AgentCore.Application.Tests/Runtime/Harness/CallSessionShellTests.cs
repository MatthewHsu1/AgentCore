using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// The end-to-end path of a <c>shell:</c> block: the tool the model is handed, the workspace it
/// runs in, the policy that refuses a command, and the process it starts being cleaned up when the
/// call closes or a turn reaches a terminal stage.
/// </summary>
public sealed class CallSessionShellTests : IDisposable
{
    private const string ShellYaml =
        """
        apiVersion: agentcore/v1
        name: harness-shell-session
        agents:
          items:
            - { id: only, instructions: "run commands", shell: { kind: local, policy: { deny: ["^rm "] } } }
        """;

    private const string NoShellYaml =
        """
        apiVersion: agentcore/v1
        name: harness-no-shell-session
        agents:
          items:
            - { id: only, instructions: "just talk" }
        """;

    private const string TerminalShellYaml =
        """
        apiVersion: agentcore/v1
        name: harness-shell-terminal
        agents:
          items:
            - { id: only, instructions: "run one command then stop", shell: { kind: local } }
        policy:
          initial: only
          stages:
            - { id: only, agent: only, terminal: true }
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "agentcore-shell-session-" + Guid.NewGuid().ToString("N"));

    /// <summary>The name the model sees for the shell tool, read off a throwaway real executor.</summary>
    private static readonly string RunShellToolName = ToolName();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FirstRequest_ListsATool_NamedRunShell()
    {
        // One literal assertion, as the brief allows: it pins the name the model actually sees.
        Assert.Equal("run_shell", RunShellToolName);

        var client = new ShellScriptedClient(RunShellToolName, "pwd", "rm -rf x", "echo $$");
        var factory = BuildFactory(ShellYaml, client);
        await using var session = factory.Create("call-1");

        await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

        var tools = client.Requests[0].Options?.Tools ?? [];
        Assert.Contains(tools, tool => tool.Name == RunShellToolName);
    }

    [Fact]
    public async Task PwdResult_ContainsTheCallsWorkspace()
    {
        // ShellYaml's policy declares deny: only, no allow:. If AgentHarnessProviders.BuildPolicy
        // ever stopped translating the empty allow: list to null, ShellPolicy would treat it as
        // deny-all and this undenied pwd would come back rejected instead of succeeding.
        var client = new ShellScriptedClient(RunShellToolName, "pwd", "rm -rf x", "echo $$");
        var factory = BuildFactory(ShellYaml, client);
        await using var session = factory.Create("call-1");

        await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

        Assert.Contains(session.Workspace!, client.ToolResults[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RmResult_ComesBackAsRejectedText_NotAFailedTurn()
    {
        var client = new ShellScriptedClient(RunShellToolName, "pwd", "rm -rf x", "echo $$");
        var factory = BuildFactory(ShellYaml, client);
        await using var session = factory.Create("call-1");

        var turn = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

        Assert.Contains("rejected", client.ToolResults[1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("done", turn.ReplyText);
    }

    [Fact]
    public async Task AfterTheCallIsClosed_TheEchoPidIsGoneFromProc()
    {
        var client = new ShellScriptedClient(RunShellToolName, "pwd", "rm -rf x", "echo $$");
        var factory = BuildFactory(ShellYaml, client);
        InMemoryCallSessions sessions = new(factory, TimeSpan.FromMinutes(30), TimeProvider.System);

        var session = await sessions.OpenAsync("call-1", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

        var pid = ParsePid(client.ToolResults[2]);
        Assert.True(Directory.Exists($"/proc/{pid}"), "the bash should be alive before the call closes");

        await sessions.CloseAsync("call-1", TestContext.Current.CancellationToken);

        await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CloseAsync_WhenTheStoreThrowsOnFlush_StillDisposesTheShell()
    {
        // AgentCoreChatHistoryProvider.WriteAfterAsync catches every exception a store write
        // raises and reports it through TranscriptWriteDropped, whose own contract is that it
        // never throws back out — "the call outlives a store that refuses" — so FlushTranscriptAsync
        // itself surfaces nothing here, which is today's behaviour: CloseAsync completes rather than
        // throw. What this proves is the part that used to be at risk: even though the flush ate the
        // fault silently, the shell this call started is still disposed once CloseAsync returns.
        var store = new ThrowingCallStore();
        var client = new ShellScriptedClient(RunShellToolName, "echo $$");
        var document = ConfigurationLoader.LoadYaml(ShellYaml);
        var chatClients = new RoutingChatClientFactory(client);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { WorkspaceRoot = _root, CallStore = store });
        var factory = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
        InMemoryCallSessions sessions = new(factory, TimeSpan.FromMinutes(30), TimeProvider.System);

        var session = await sessions.OpenAsync("call-1", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("go", TestContext.Current.CancellationToken);
        var pid = ParsePid(client.ToolResults[0]);

        await sessions.CloseAsync("call-1", TestContext.Current.CancellationToken);

        await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASecondCallFromTheSameFactory_GetsADifferentPid()
    {
        var client = new ToolCallingChatClient(
            "done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = "echo $$" });
        var factory = BuildFactory(ShellYaml, client);

        await using var sessionA = factory.Create("call-a");
        await sessionA.RunTurnAsync("go", TestContext.Current.CancellationToken);

        await using var sessionB = factory.Create("call-b");
        await sessionB.RunTurnAsync("go", TestContext.Current.CancellationToken);

        var pidA = ParsePid(client.ToolResults[0]);
        var pidB = ParsePid(client.ToolResults[1]);

        Assert.NotEqual(pidA, pidB);
    }

    [Fact]
    public async Task NoShellBlock_NoRunShellToolInTheRequest()
    {
        using SequencedChatClient reply = new("hello there.");
        var factory = BuildFactory(NoShellYaml, reply);
        await using var session = factory.Create("call-1");

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.DoesNotContain(RunShellToolName, toolNames);
    }

    [Fact]
    public async Task ATurnThatReachesATerminalStage_DisposesTheShellWithoutCloseAsync()
    {
        var client = new ShellScriptedClient(RunShellToolName, "echo $$");
        var factory = BuildFactory(TerminalShellYaml, client);
        var session = factory.Create("call-1");

        var turn = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);
        Assert.True(turn.IsTerminal);

        var pid = ParsePid(client.ToolResults[0]);

        await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the pid off the first line of a <c>run_shell</c> tool result: MAF's own formatting
    /// appends an <c>exit_code:</c> line after the command's stdout.
    /// </summary>
    private static int ParsePid(string toolResult) =>
        int.Parse(toolResult.Split('\n', StringSplitOptions.TrimEntries)[0]);

    private static string ToolName()
    {
        var workspace = Directory.CreateTempSubdirectory("shell-tool-name-").FullName;
        try
        {
            return new LocalShellExecutor(new LocalShellExecutorOptions
            {
                WorkingDirectory = workspace,
                AcknowledgeUnsafe = true,
            }).AsAIFunction(requireApproval: false).Name;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private CallSessionFactory BuildFactory(string yaml, IChatClient client)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new RoutingChatClientFactory(client);

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { WorkspaceRoot = _root });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            workspaceRoot: _root);
    }

}

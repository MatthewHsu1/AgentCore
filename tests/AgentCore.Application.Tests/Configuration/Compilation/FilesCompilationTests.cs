using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The <c>files:</c> block reaching a compiled agent as a <see cref="FileAccessProvider"/>, the
/// <c>file_access_*</c> tools it puts in front of the model, and the call folder it scopes to.
/// </summary>
#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.
public sealed class FilesCompilationTests : IDisposable
{
    private static readonly string[] ExpectedFilesTools =
    [
        "file_access_read",
        "file_access_read_lines",
        "file_access_ls",
        "file_access_grep",
        "file_access_write",
        "file_access_delete",
        "file_access_replace",
        "file_access_replace_lines",
    ];

    private static readonly string[] ExpectedReadOnlyFilesTools =
    [
        "file_access_read",
        "file_access_read_lines",
        "file_access_ls",
        "file_access_grep",
    ];

    private const string FilesYaml =
        """
        apiVersion: agentcore/v1
        name: harness-files
        agents:
          items:
            - { id: only, instructions: "work with files", files: { store: workspace } }
        """;

    private const string ReadOnlyFilesYaml =
        """
        apiVersion: agentcore/v1
        name: harness-files-readonly
        agents:
          items:
            - { id: only, instructions: "read files", files: { store: workspace, write: false } }
        """;

    private const string NoFilesYaml =
        """
        apiVersion: agentcore/v1
        name: harness-no-files
        agents:
          items:
            - { id: only, instructions: "no files here" }
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "agentcore-files-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Compile_FilesWorkspaceWithARoot_GetsAFileAccessProviderAndTheEightTools()
    {
        using SequencedChatClient reply = new("hello there.");
        var agent = CompileOne(FilesYaml, reply, _root);
        Assert.Contains(Providers(agent), provider => provider is FileAccessProvider);

        var factory = BuildFactory(FilesYaml, _root, reply);
        var session = factory.Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var tools = reply.Options[^1]?.Tools?.OfType<AITool>().ToArray() ?? [];
        AssertSameNames(ExpectedFilesTools, tools.Select(tool => tool.Name).ToArray());
        Assert.True(
            tools.All(tool => tool is not ApprovalRequiredAIFunction),
            "expected no file_access_* tool to require approval");
    }

    [Fact]
    public async Task Compile_FilesWorkspaceWithARoot_TheModelsInstructionsAreTruthfulAboutDeletion()
    {
        using SequencedChatClient reply = new("hello there.");
        var factory = BuildFactory(FilesYaml, _root, reply);
        var session = factory.Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var instructions = reply.Options[^1]?.Instructions ?? string.Empty;
        Assert.Contains("deleted when the call ends", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("persist beyond", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compile_FilesWorkspaceWriteFalse_GetsOnlyTheFourReadOnlyTools()
    {
        using SequencedChatClient reply = new("hello there.");
        var factory = BuildFactory(ReadOnlyFilesYaml, _root, reply);
        var session = factory.Create("call-1");
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        AssertSameNames(ExpectedReadOnlyFilesTools, toolNames);
    }

    [Fact]
    public async Task Compile_NoFilesBlock_GetsNoFileAccessProviderAndNoTool()
    {
        using SequencedChatClient reply = new("hello there.");

        var agent = CompileOne(NoFilesYaml, reply, _root);
        Assert.DoesNotContain(Providers(agent), provider => provider is FileAccessProvider);

        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);
        await agent.RunAsync("hi", session, cancellationToken: token);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.DoesNotContain(toolNames, name => name.StartsWith("file_access_", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_FilesWorkspaceWithNoRoot_FailsNamingTheFilesPointer()
    {
        var document = ConfigurationLoader.LoadYaml(FilesYaml);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi")))));

        Assert.Equal("/agents/items/0/files", failure.Pointer);
        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_WriteReadLsGrep_ThenEndCallDeletesTheFolder()
    {
        var client = new SequencedToolCallClient(
            (FileAccessProvider.WriteToolName, Args(("fileName", "out.txt"), ("content", "hello"))),
            (FileAccessProvider.ReadFileToolName, Args(("fileName", "out.txt"))),
            (FileAccessProvider.LsToolName, Args()),
            (FileAccessProvider.GrepToolName, Args(("regexPattern", "hell"))));

        var factory = BuildFactory(FilesYaml, _root, client);
        var session = factory.Create("call-1");
        var callFolder = session.Workspace!;

        await session.RunTurnAsync("write hello to out.txt", TestContext.Current.CancellationToken);
        Assert.Equal("hello", await File.ReadAllTextAsync(
            Path.Combine(callFolder, "out.txt"), TestContext.Current.CancellationToken));

        await session.RunTurnAsync("read out.txt", TestContext.Current.CancellationToken);
        Assert.Contains("hello", client.ToolResults[1]);

        await session.RunTurnAsync("list the folder", TestContext.Current.CancellationToken);
        Assert.Contains("out.txt", client.ToolResults[2]);
        Assert.DoesNotContain("call-1", client.ToolResults[2], StringComparison.Ordinal);

        await session.RunTurnAsync("grep for hell", TestContext.Current.CancellationToken);
        Assert.Contains("out.txt", client.ToolResults[3]);

        session.EndCall(CallEndReason.CallerHungUp);
        Assert.False(Directory.Exists(callFolder));
    }

    [Fact]
    public async Task Escape_WriteOutsideTheCallFolder_CreatesNothingOutsideItAndDoesNotThrow()
    {
        var client = new SequencedToolCallClient(
            (FileAccessProvider.WriteToolName, Args(("fileName", "../escape.txt"), ("content", "leaked"))));

        var factory = BuildFactory(FilesYaml, _root, client);
        var session = factory.Create("call-1");

        // The point of the assertion is that this completes at all: an escape attempt must not
        // throw out of RunTurnAsync.
        await session.RunTurnAsync("escape", TestContext.Current.CancellationToken);

        // The real string, not the probe's "Error: Function failed." (that string is what a store
        // exception looks like when it escapes uncaught, e.g. the missing-ambient case; a rejected
        // path is instead refused before it throws, as a JSON error object naming the tool.)
        Assert.Contains("\"error\": true", client.ToolResults[0], StringComparison.Ordinal);
        Assert.Contains("file_access_write", client.ToolResults[0], StringComparison.Ordinal);

        var escaped = Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "escape.txt", SearchOption.AllDirectories).ToList()
            : [];
        Assert.Empty(escaped);
    }

    [Fact]
    public async Task TwoSessions_DifferentCallIds_SeeOnlyTheirOwnFile()
    {
        var clientA = new SequencedToolCallClient(
            (FileAccessProvider.WriteToolName, Args(("fileName", "out.txt"), ("content", "A"))));
        var clientB = new SequencedToolCallClient(
            (FileAccessProvider.WriteToolName, Args(("fileName", "out.txt"), ("content", "B"))));

        var factoryA = BuildFactory(FilesYaml, _root, clientA);
        var sessionA = factoryA.Create("call-a");
        await sessionA.RunTurnAsync("write A", TestContext.Current.CancellationToken);

        var factoryB = BuildFactory(FilesYaml, _root, clientB);
        var sessionB = factoryB.Create("call-b");
        await sessionB.RunTurnAsync("write B", TestContext.Current.CancellationToken);

        var pathA = Path.Combine(sessionA.Workspace!, "out.txt");
        var pathB = Path.Combine(sessionB.Workspace!, "out.txt");

        Assert.Equal("A", await File.ReadAllTextAsync(pathA, TestContext.Current.CancellationToken));
        Assert.Equal("B", await File.ReadAllTextAsync(pathB, TestContext.Current.CancellationToken));
    }

    private static void AssertSameNames(string[] expected, string[] actual)
    {
        var expectedSorted = expected.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var actualSorted = actual.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedSorted, actualSorted, StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            args[key] = value;
        }

        return args;
    }

    private static CallSessionFactory BuildFactory(string yaml, string root, IChatClient? client = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new RoutingChatClientFactory(client ?? new SequencedChatClient("done"));

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { WorkspaceRoot = root });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            workspaceRoot: root);
    }

    private static AIAgent CompileOne(string yaml, SequencedChatClient reply, string root)
    {
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { WorkspaceRoot = root });

        return Assert.Single(compiled.Agents.Values);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }

    /// <summary>
    /// A deterministic offline model that plays a queue of specific named tool calls, one per turn:
    /// it calls the next queued tool when the last message is a fresh user turn, and replies with
    /// text once the framework has fed the tool's result back mid-turn.
    /// </summary>
    private sealed class SequencedToolCallClient : IChatClient
    {
        private readonly Queue<(string Name, Dictionary<string, object?> Args)> _steps;

        public SequencedToolCallClient(params (string Name, Dictionary<string, object?> Args)[] steps)
            => _steps = new(steps);

        /// <summary>Gets the text of each tool result this client was fed back, in call order.</summary>
        public List<string> ToolResults { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var transcript = messages.ToList();
            var responseId = Guid.NewGuid().ToString("N");

            var lastResults = transcript.Count > 0
                ? transcript[^1].Contents.OfType<FunctionResultContent>().ToList()
                : [];

            foreach (var result in lastResults)
            {
                lock (ToolResults)
                {
                    ToolResults.Add(result.Result?.ToString() ?? string.Empty);
                }
            }

            if (lastResults.Count == 0 && _steps.Count > 0)
            {
                var (name, args) = _steps.Dequeue();

                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call_" + Guid.NewGuid().ToString("N"), name, args)])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
#pragma warning restore MAAI001

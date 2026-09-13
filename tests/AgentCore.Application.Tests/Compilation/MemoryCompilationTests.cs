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

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// The <c>memory:</c> block reaching a compiled agent as a <see cref="FileMemoryProvider"/>, the
/// seven <c>file_memory_*</c> tools it puts in front of the model, and the working folder it binds
/// to the running call.
/// </summary>
public sealed class MemoryCompilationTests : IDisposable
{
    private static readonly string[] ExpectedMemoryTools =
    [
        "file_memory_write",
        "file_memory_read",
        "file_memory_delete",
        "file_memory_ls",
        "file_memory_grep",
        "file_memory_replace",
        "file_memory_replace_lines",
    ];

    private const string MemoryYaml =
        """
        apiVersion: agentcore/v1
        name: harness-memory
        agents:
          items:
            - { id: only, instructions: "remember things", memory: { store: workspace } }
        """;

    private const string NoMemoryYaml =
        """
        apiVersion: agentcore/v1
        name: harness-no-memory
        agents:
          items:
            - { id: only, instructions: "remember nothing" }
        """;

    private const string MemoryPolicyYaml =
        """
        apiVersion: agentcore/v1
        name: harness-memory-policy
        state:
          done:
            type: boolean
            default: false
            writer: const
            value: false
        guards:
          isDone: { var: done }
        agents:
          items:
            - { id: only, instructions: "remember things", memory: { store: workspace } }
        policy:
          initial: working
          stages:
            - id: working
              agent: only
              to: [ { stage: finished, when: isDone } ]
            - id: finished
              agent: only
              terminal: true
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "agentcore-memory-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Compile_MemoryWorkspaceWithARoot_GetsAFileMemoryProviderAndTheSevenTools()
    {
        using SequencedChatClient reply = new("hello there.");
        var document = ConfigurationLoader.LoadYaml(MemoryYaml);
        var chatClients = new RoutingChatClientFactory(reply);

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { WorkspaceRoot = _root });
        var agent = Assert.Single(compiled.Agents.Values);
        Assert.Contains(Providers(agent), provider => provider is FileMemoryProvider);

        var factory = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
        var session = factory.Create("call-1");

        // A turn must be open for the FileMemoryProvider's state initializer to read the call id off
        // TurnAmbients, so the tool list is read through a real call rather than agent.RunAsync
        // directly, unlike the todos/mode providers in HarnessCompilationTests.
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.Equal(ExpectedMemoryTools, toolNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Compile_NoMemoryBlock_GetsNoFileMemoryProviderAndNoTool()
    {
        using SequencedChatClient reply = new("hello there.");

        var agent = CompileOne(NoMemoryYaml, reply, _root);
        Assert.DoesNotContain(Providers(agent), provider => provider is FileMemoryProvider);

        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);
        await agent.RunAsync("hi", session, cancellationToken: token);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.DoesNotContain(toolNames, name => name.StartsWith("file_memory_", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_MemoryWorkspaceWithNoRoot_FailsNamingTheMemoryPointer()
    {
        using SequencedChatClient reply = new("hello there.");
        var document = ConfigurationLoader.LoadYaml(MemoryYaml);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(reply))));

        Assert.Equal("/agents/items/0/memory", failure.Pointer);
        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_TheModelWritesAFile_ItLandsUnderTheCallsFolder_AndIsGoneWhenTheCallEnds()
    {
        var factory = BuildFactory(MemoryYaml, _root);
        var session = factory.Create("call-1");
        var callFolder = session.Workspace!;

        await session.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

        var notes = FindRecursive(callFolder, "notes.md");
        Assert.NotEmpty(notes);
        Assert.Contains("hello", await File.ReadAllTextAsync(notes[0], TestContext.Current.CancellationToken));

        session.EndCall(CallEndReason.CallerHungUp);

        Assert.False(Directory.Exists(callFolder));
    }

    [Fact]
    public async Task TwoSessions_DifferentCallIds_WriteToDifferentFolders()
    {
        var factory = BuildFactory(MemoryYaml, _root);

        var first = factory.Create("call-a");
        await first.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

        var second = factory.Create("call-b");
        await second.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

        var firstNotes = FindRecursive(first.Workspace!, "notes.md");
        var secondNotes = FindRecursive(second.Workspace!, "notes.md");

        Assert.NotEmpty(firstNotes);
        Assert.NotEmpty(secondNotes);
        Assert.DoesNotContain(firstNotes[0], secondNotes);
    }

    [Fact]
    public async Task MidTurnEndCall_DuringAFileMemoryWrite_StillDeletesTheFolder_EvenWhenThePolicyIsNotTerminal()
    {
        var document = ConfigurationLoader.LoadYaml(MemoryPolicyYaml);
        EndCallDuringToolChatClient chatClient = new(
            "hello there.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fileName"] = "notes.md",
                ["content"] = "hello",
            });
        var chatClients = new RoutingChatClientFactory(chatClient);

        var compiled = ConfigurationCompiler.Compile(
            document, new AgentCompilationContext(chatClients) { WorkspaceRoot = _root });
        var factory = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), workspaceRoot: _root);
        var session = factory.Create("call-1");
        var callFolder = session.Workspace!;

        // The callback fires from inside the model's response, before the framework runs the tool
        // it just asked for — mid-turn, exactly where a mid-turn EndCall would land in production.
        chatClient.BeforeToolCall = () => session.EndCall(CallEndReason.CallerHungUp);

        var turn = await session.RunTurnAsync("please remember this", TestContext.Current.CancellationToken);

        Assert.False(turn.IsTerminal);
        Assert.False(Directory.Exists(callFolder));
    }

    private static CallSessionFactory BuildFactory(string yaml, string root)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new RoutingChatClientFactory(new ToolCallingChatClient(
            "hello there.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fileName"] = "notes.md",
                ["content"] = "hello",
            }));

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { WorkspaceRoot = root });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            workspaceRoot: root);
    }

    private static string[] FindRecursive(string root, string fileName)
        => Directory.Exists(root)
            ? Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            : [];

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
    /// Like <see cref="ToolCallingChatClient"/>, but runs <see cref="BeforeToolCall"/> immediately
    /// before it emits the one tool call it makes — the model-side moment a mid-turn
    /// <c>EndCall</c> lands at, before the framework has actually run the tool.
    /// </summary>
    private sealed class EndCallDuringToolChatClient : IChatClient
    {
        private const string CallId = "call_1";

        private readonly string _reply;
        private readonly Dictionary<string, object?> _arguments;
        private bool _answered;

        public EndCallDuringToolChatClient(string reply, Dictionary<string, object?> arguments)
        {
            _reply = reply;
            _arguments = arguments;
        }

        public Action? BeforeToolCall { get; set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var answeredAlready = messages.Any(
                message => message.Contents.Any(content => content is FunctionResultContent));
            var tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();
            var responseId = Guid.NewGuid().ToString("N");

            if (tool is not null && !answeredAlready && !_answered)
            {
                _answered = true;
                BeforeToolCall?.Invoke();

                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(CallId, tool.Name, _arguments)])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
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

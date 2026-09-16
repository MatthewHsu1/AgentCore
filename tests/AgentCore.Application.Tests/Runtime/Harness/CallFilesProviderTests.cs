using System.Text.Json;

using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.

/// <summary>
/// <see cref="CallFilesProvider"/> as a unit: it serves the framework's file tools rooted at the
/// workspace of the turn filed for the invoking session, and refuses to serve any when no turn
/// is filed.
/// </summary>
public sealed class CallFilesProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"call-files-{Guid.NewGuid():N}");

    public CallFilesProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InvokingAsync_WithASessionFiledTurn_ServesFileTools()
    {
        using CallFilesProvider provider = new(_root, Options());
        StubSession session = new();
        TurnRegistry.Set(
            session,
            new TurnInvocation { CallId = "call-1", TurnIndex = 0, Stage = "", Workspace = _root });

        var context = await provider.InvokingAsync(
            Invoking(session), TestContext.Current.CancellationToken);

        Assert.NotNull(context.Tools);
        Assert.NotEmpty(context.Tools);
    }

    [Fact]
    public async Task InvokingAsync_WithoutATurn_ThrowsInvalidOperationException()
    {
        using CallFilesProvider provider = new(_root, Options());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.InvokingAsync(
                Invoking(new StubSession()), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("files: tool", exception.Message, StringComparison.Ordinal);
    }

    private static FileAccessProviderOptions Options() => new()
    {
        DisableReadOnlyToolApproval = true,
        DisableWriteToolApproval = true,
    };

    private static AIContextProvider.InvokingContext Invoking(AgentSession session) => new(
        StubAgent.Instance,
        session,
        new AIContext { Messages = [new ChatMessage(ChatRole.User, "list my files")] });

    private sealed class StubSession : AgentSession;

    private sealed class StubAgent : AIAgent
    {
        public static StubAgent Instance { get; } = new();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default)
            => new(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

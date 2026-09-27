using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness
{
#pragma warning disable MAAI001 // AIContextProvider.InvokingContext is evaluation-only in Microsoft.Agents.AI 1.21.0.

    /// <summary>
    /// <see cref="ConversationShellProvider"/> and <see cref="ConversationShellEnvironmentProvider"/> as units:
    /// with no turn filed on the invoking session (a delegated agent's run), both serve an
    /// empty context instead of throwing (design §6.3#6, inverse of probe P11c).
    /// </summary>
    public sealed class ConversationShellProviderTests
    {
        [Fact]
        public async Task ConversationShellProvider_WithoutATurn_ServesAnEmptyContext()
        {
            ConversationShellProvider provider = new(Options());

            AIContext context = await provider.InvokingAsync(Invoking(), TestContext.Current.CancellationToken);

            Assert.Null(context.Tools);
        }

        [Fact]
        public async Task ConversationShellEnvironmentProvider_WithoutATurn_ServesAnEmptyContext()
        {
            ConversationShellEnvironmentProvider provider = new(Options());

            AIContext context = await provider.InvokingAsync(Invoking(), TestContext.Current.CancellationToken);

            Assert.Null(context.Instructions);
        }

        private static ConversationShellOptions Options()
        {
            return new(ShellKind.Docker, null, null, null, null, null, null, null);
        }

        private static AIContextProvider.InvokingContext Invoking()
        {
            return new(StubAgent.Instance, new StubSession(), new AIContext());
        }

        private sealed class StubSession : AgentSession;

        private sealed class StubAgent : AIAgent
        {
            public static StubAgent Instance { get; } = new();

            protected override ValueTask<AgentSession> CreateSessionCoreAsync(
                CancellationToken cancellationToken = default)
            {
                return new(new StubSession());
            }

            protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
                AgentSession session,
                JsonSerializerOptions? jsonSerializerOptions = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
                JsonElement serializedState,
                JsonSerializerOptions? jsonSerializerOptions = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override Task<AgentResponse> RunCoreAsync(
                IEnumerable<ChatMessage> messages,
                AgentSession? session = null,
                AgentRunOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
                IEnumerable<ChatMessage> messages,
                AgentSession? session = null,
                AgentRunOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }

#pragma warning restore MAAI001
}

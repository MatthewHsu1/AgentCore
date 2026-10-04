using System.Text.Json;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>Records the turn its run finds on the options and on its session.</summary>
    internal sealed class TurnProbeAgent : AIAgent
    {
        public TurnInvocation? FromOptions { get; private set; }

        public TurnInvocation? FromSession { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return new(new Session());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            FromOptions = TurnInvocation.From(CurrentRunContext?.RunOptions);
            FromSession = TurnRegistry.For(session);
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>A session that carries nothing; the registry keys on its identity.</summary>
        internal sealed class Session : AgentSession;
    }
}

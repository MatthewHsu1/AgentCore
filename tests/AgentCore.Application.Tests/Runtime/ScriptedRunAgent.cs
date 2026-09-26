using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Tests.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// An agent with no layers of its own: it streams its fragments as assistant text, then throws when it holds a
    /// fault. It records whether its stream reached its <c>finally</c>.
    /// </summary>
    internal sealed class ScriptedRunAgent(IReadOnlyList<string> fragments, Exception? fault = null) : AIAgent
    {
        /// <summary>Gets whether the stream's <c>finally</c> ran, which it does only when the stream ends or is disposed.</summary>
        public bool StreamClosed { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return new(new StubSession());
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
            throw new NotSupportedException();
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                foreach (string fragment in fragments)
                {
                    await Task.Yield();
                    yield return new AgentResponseUpdate(ChatRole.Assistant, fragment) { MessageId = "m0" };
                }

                if (fault is not null)
                {
                    throw fault;
                }
            }
            finally
            {
                StreamClosed = true;
            }
        }
    }
}

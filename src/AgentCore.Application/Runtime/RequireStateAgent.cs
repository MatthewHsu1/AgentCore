using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Fails a guarded row 4 graph before it starts when the state its edges read is not there.
/// </summary>
internal sealed class RequireStateAgent : DelegatingAIAgent
{
    private readonly Func<IReadOnlyDictionary<string, JsonNode?>> _state;

    /// <summary>Puts the state pre-check in front of one guarded graph.</summary>
    public RequireStateAgent(AIAgent inner, Func<IReadOnlyDictionary<string, JsonNode?>> state)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = _state();
        return base.RunCoreAsync(messages, session, options, cancellationToken);
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = _state();
        return base.RunCoreStreamingAsync(messages, session, options, cancellationToken);
    }
}

using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Agents.Graph
{
    /// <summary>
    /// Files the turn's state snapshot at the head of a guarded graph run, where the
    /// graph-state entry strips it into workflow-scoped state for the guard gates.
    /// </summary>
    internal sealed class GraphStateAgent : DelegatingAIAgent
    {
        private readonly string _name;

        /// <summary>Puts the state filing in front of one guarded graph.</summary>
        /// <param name="inner">The graph workflow, already wrapped by <c>AsAIAgent()</c>.</param>
        /// <param name="name">The name of the document, which the failure reports.</param>
        public GraphStateAgent(AIAgent inner, string name)
            : base(inner)
        {
            ArgumentNullException.ThrowIfNull(name);
            _name = name;
        }

        /// <inheritdoc />
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return base.RunCoreAsync(Filed(messages, session, options), session, options, cancellationToken);
        }

        /// <inheritdoc />
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return base.RunCoreStreamingAsync(Filed(messages, session, options), session, options, cancellationToken);
        }

        private List<ChatMessage> Filed(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options)
        {
            ArgumentNullException.ThrowIfNull(messages);

            TurnInvocation? invocation = TurnRegistry.For(session) ?? TurnInvocation.From(options);
            IReadOnlyDictionary<string, JsonNode?>? snapshot = invocation?.State?.Snapshot();

            return snapshot is null
                ? throw new InvalidOperationException(
                    $"The graph '{_name}' runs a guarded edge with no state filed on the run. "
                    + GraphGuardGate.NoStateMessage)
                : [GraphStateCarrier.Build(snapshot), .. messages];
        }
    }
}

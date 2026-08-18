namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The built-in workflow shapes of row 3 of the compile table in section 8.2.
/// </summary>
public enum GraphPattern
{
    /// <summary><c>AgentWorkflowBuilder.BuildSequential</c>.</summary>
    Sequential,

    /// <summary><c>AgentWorkflowBuilder.BuildConcurrent</c>.</summary>
    Concurrent,

    /// <summary><c>AgentWorkflowBuilder.CreateHandoffBuilderWith</c>.</summary>
    /// <remarks>
    /// The first agent in <c>graph.agents</c> starts the handoff, and every other agent is a target it
    /// hands to. That is fixed, and no document key moves it. D15 makes a new public key a permanent
    /// obligation, so the order of <c>graph.agents</c> carries the decision instead.
    /// </remarks>
    Handoff,

    /// <summary><c>AgentWorkflowBuilder.CreateGroupChatBuilderWith</c>.</summary>
    /// <remarks>
    /// The group chat is round-robin, through <c>RoundRobinGroupChatManager</c>. That is fixed, and no
    /// document key selects another manager. D15 makes a new public key a permanent obligation, so a
    /// document that needs another turn order builds an explicit graph of row 4 instead.
    /// </remarks>
    GroupChat,
}

/// <summary>
/// One node of an explicit graph. An agent binds to it as an executor.
/// </summary>
public sealed record GraphNodeConfiguration
{
    /// <summary>Gets the node id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the id of the agent bound to this node, or <see langword="null"/>.</summary>
    public string? Agent { get; init; }

    /// <summary>Gets whether the run starts here. Check 7 needs exactly one start node.</summary>
    public bool Start { get; init; }

    /// <summary>Gets whether the node yields an output. Check 7 needs every path to reach one.</summary>
    public bool Output { get; init; }
}

/// <summary>
/// One edge of an explicit graph.
/// </summary>
public sealed record GraphEdgeConfiguration
{
    /// <summary>Gets the id of the node the edge leaves.</summary>
    public required string From { get; init; }

    /// <summary>Gets the id of the node the edge reaches.</summary>
    public required string To { get; init; }

    /// <summary>Gets the guard on the edge, or <see langword="null"/> when the edge is unconditional.</summary>
    public GuardReference? When { get; init; }
}

/// <summary>
/// The <c>graph:</c> section. It holds either a pattern or a node and edge list, never both.
/// </summary>
/// <remarks>
/// Section 8.2 compiles the first form through <c>AgentWorkflowBuilder</c> and the second through
/// <c>WorkflowBuilder</c> and <c>AsAIAgent()</c>. A document that holds both <c>policy:</c> and
/// <c>graph:</c> is a load-time error.
/// </remarks>
public sealed record GraphConfiguration
{
    /// <summary>Gets the built-in shape, or <see langword="null"/> when the graph lists nodes and edges.</summary>
    public GraphPattern? Pattern { get; init; }

    /// <summary>Gets the participants of a pattern graph, in order. It is empty for a node and edge graph.</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>Gets the nodes. It is empty for a pattern graph.</summary>
    public IReadOnlyList<GraphNodeConfiguration> Nodes { get; init; } = [];

    /// <summary>Gets the edges. It is empty for a pattern graph.</summary>
    public IReadOnlyList<GraphEdgeConfiguration> Edges { get; init; } = [];
}

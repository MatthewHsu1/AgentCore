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
    Handoff,

    /// <summary><c>AgentWorkflowBuilder.CreateGroupChatBuilderWith</c>.</summary>
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
    public EquatableList<string> Agents { get; init; } = EquatableList<string>.Empty;

    /// <summary>Gets the nodes. It is empty for a pattern graph.</summary>
    public EquatableList<GraphNodeConfiguration> Nodes { get; init; } = EquatableList<GraphNodeConfiguration>.Empty;

    /// <summary>Gets the edges. It is empty for a pattern graph.</summary>
    public EquatableList<GraphEdgeConfiguration> Edges { get; init; } = EquatableList<GraphEdgeConfiguration>.Empty;
}

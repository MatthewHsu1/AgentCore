namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The row of the compile table in section 8.2 that one document selects.
/// </summary>
/// <remarks>
/// The compiler is a table, not a heuristic. A document selects exactly one row, and a document that
/// selects none, or that holds both <c>policy:</c> and <c>graph:</c>, fails to load.
/// </remarks>
public enum CompiledAgentShape
{
    /// <summary>One <c>agents.items</c> entry, no <c>policy:</c>, no <c>graph:</c>. It builds a <c>ChatClientAgent</c>.</summary>
    SingleAgent,

    /// <summary><c>agents:</c> plus <c>policy:</c>. The machine picks a stage each turn, and the stage names one agent.</summary>
    Policy,

    /// <summary><c>graph:</c> with <c>pattern:</c>. It builds one of the four <c>AgentWorkflowBuilder</c> shapes.</summary>
    PatternGraph,

    /// <summary><c>graph:</c> with <c>nodes:</c> and <c>edges:</c>. It builds a <c>WorkflowBuilder</c> graph.</summary>
    ExplicitGraph,
}

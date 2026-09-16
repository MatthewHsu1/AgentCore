namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The row of the compile table in section 8.2 that one entry selects.
/// </summary>
public enum CompiledAgentShape
{
    /// <summary>The entry holds <c>agent:</c>. It builds a <c>ChatClientAgent</c>.</summary>
    SingleAgent,

    /// <summary>The entry holds <c>policy:</c>. The machine picks a stage each turn, and the stage names one agent.</summary>
    Policy,

    /// <summary>The entry holds <c>graph:</c> with <c>pattern:</c>. It builds one of the four <c>AgentWorkflowBuilder</c> shapes.</summary>
    PatternGraph,

    /// <summary>The entry holds <c>graph:</c> with <c>nodes:</c> and <c>edges:</c>. It builds a <c>WorkflowBuilder</c> graph.</summary>
    ExplicitGraph,
}

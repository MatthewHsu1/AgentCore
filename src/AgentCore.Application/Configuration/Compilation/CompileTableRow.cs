using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// One row of the section 8.2 compile table. The row owns everything the compiler decides per
/// shape, so adding a shape is one new row and nothing else.
/// </summary>
internal abstract class CompileTableRow
{
    /// <summary>Gets the <see cref="CompiledAgentShape"/> this row compiles.</summary>
    internal abstract CompiledAgentShape Shape { get; }

    /// <summary>Gets whether the row answers its runs out of store 1 on its own session.</summary>
    /// <remarks>Rows 1 and 2 ride the session. A graph run rides the request messages.</remarks>
    internal virtual bool SessionCarriesHistory => false;

    /// <summary>Builds the entry agent of one document, and the stage table when the row has one.</summary>
    /// <param name="configuration">The loaded document. It already selected this row.</param>
    /// <param name="agents">The compiled <c>agents.items</c> entries, keyed by id.</param>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The agent a turn runs, and the agent id each <c>policy.stages</c> entry names.</returns>
    /// <exception cref="ConfigurationLoadException">The document does not compile through this row.</exception>
    internal abstract (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context);

    /// <summary>Names the agents whose reply the caller actually hears.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <returns>
    /// The <c>agents.items</c> ids that answer the caller, or <see langword="null"/> when the last
    /// thing the run produced is the answer.
    /// </returns>
    internal virtual HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration) => null;

    private protected static Dictionary<string, string> NoStages() => new(StringComparer.Ordinal);
}

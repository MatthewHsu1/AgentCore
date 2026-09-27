using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>
    /// Turns one entry into the agent a turn runs.
    /// </summary>
    internal abstract class CompileTableRow
    {
        /// <summary>Gets the <see cref="CompiledAgentShape"/> this row compiles.</summary>
        internal abstract CompiledAgentShape Shape { get; }

        /// <summary>Gets whether the row answers its runs out of store 1 on its own session.</summary>
        internal virtual bool SessionCarriesHistory => false;

        /// <summary>Builds the entry agent of one entry, and the stage table when the row has one.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <param name="entryName">The entry key. Graph rows build workflows under it.</param>
        /// <param name="entry">The entry being compiled. It already selected this row.</param>
        /// <param name="entryPointer">The JSON Pointer to the entry, <c>/entries/&lt;name&gt;</c>.</param>
        /// <param name="agents">The compiled <c>agents.items</c> entries, keyed by id.</param>
        /// <param name="context">The seams the document names.</param>
        /// <param name="reusesGraphSession">
        /// <see langword="true"/> when a conversation on this entry keeps one MAF session across turns
        /// (<c>!SessionCarriesHistory &amp;&amp; HarnessStateKeys.Count &gt; 0</c>, the same test
        /// <see cref="AgentCore.Application.Runtime.ConversationSession.ReusesGraphSession"/> runs). Only the graph rows read it.
        /// </param>
        /// <returns>The agent a turn runs, and the agent id each <c>policy.stages</c> entry names.</returns>
        /// <exception cref="ConfigurationLoadException">The entry does not compile through this row.</exception>
        internal abstract EntryBuild BuildEntry(
            AgentCoreConfiguration configuration,
            string entryName,
            EntryConfiguration entry,
            string entryPointer,
            Dictionary<string, AIAgent> agents,
            AgentCompilationContext context,
            bool reusesGraphSession);

        /// <summary>Names the agents whose reply the caller actually hears.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <param name="entry">The entry being compiled.</param>
        /// <returns>
        /// The <c>agents.items</c> ids that answer the caller, or <see langword="null"/> when the last
        /// thing the run produced is the answer.
        /// </returns>
        internal virtual HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration, EntryConfiguration entry)
        {
            return null;
        }

        private protected static Dictionary<string, string> NoStages()
        {
            return new(StringComparer.Ordinal);
        }
    }
}

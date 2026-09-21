using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>
    /// Collects what one entry compiles from, then builds the <see cref="CompiledAgent"/> once every
    /// part is present. <c>required</c> makes the compiler enforce completeness, so a new part is one
    /// property here and one on the record that owns it; the constructor never grows.
    /// </summary>
    internal sealed class CompiledAgentBuilder
    {
        /// <summary>Gets what every entry of the document shares.</summary>
        public required CompiledDocument Document { get; init; }

        /// <summary>Gets the entry key. It is the agent's name.</summary>
        public required string EntryName { get; init; }

        /// <summary>Gets the entry's stage machine, or <see langword="null"/> when it holds none.</summary>
        public PolicyConfiguration? Policy { get; init; }

        /// <summary>Gets the row of the compile table the entry selected.</summary>
        public required CompileTableRow Row { get; init; }

        /// <summary>Gets what the row built for the entry.</summary>
        public required EntryBuild Entry { get; init; }

        /// <summary>Gets the <c>agents.items</c> entries the row built on.</summary>
        public required CompiledAgentSet Agents { get; init; }

        /// <summary>Gets the turn layers of the entry.</summary>
        public required TurnLayers Layers { get; init; }

        /// <summary>Builds the agent, putting the turn layers on every agent a turn runs.</summary>
        /// <returns>The compiled entry.</returns>
        public CompiledAgent Build()
        {
            ArgumentException.ThrowIfNullOrEmpty(EntryName);

            return new CompiledAgent(this);
        }
    }
}

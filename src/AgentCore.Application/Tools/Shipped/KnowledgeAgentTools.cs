using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>
/// The four knowledge built-ins, built at the names the agentic search agent calls them by.
/// </summary>
/// <remarks>
/// <para>
/// A shipped agent's tools are its own, so these are not the document's declarations and the
/// document need not declare any of them. Each one is still built by the same
/// <see cref="IBuiltinToolDefinition"/> a declared tool goes through, handed a declaration whose id
/// is the inner name. That keeps one description and one unbound-port check per knowledge tool in
/// the whole codebase, rather than a second copy that can drift from the first.
/// </para>
/// <para>
/// The names are short because the agent's instructions spell them, and they cost prompt tokens on
/// every inner round. They are not the <c>knowledge.*</c> ids a document writes, and they never
/// reach the outer agent's context; a propagating fault does name one in the audit chain.
/// </para>
/// </remarks>
internal static class KnowledgeAgentTools
{
    /// <summary>Ranks passages for one query.</summary>
    internal const string Search = "search";

    /// <summary>Opens one whole document.</summary>
    internal const string Read = "read";

    /// <summary>Names the documents a glob keeps.</summary>
    internal const string List = "list";

    /// <summary>Finds the lines one regular expression matches.</summary>
    internal const string Grep = "grep";

    private static readonly (string Name, IBuiltinToolDefinition Definition)[] Inner =
    [
        (Search, new KnowledgeSearchDefinition()),
        (Read, new KnowledgeReadDefinition()),
        (List, new KnowledgeListDefinition()),
        (Grep, new KnowledgeGrepDefinition()),
    ];

    /// <summary>The inner names, in the order the instructions introduce them.</summary>
    internal static IReadOnlyList<string> Names { get; } = [.. Inner.Select(entry => entry.Name)];

    /// <summary>Builds the tools the agentic search agent calls.</summary>
    /// <param name="ports">The adapters the host bound.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="Configuration.Parsing.ConfigurationLoadException">
    /// A knowledge port one of them reads is unbound.
    /// </exception>
    internal static IReadOnlyList<AITool> Build(BuiltinToolPorts ports)
    {
        List<AITool> tools = [];

        foreach (var (name, definition) in Inner)
        {
            var declared = BuiltinToolSource.Described(
                new ToolConfiguration { Id = name, Kind = ToolKind.Builtin, Uses = definition.Name },
                definition);

            tools.Add(definition.Build(declared, ports));
        }

        return tools;
    }
}

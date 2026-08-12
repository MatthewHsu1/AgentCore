using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// The one <see cref="IAgentToolFactory"/> the compile table asks, over the three kinds that need one.
/// </summary>
/// <remarks>
/// <para>
/// It holds an ordered list of links and asks each one until a link answers. A link answers
/// <see langword="null"/> for a kind it does not serve, so a host adds a kind by adding a link and
/// changes nothing else.
/// </para>
/// <para>
/// <see cref="ToolKind.Agent"/> reaches no link at all, and this factory answers
/// <see langword="null"/> for it. That kind names another declared agent, so the compile table
/// already holds everything it needs and builds it through <c>AsAIFunction()</c>.
/// </para>
/// <para>
/// Any other kind that no link serves is a startup failure. The compile table drops a
/// <see langword="null"/> quietly, which is right for the agent kind and wrong for every other one:
/// a tool the document declares and an agent lists has to reach the model.
/// </para>
/// </remarks>
public sealed class CompositeAgentToolFactory : IAgentToolFactory
{
    private readonly IAgentToolFactory[] _links;

    /// <summary>Creates the factory.</summary>
    /// <param name="links">The links, in the order this factory asks them.</param>
    public CompositeAgentToolFactory(IEnumerable<IAgentToolFactory> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        _links = [.. links];
        foreach (var link in _links)
        {
            ArgumentNullException.ThrowIfNull(link, nameof(links));
        }
    }

    /// <summary>Gets the number of links.</summary>
    public int Count => _links.Length;

    /// <summary>Builds the tool one declaration names.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <returns>The tool, or <see langword="null"/> when the kind is <see cref="ToolKind.Agent"/>.</returns>
    /// <exception cref="ConfigurationLoadException">No link serves the kind.</exception>
    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Kind == ToolKind.Agent)
        {
            return null;
        }

        foreach (var link in _links)
        {
            if (link.Create(tool) is { } built)
            {
                return built;
            }
        }

        throw new ConfigurationLoadException(new ConfigurationError
        {
            Pointer = "/tools",
            Message = $"the tool '{tool.Id}' is kind: {tool.Kind.ToString().ToLowerInvariant()}, and no tool "
                      + "factory in the chain builds that kind. Bind one before the document compiles.",
            Check = ConfigurationCheck.ReferenceResolution,
        });
    }
}

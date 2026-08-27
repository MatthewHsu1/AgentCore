using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>One agent AgentCore ships, describing itself.</summary>
/// <remarks>
/// A shipped agent is an inner agent with baked-in instructions and its own tools. It is not a
/// document agent: it cannot name a model preference, because <c>{ ref: ... }</c> points at the
/// host's own <c>providers.llm[].as</c> entries and a shipped agent cannot know them. The document
/// names one with <c>model:</c>, or names none and the host default applies.
/// </remarks>
internal interface IShippedAgentDefinition : IToolDefinition
{
    /// <summary>The instructions. They are the contract, so a document never overrides them.</summary>
    string Instructions { get; }

    /// <summary>The round cap when the document writes no <c>maxRounds:</c>.</summary>
    int DefaultMaxRounds { get; }

    /// <summary>The tools this agent calls. They are its own, never the document's.</summary>
    /// <param name="tool">The declaration, so a tool can name the declared id in its own errors.</param>
    /// <param name="ports">The adapters the host bound, for an inner tool that needs one of its own.</param>
    /// <returns>The inner tools.</returns>
    IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports);

    /// <summary>Names the one port this agent needs and the host did not bind.</summary>
    /// <param name="ports">The adapters the host bound.</param>
    /// <returns>The port name, or <see langword="null"/> when everything it needs is bound.</returns>
    string? MissingPort(BuiltinToolPorts ports);
}

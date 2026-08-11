using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Turns one declared tool into the <see cref="AITool"/> an agent advertises.
/// </summary>
/// <remarks>
/// <para>
/// Three kinds of section 8.1 bind here: a <c>builtin</c> tool AgentCore ships, an <c>http</c> tool
/// the HTTP adapter runs, and a <c>binding</c> tool that calls a host delegate. A <c>builtin</c>
/// tool returns an error result and does not throw. See section 8.7.
/// </para>
/// <para>
/// The fourth kind, <c>agent</c>, does not reach this factory. It names another declared agent, so
/// the compile table already holds everything it needs and builds it through
/// <c>AIAgentExtensions.AsAIFunction()</c>. Section 7 stays true: section 8 adds no port.
/// </para>
/// </remarks>
public interface IAgentToolFactory
{
    /// <summary>Builds the tool one declaration names.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <returns>The tool, or <see langword="null"/> when this factory does not serve that kind.</returns>
    AITool? Create(ToolConfiguration tool);
}

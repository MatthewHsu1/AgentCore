using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The adapters a built-in may need. A built-in uses none, one, or two of them.</summary>
/// <param name="Retrieval">The adapter <c>knowledge.search</c> ranks with, or <see langword="null"/>.</param>
/// <param name="Documents">The adapter the three document built-ins open, or <see langword="null"/>.</param>
/// <param name="ChatClients">Finds the chat client factory <c>ui.draw</c> draws with.</param>
public sealed record BuiltinToolPorts(
    IKnowledgeRetrievalPort? Retrieval,
    IDocumentStorePort? Documents,
    Func<IChatClientFactory?> ChatClients);

/// <summary>One tool AgentCore ships, describing itself.</summary>
internal interface IBuiltinToolDefinition
{
    /// <summary>The name a <c>uses:</c> field writes.</summary>
    string Name { get; }

    /// <summary>The sentence the model reads when the document writes no <c>description:</c>.</summary>
    string DefaultDescription { get; }

    /// <summary>Builds the tool.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <param name="ports">The adapters the host bound.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ConfigurationLoadException">A port this built-in reads is unbound.</exception>
    AITool Build(ToolConfiguration tool, BuiltinToolPorts ports);
}

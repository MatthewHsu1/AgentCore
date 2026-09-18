using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The adapters a built-in may need. A built-in uses none or more of them.</summary>
/// <param name="ChatClients">The factory the tool runs on, or <see langword="null"/> when the host bound none.</param>
/// <param name="Blobs">The store <c>providers.blobs</c> opened, or <see langword="null"/> when the document names none.</param>
/// <param name="BlobPolicy">The cap and the allowlist a published file must pass, or <see langword="null"/> for <see cref="Blobs.BlobPolicy.Default"/>.</param>
/// <param name="WorkspaceRoot">The root <c>options.UseWorkspace(...)</c> bound, or <see langword="null"/> when the host bound none.</param>
/// <param name="Loggers">Where a built-in takes its logger from, or <see langword="null"/> to log nothing.</param>
public sealed record BuiltinToolPorts(
    IChatClientFactory? ChatClients,
    IBlobStore? Blobs = null,
    BlobPolicy? BlobPolicy = null,
    string? WorkspaceRoot = null,
    ILoggerFactory? Loggers = null);

/// <summary>What every <c>uses:</c> name AgentCore ships has, whatever kind of thing it builds.</summary>
internal interface IToolDefinition
{
    /// <summary>The name a <c>uses:</c> field writes.</summary>
    string Name { get; }

    /// <summary>The sentence the model reads when the document writes no <c>description:</c>.</summary>
    string DefaultDescription { get; }
}

/// <summary>One tool AgentCore ships, describing itself.</summary>
internal interface IBuiltinToolDefinition : IToolDefinition
{
    /// <summary>Builds the tool.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <param name="ports">The adapters the host bound.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ConfigurationLoadException">A port this built-in reads is unbound.</exception>
    AITool Build(ToolConfiguration tool, BuiltinToolPorts ports);
}

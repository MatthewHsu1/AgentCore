using System.ComponentModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The <c>knowledge.read</c> definition.</summary>
internal sealed class KnowledgeReadDefinition : IBuiltinToolDefinition
{
    public string Name => BuiltinToolNames.KnowledgeRead;

    public string DefaultDescription
        => "Read one whole document from the knowledge base by its id.";

    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        => KnowledgeReadTool.Create(
            tool,
            ports.Documents ?? throw BuiltinToolSource.Unbound(
                tool, BuiltinToolNames.KnowledgeRead, nameof(IDocumentStorePort)));
}

/// <summary>The <c>knowledge.read</c> built-in.</summary>
internal sealed class KnowledgeReadTool
{
    private readonly string _toolId;
    private readonly IDocumentStorePort _documents;

    private KnowledgeReadTool(string toolId, IDocumentStorePort documents)
    {
        _toolId = toolId;
        _documents = documents;
    }

    /// <summary>Builds the tool the model calls.</summary>
    internal static AIFunction Create(ToolConfiguration tool, IDocumentStorePort documents)
        => AIFunctionFactory.Create(
            new KnowledgeReadTool(tool.Id, documents).ReadAsync,
            BuiltinToolOptions.Options(tool));

    private async Task<JsonObject> ReadAsync(
        [Description("The id a search result named.")] string documentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(documentId))
        {
            return ToolErrorResult.Create(_toolId, "the call filled no 'documentId', so there is nothing to read.");
        }

        var document = await _documents.ReadAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return ToolErrorResult.Create(
                _toolId,
                $"the knowledge base holds no document '{documentId}'. Search for one first.");
        }

        return new JsonObject
        {
            ["documentId"] = document.DocumentId,
            ["text"] = document.Text,
        };
    }
}

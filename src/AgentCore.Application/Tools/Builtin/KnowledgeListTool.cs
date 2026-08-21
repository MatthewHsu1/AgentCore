using System.ComponentModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The <c>knowledge.list</c> definition.</summary>
internal sealed class KnowledgeListDefinition : IBuiltinToolDefinition
{
    public string Name => BuiltinToolNames.KnowledgeList;

    public string DefaultDescription
        => "Name the knowledge base documents whose path matches one glob.";

    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        => KnowledgeListTool.Create(
            tool,
            ports.Documents ?? throw BuiltinToolSource.Unbound(
                tool, BuiltinToolNames.KnowledgeList, nameof(IDocumentStorePort)));
}

/// <summary>The <c>knowledge.list</c> built-in.</summary>
internal sealed class KnowledgeListTool
{
    private readonly IDocumentStorePort _documents;

    private KnowledgeListTool(IDocumentStorePort documents) => _documents = documents;

    /// <summary>Builds the tool the model calls.</summary>
    internal static AIFunction Create(ToolConfiguration tool, IDocumentStorePort documents)
        => AIFunctionFactory.Create(
            new KnowledgeListTool(documents).ListAsync,
            BuiltinToolOptions.Options(tool));

    private async Task<JsonObject> ListAsync(
        [Description("A glob over document ids, such as policies/**/*.md. Leave it out to name every document.")]
        string pattern = "",
        CancellationToken cancellationToken = default)
    {
        // An empty pattern is the silent default of row T46, and it is not a glob that keeps
        // nothing. It means the model asked for no pattern at all.
        var listing = await _documents.ListAsync(string.IsNullOrEmpty(pattern) ? null : pattern, cancellationToken)
            .ConfigureAwait(false);

        JsonArray documentIds = [];

        foreach (var documentId in listing.DocumentIds)
        {
            documentIds.Add(documentId);
        }

        return new JsonObject
        {
            ["documentIds"] = documentIds,
            ["truncated"] = listing.Truncated,
        };
    }
}

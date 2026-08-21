using System.ComponentModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The <c>knowledge.grep</c> definition.</summary>
internal sealed class KnowledgeGrepDefinition : IBuiltinToolDefinition
{
    public string Name => BuiltinToolNames.KnowledgeGrep;

    public string DefaultDescription
        => "Find the knowledge base lines that match one regular expression.";

    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        => KnowledgeGrepTool.Create(
            tool,
            ports.Documents ?? throw BuiltinToolSource.Unbound(
                tool, BuiltinToolNames.KnowledgeGrep, nameof(IDocumentStorePort)));
}

/// <summary>The <c>knowledge.grep</c> built-in.</summary>
internal sealed class KnowledgeGrepTool
{
    private readonly string _toolId;
    private readonly IDocumentStorePort _documents;

    private KnowledgeGrepTool(string toolId, IDocumentStorePort documents)
    {
        _toolId = toolId;
        _documents = documents;
    }

    /// <summary>Builds the tool the model calls.</summary>
    internal static AIFunction Create(ToolConfiguration tool, IDocumentStorePort documents)
        => AIFunctionFactory.Create(
            new KnowledgeGrepTool(tool.Id, documents).GrepAsync,
            BuiltinToolOptions.Options(tool));

    private async Task<JsonObject> GrepAsync(
        [Description("The regular expression each line is matched against.")] string pattern,
        [Description("A glob over document ids, such as policies/**/*.md, that says which documents to read.")]
        string glob = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return ToolErrorResult.Create(_toolId, "the call filled no 'pattern', so there is nothing to search for.");
        }

        var found = await _documents.GrepAsync(pattern, string.IsNullOrEmpty(glob) ? null : glob, cancellationToken)
            .ConfigureAwait(false);

        JsonArray matches = [];
        foreach (var match in found.Matches)
        {
            matches.Add(new JsonObject
            {
                ["documentId"] = match.DocumentId,
                ["lineNumber"] = match.LineNumber,
                ["line"] = match.Line,
            });
        }

        return new JsonObject
        {
            ["matches"] = matches,
            ["truncated"] = found.Truncated,
        };
    }
}

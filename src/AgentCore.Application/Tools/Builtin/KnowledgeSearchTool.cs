using System.ComponentModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The <c>knowledge.search</c> definition.</summary>
internal sealed class KnowledgeSearchDefinition : IBuiltinToolDefinition
{
    public string Name => BuiltinToolNames.KnowledgeSearch;

    public string DefaultDescription
        => "Search the knowledge base and return the passages that best answer one query.";

    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        => KnowledgeSearchTool.Create(
            tool,
            ports.Retrieval ?? throw BuiltinToolSource.Unbound(
                tool, BuiltinToolNames.KnowledgeSearch, nameof(IKnowledgeRetrievalPort)));
}

/// <summary>The <c>knowledge.search</c> built-in.</summary>
internal sealed class KnowledgeSearchTool
{
    /// <summary>The number of passages a call returns when the model asks for no limit.</summary>
    private const int DefaultLimit = 5;

    private readonly string _toolId;
    private readonly IKnowledgeRetrievalPort _retrieval;

    private KnowledgeSearchTool(string toolId, IKnowledgeRetrievalPort retrieval)
    {
        _toolId = toolId;
        _retrieval = retrieval;
    }

    /// <summary>Builds the tool the model calls.</summary>
    internal static AIFunction Create(ToolConfiguration tool, IKnowledgeRetrievalPort retrieval)
        => AIFunctionFactory.Create(
            new KnowledgeSearchTool(tool.Id, retrieval).SearchAsync,
            BuiltinToolOptions.Options(tool));

    private async Task<JsonObject> SearchAsync(
        [Description("What to look for.")] string query,
        [Description("The largest number of passages to return.")] int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            return ToolErrorResult.Create(_toolId, "the call filled no 'query', so there is nothing to search for.");
        }

        // The model writes the limit, so it is clamped rather than trusted: a zero returns nothing
        // and a thousand returns the whole knowledge base down the telephone.
        var chunks = await _retrieval.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken)
            .ConfigureAwait(false);

        JsonArray results = [];
        foreach (var chunk in chunks)
        {
            results.Add(new JsonObject
            {
                ["documentId"] = chunk.DocumentId,
                ["text"] = chunk.Text,
                ["score"] = chunk.Score,
            });
        }

        return new JsonObject { ["chunks"] = results };
    }
}

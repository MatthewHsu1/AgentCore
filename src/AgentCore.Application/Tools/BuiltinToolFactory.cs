using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>The name of every tool AgentCore ships. <c>uses:</c> names one of these.</summary>
public static class BuiltinToolNames
{
    /// <summary>Ranks the knowledge-base passages that answer one query.</summary>
    public const string KnowledgeSearch = "knowledge.search";

    /// <summary>Reads one whole knowledge-base document.</summary>
    public const string KnowledgeRead = "knowledge.read";
}

/// <summary>
/// Builds the <c>kind: builtin</c> tools.
/// </summary>
/// <remarks>
/// Two names ship in this release, and both bind to <see cref="IKnowledgePort"/>. A <c>uses:</c>
/// name nothing answers is a startup failure: the document names a tool AgentCore does not have, and
/// an agent that quietly loses a tool is the silent failure the checks exist to stop.
/// </remarks>
public sealed class BuiltinToolFactory : IAgentToolFactory
{
    private readonly IKnowledgePort _knowledge;

    /// <summary>Creates the factory.</summary>
    /// <param name="knowledge">The knowledge base both built-in tools read.</param>
    public BuiltinToolFactory(IKnowledgePort knowledge)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        _knowledge = knowledge;
    }

    /// <summary>Builds one built-in tool.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <returns>The tool, or <see langword="null"/> when the kind is not <see cref="ToolKind.Builtin"/>.</returns>
    /// <exception cref="ConfigurationLoadException">The <c>uses:</c> name is not one AgentCore ships.</exception>
    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Kind != ToolKind.Builtin)
        {
            return null;
        }

        return tool.Uses switch
        {
            BuiltinToolNames.KnowledgeSearch => new KnowledgeSearchTool(tool, _knowledge),
            BuiltinToolNames.KnowledgeRead => new KnowledgeReadTool(tool, _knowledge),
            _ => throw new ConfigurationLoadException(new ConfigurationError
            {
                Pointer = "/tools",
                Message = $"the tool '{tool.Id}' is kind: builtin and uses: '{tool.Uses}', which AgentCore does "
                          + $"not ship. This release ships {BuiltinToolNames.KnowledgeSearch} and "
                          + $"{BuiltinToolNames.KnowledgeRead}.",
                Check = ConfigurationCheck.ReferenceResolution,
            }),
        };
    }
}

/// <summary>The <c>knowledge.search</c> built-in.</summary>
internal sealed class KnowledgeSearchTool : DeclaredTool
{
    /// <summary>The shape the model fills when the document declares no <c>parameters:</c>.</summary>
    private const string DefaultSchema =
        """{"type":"object","properties":{"query":{"type":"string","description":"What to look for."},"limit":{"type":"integer","description":"The largest number of passages to return."}},"required":["query"]}""";

    /// <summary>The number of passages a call returns when the model asks for no limit.</summary>
    private const int DefaultLimit = 5;

    private readonly IKnowledgePort _knowledge;

    internal KnowledgeSearchTool(ToolConfiguration tool, IKnowledgePort knowledge)
        : base(tool, DefaultSchema) => _knowledge = knowledge;

    protected override async ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (ArgumentText(arguments, "query") is not { Length: > 0 } query)
        {
            return Failed("the call filled no 'query', so there is nothing to search for.");
        }

        var limit = Math.Clamp(ArgumentInteger(arguments, "limit", DefaultLimit), 1, 50);
        var chunks = await _knowledge.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);

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

/// <summary>The <c>knowledge.read</c> built-in.</summary>
internal sealed class KnowledgeReadTool : DeclaredTool
{
    /// <summary>The shape the model fills when the document declares no <c>parameters:</c>.</summary>
    private const string DefaultSchema =
        """{"type":"object","properties":{"documentId":{"type":"string","description":"The id a search result named."}},"required":["documentId"]}""";

    private readonly IKnowledgePort _knowledge;

    internal KnowledgeReadTool(ToolConfiguration tool, IKnowledgePort knowledge)
        : base(tool, DefaultSchema) => _knowledge = knowledge;

    protected override async ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (ArgumentText(arguments, "documentId") is not { Length: > 0 } documentId)
        {
            return Failed("the call filled no 'documentId', so there is nothing to read.");
        }

        var document = await _knowledge.ReadAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return Failed($"the knowledge base holds no document '{documentId}'. Search for one first.");
        }

        return new JsonObject
        {
            ["documentId"] = document.DocumentId,
            ["text"] = document.Text,
        };
    }
}

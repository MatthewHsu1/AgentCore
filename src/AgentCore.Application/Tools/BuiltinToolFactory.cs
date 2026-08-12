using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
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
/// <para>
/// Two names ship in this release, and each one binds to a different port:
/// <c>knowledge.search</c> reads <see cref="IKnowledgeRetrievalPort"/> and <c>knowledge.read</c>
/// reads <see cref="IDocumentStorePort"/>. A host binds one port, the other, or both, so a host with
/// a document store and no retrieval adapter still gets <c>knowledge.read</c>.
/// </para>
/// <para>
/// A <c>uses:</c> name nothing answers is a startup failure, and so is a <c>uses:</c> name whose
/// port no adapter binds. Both stop the load and both name what is missing, because an agent that
/// quietly loses a tool is the silent failure the checks exist to stop.
/// </para>
/// </remarks>
public sealed class BuiltinToolFactory : IAgentToolFactory
{
    private readonly IKnowledgeRetrievalPort? _retrieval;
    private readonly IDocumentStorePort? _documents;

    /// <summary>Creates the factory.</summary>
    /// <param name="retrieval">
    /// The adapter <c>knowledge.search</c> ranks with, or <see langword="null"/> when the host bound
    /// none.
    /// </param>
    /// <param name="documents">
    /// The adapter <c>knowledge.read</c> opens, or <see langword="null"/> when the host bound none.
    /// </param>
    /// <remarks>
    /// One object that answers both ports passes as both arguments. That is what the file store
    /// does today, and what a Zilliz retrieval adapter beside a file document store will not do.
    /// </remarks>
    public BuiltinToolFactory(IKnowledgeRetrievalPort? retrieval, IDocumentStorePort? documents)
    {
        _retrieval = retrieval;
        _documents = documents;
    }

    /// <summary>Builds one built-in tool.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <returns>The tool, or <see langword="null"/> when the kind is not <see cref="ToolKind.Builtin"/>.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// The <c>uses:</c> name is not one AgentCore ships, or no adapter binds the port that name reads.
    /// </exception>
    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Kind != ToolKind.Builtin)
        {
            return null;
        }

        return tool.Uses switch
        {
            BuiltinToolNames.KnowledgeSearch => new KnowledgeSearchTool(
                tool,
                _retrieval ?? throw Unbound(tool, BuiltinToolNames.KnowledgeSearch, nameof(IKnowledgeRetrievalPort))),
            BuiltinToolNames.KnowledgeRead => new KnowledgeReadTool(
                tool,
                _documents ?? throw Unbound(tool, BuiltinToolNames.KnowledgeRead, nameof(IDocumentStorePort))),
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

    /// <summary>Reports the built-in tool whose port no adapter binds.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <param name="uses">The <c>uses:</c> name the document wrote.</param>
    /// <param name="port">The port that name reads.</param>
    /// <returns>The failure the load throws.</returns>
    private static ConfigurationLoadException Unbound(ToolConfiguration tool, string uses, string port)
        => new(new ConfigurationError
        {
            Pointer = "/tools",
            Message = $"the tool '{tool.Id}' is kind: builtin and uses: '{uses}', which reads {port}, and no "
                      + "adapter binds that port. Bind one, or take the tool out of the document.",
            Check = ConfigurationCheck.ReferenceResolution,
        });
}

/// <summary>The <c>knowledge.search</c> built-in.</summary>
internal sealed class KnowledgeSearchTool : DeclaredTool
{
    /// <summary>The shape the model fills when the document declares no <c>parameters:</c>.</summary>
    private const string DefaultSchema =
        """{"type":"object","properties":{"query":{"type":"string","description":"What to look for."},"limit":{"type":"integer","description":"The largest number of passages to return."}},"required":["query"]}""";

    /// <summary>The number of passages a call returns when the model asks for no limit.</summary>
    private const int DefaultLimit = 5;

    private readonly IKnowledgeRetrievalPort _retrieval;

    internal KnowledgeSearchTool(ToolConfiguration tool, IKnowledgeRetrievalPort retrieval)
        : base(tool, DefaultSchema) => _retrieval = retrieval;

    protected override async ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (ArgumentText(arguments, "query") is not { Length: > 0 } query)
        {
            return Failed("the call filled no 'query', so there is nothing to search for.");
        }

        var limit = Math.Clamp(ArgumentInteger(arguments, "limit", DefaultLimit), 1, 50);
        var chunks = await _retrieval.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);

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

    private readonly IDocumentStorePort _documents;

    internal KnowledgeReadTool(ToolConfiguration tool, IDocumentStorePort documents)
        : base(tool, DefaultSchema) => _documents = documents;

    protected override async ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (ArgumentText(arguments, "documentId") is not { Length: > 0 } documentId)
        {
            return Failed("the call filled no 'documentId', so there is nothing to read.");
        }

        var document = await _documents.ReadAsync(documentId, cancellationToken).ConfigureAwait(false);
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

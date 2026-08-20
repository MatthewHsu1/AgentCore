using System.ComponentModel;
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

    /// <summary>Names the knowledge-base documents a glob keeps.</summary>
    public const string KnowledgeList = "knowledge.list";

    /// <summary>Finds the knowledge-base lines one regular expression matches.</summary>
    public const string KnowledgeGrep = "knowledge.grep";
}

/// <summary>
/// Builds the <c>kind: builtin</c> tools.
/// </summary>
/// <remarks>
/// <para>
/// Four names ship in this release, and they bind to two ports: <c>knowledge.search</c> reads
/// <see cref="IKnowledgeRetrievalPort"/>, and <c>knowledge.read</c>, <c>knowledge.list</c> and
/// <c>knowledge.grep</c> read <see cref="IDocumentStorePort"/>. A host binds one port, the other, or
/// both, so a host with a document store and no retrieval adapter still gets the three document
/// tools.
/// </para>
/// <para>
/// A <c>uses:</c> name nothing answers is a startup failure, and so is a <c>uses:</c> name whose
/// port no adapter binds. Both stop the load and both name what is missing, because an agent that
/// quietly loses a tool is the silent failure the checks exist to stop.
/// </para>
/// <para>
/// <b>Task 7b: each built-in declares its shape once.</b> Every one of these used to write its form
/// twice — a hand-written JSON Schema string beside C# that read the same argument names back out of
/// an untyped bag — and nothing kept the two halves agreeing. They are now plain C# methods handed to
/// <see cref="AIFunctionFactory"/>, which generates the schema from the parameter list, the
/// <see cref="DescriptionAttribute"/> on each parameter, and which parameters carry a default. The
/// document's own <c>parameters:</c> cannot compete with it: <c>7fc7501</c> made that key illegal on
/// <c>kind: builtin</c> in <c>agentcore-v1.schema.json</c>, precisely because a document that
/// overrode the schema made the model fill boxes the C# never read.
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
    /// The adapter <c>knowledge.read</c>, <c>knowledge.list</c> and <c>knowledge.grep</c> open, or
    /// <see langword="null"/> when the host bound none.
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
            BuiltinToolNames.KnowledgeSearch => KnowledgeSearchTool.Create(
                tool,
                _retrieval ?? throw Unbound(tool, BuiltinToolNames.KnowledgeSearch, nameof(IKnowledgeRetrievalPort))),
            BuiltinToolNames.KnowledgeRead => KnowledgeReadTool.Create(
                tool,
                _documents ?? throw Unbound(tool, BuiltinToolNames.KnowledgeRead, nameof(IDocumentStorePort))),
            BuiltinToolNames.KnowledgeList => KnowledgeListTool.Create(
                tool,
                _documents ?? throw Unbound(tool, BuiltinToolNames.KnowledgeList, nameof(IDocumentStorePort))),
            BuiltinToolNames.KnowledgeGrep => KnowledgeGrepTool.Create(
                tool,
                _documents ?? throw Unbound(tool, BuiltinToolNames.KnowledgeGrep, nameof(IDocumentStorePort))),
            _ => throw new ConfigurationLoadException(new ConfigurationError
            {
                Pointer = "/tools",
                Message = $"the tool '{tool.Id}' is kind: builtin and uses: '{tool.Uses}', which AgentCore does "
                          + $"not ship. This release ships {BuiltinToolNames.KnowledgeSearch}, "
                          + $"{BuiltinToolNames.KnowledgeRead}, {BuiltinToolNames.KnowledgeList} and "
                          + $"{BuiltinToolNames.KnowledgeGrep}.",
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

/// <summary>
/// What every built-in hands <see cref="AIFunctionFactory"/>, and the two rules they all share.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name and the description come from the document, never from the method.</b> A document
/// names its own tool (<c>id: search_chunks</c>) and writes the sentence the model reads to decide
/// when to call it, so both are set here and the C# method name is never advertised.
/// </para>
/// <para>
/// <b>No result schema.</b> <see cref="AIFunctionFactoryOptions.ExcludeResultSchema"/> keeps
/// <c>AIFunction.ReturnJsonSchema</c> null, which is what these tools advertised before. It is
/// not laziness: a built-in returns either its answer or the section 8.7 error object, and a
/// generated schema of the success shape alone would describe the failure as malformed.
/// </para>
/// <para>
/// <b>The empty argument is still checked here.</b> Row T46: a model that omits an argument often
/// sends the empty string rather than omitting the key, and an empty pattern is not a pattern that
/// keeps nothing — it is the one that keeps everything. Binding cannot see that difference, so each
/// tool that cares tests it in its own body and answers with <see cref="ToolErrorResult"/> directly.
/// An argument that is genuinely ABSENT no longer reaches the body at all: binding throws, and
/// <c>AuditingFunctionInvokingChatClient.InvokeFunctionAsync</c> turns that into the same error
/// result the model reads and can retry from.
/// </para>
/// </remarks>
internal static class BuiltinTool
{
    /// <summary>Builds the options every built-in is created with.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <returns>The options.</returns>
    internal static AIFunctionFactoryOptions Options(ToolConfiguration tool) => new()
    {
        Name = tool.Id,
        Description = tool.Description ?? string.Empty,
        ExcludeResultSchema = true,
    };
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
            BuiltinTool.Options(tool));

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
            BuiltinTool.Options(tool));

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

/// <summary>The <c>knowledge.list</c> built-in.</summary>
/// <remarks>
/// The model reads a directory before it reads a document, so it learns which ids exist rather than
/// guesses one. The store caps the answer, and <c>truncated</c> carries that cap through unchanged.
/// </remarks>
internal sealed class KnowledgeListTool
{
    private readonly IDocumentStorePort _documents;

    private KnowledgeListTool(IDocumentStorePort documents) => _documents = documents;

    /// <summary>Builds the tool the model calls.</summary>
    internal static AIFunction Create(ToolConfiguration tool, IDocumentStorePort documents)
        => AIFunctionFactory.Create(
            new KnowledgeListTool(documents).ListAsync,
            BuiltinTool.Options(tool));

    /// <remarks>
    /// <paramref name="pattern"/> defaults to the empty string and not to <see langword="null"/>. A
    /// nullable parameter makes the generated schema say <c>"type":["string","null"]</c>, and a union
    /// type is the one thing here a provider is known to reject outright; the sentinel keeps the
    /// plain <c>"type":"string"</c> the hand-written schema advertised. Nothing is lost by it,
    /// because absent and empty already meant the same thing here.
    /// </remarks>
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

/// <summary>The <c>knowledge.grep</c> built-in.</summary>
/// <remarks>
/// A model that omits a required argument still sends a call, and the missing argument arrives as a
/// silent default. That is row T46, so this tool checks the pattern itself. An empty pattern matches
/// every line of every document, which is the answer nobody asked for.
/// </remarks>
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
            BuiltinTool.Options(tool));

    /// <remarks>
    /// <paramref name="glob"/> takes the empty-string sentinel for the reason spelled out on
    /// <see cref="KnowledgeListTool"/>: absent and empty already meant the same thing, and the
    /// sentinel keeps a union type out of the schema the model reads.
    /// </remarks>
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

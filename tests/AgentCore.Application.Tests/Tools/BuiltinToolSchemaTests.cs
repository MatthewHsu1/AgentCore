using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The exact JSON Schema each <c>kind: builtin</c> tool advertises to the model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Task 7b deleted four hand-written schema literals and let
/// <see cref="AIFunctionFactory"/> generate them from the C# parameter lists instead. That trades a
/// schema nobody could forget to update for one nobody controls: a library upgrade can reshape it
/// silently, and the schema is the whole of what the model reads to decide how to call a tool. The
/// expected documents below are therefore stored in full, so an upgrade that changes so much as a
/// key fails here rather than on the telephone.
/// </para>
/// <para>
/// <b>Compared as parsed trees, never as strings.</b> Key order is not part of a JSON Schema's
/// meaning and the generator does not preserve the hand-written order — it writes
/// <c>description</c> before <c>type</c>, where the literals wrote <c>type</c> first. A string
/// comparison would fail on that and teach the next reader to loosen the test.
/// </para>
/// <para>
/// <b>What changed against the hand-written literals</b>, once, here, so the diff is not lost. Each
/// case below carries the literal it replaced. Two differences are real and deliberate:
/// </para>
/// <list type="number">
/// <item><description>
/// Every optional property gained a <c>default</c> key — <c>limit</c> its 5, <c>pattern</c> and
/// <c>glob</c> the empty string. <c>default</c> is a standard JSON Schema annotation and it
/// constrains nothing: a call that was valid against the literal is still valid against this. It is
/// accepted here because nothing in this repository sends a tool schema in a provider's strict
/// mode — no code sets <c>strict</c> — and because it states the one fact the literals left the
/// model to guess, which is what omitting the property does.
/// </description></item>
/// <item><description>
/// <c>pattern</c> and <c>glob</c> are declared <c>string</c> with an empty-string default rather
/// than as nullable C# parameters. A nullable parameter generates <c>"type":["string","null"]</c>,
/// a union some providers reject in strict schema modes. Absent and empty already meant the same
/// thing to both tools, so the sentinel costs nothing.
/// </description></item>
/// </list>
/// <para>
/// Everything else matches property for property: the same names, the same types, the same
/// descriptions word for word, and the same <c>required</c> list.
/// </para>
/// </remarks>
public sealed class BuiltinToolSchemaTests
{
    /// <summary>
    /// Was:
    /// <c>{"type":"object","properties":{"query":{"type":"string","description":"What to look for."},
    /// "limit":{"type":"integer","description":"The largest number of passages to return."}},
    /// "required":["query"]}</c>
    /// </summary>
    private const string SearchSchema =
        """
        {
          "type": "object",
          "properties": {
            "query": { "description": "What to look for.", "type": "string" },
            "limit": {
              "description": "The largest number of passages to return.",
              "type": "integer",
              "default": 5
            }
          },
          "required": ["query"]
        }
        """;

    /// <summary>
    /// Was:
    /// <c>{"type":"object","properties":{"documentId":{"type":"string","description":"The id a search
    /// result named."}},"required":["documentId"]}</c>
    /// </summary>
    private const string ReadSchema =
        """
        {
          "type": "object",
          "properties": {
            "documentId": { "description": "The id a search result named.", "type": "string" }
          },
          "required": ["documentId"]
        }
        """;

    /// <summary>
    /// Was:
    /// <c>{"type":"object","properties":{"pattern":{"type":"string","description":"A glob over document
    /// ids, such as policies/**/*.md. Leave it out to name every document."}}}</c>
    /// </summary>
    private const string ListSchema =
        """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "description": "A glob over document ids, such as policies/**/*.md. Leave it out to name every document.",
              "type": "string",
              "default": ""
            }
          }
        }
        """;

    /// <summary>
    /// Was:
    /// <c>{"type":"object","properties":{"pattern":{"type":"string","description":"The regular expression
    /// each line is matched against."},"glob":{"type":"string","description":"A glob over document ids,
    /// such as policies/**/*.md, that says which documents to read."}},"required":["pattern"]}</c>
    /// </summary>
    private const string GrepSchema =
        """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "description": "The regular expression each line is matched against.",
              "type": "string"
            },
            "glob": {
              "description": "A glob over document ids, such as policies/**/*.md, that says which documents to read.",
              "type": "string",
              "default": ""
            }
          },
          "required": ["pattern"]
        }
        """;

    public static TheoryData<string, string, string> Builtins => new()
    {
        { BuiltinToolNames.KnowledgeSearch, "search_chunks", SearchSchema },
        { BuiltinToolNames.KnowledgeRead, "read_doc", ReadSchema },
        { BuiltinToolNames.KnowledgeList, "list_docs", ListSchema },
        { BuiltinToolNames.KnowledgeGrep, "grep_docs", GrepSchema },
    };

    [Theory]
    [MemberData(nameof(Builtins))]
    public void EachBuiltIn_AdvertisesTheSchemaThisFileStores(string uses, string toolId, string expected)
    {
        var function = Build(uses, toolId);

        var actual = JsonNode.Parse(function.JsonSchema.GetRawText());

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"the schema of '{toolId}' is no longer the one this file stores.\n"
            + $"expected: {JsonNode.Parse(expected)!.ToJsonString()}\n"
            + $"actual:   {actual!.ToJsonString()}");
    }

    private static AIFunction Build(string uses, string toolId)
    {
        MapKnowledgePort store = new();
        BuiltinToolSource source = new(new BuiltinToolPorts(store, store, null));
        ToolConfiguration tool = new() { Id = toolId, Kind = ToolKind.Builtin, Uses = uses };
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [tool],
        });

        var registrations = source.ProvideAsync(context).AsTask().GetAwaiter().GetResult();

        return Assert.IsAssignableFrom<AIFunction>(Assert.Single(registrations).Materialise());
    }
}

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Pointer;
using Json.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// Check 1 of section 8.5: the JSON Schema over the document. It fails on any shape error.
/// </summary>
/// <remarks>
/// The schema ships as an embedded resource of this assembly. Checks 2 to 8 run after this one, on
/// the bound records.
/// </remarks>
public static partial class ConfigurationSchemaValidator
{
    private const string ResourceName = "AgentCore.Application.Configuration.Schema.agentcore-v1.schema.json";

    private static readonly Lazy<string> LazySchemaJson = new(ReadResource);
    private static readonly Lazy<JsonSchema> LazySchema = new(() => JsonSchema.FromText(LazySchemaJson.Value));
    private static readonly Lazy<JsonNode> LazySchemaTree = new(() => JsonNode.Parse(LazySchemaJson.Value)!);

    private static readonly EvaluationOptions Options = new()
    {
        // The list format walks the whole tree once and hands back every node flat, each with its own
        // Details cleared. So Details is the answer, and nothing below it needs walking again.
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// The keywords that only relay a child failure upwards.
    /// </summary>
    /// <remarks>
    /// Each of these says "something under here failed", and the thing that failed reports itself at
    /// its own pointer. Repeating the parent gives the author a stack of sentences that name no
    /// mistake. <c>additionalProperties</c> is not in the list because it has two meanings, and one
    /// of them is the author's mistake — see <see cref="DescribeAdditionalProperties"/>.
    /// </remarks>
    private static readonly HashSet<string> RelayKeywords = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "items", "prefixItems", "contains",
        "allOf", "then", "else", "dependentSchemas", "dependentRequired",
    };

    /// <summary>
    /// The schema keywords whose subschemas are asked a question rather than imposed as a rule.
    /// </summary>
    /// <remarks>
    /// A failure inside one of these is how the keyword works, not a mistake in the document. An
    /// <c>if</c> that does not match selects another branch; a <c>oneOf</c> branch that does not
    /// match is the wrong branch; the subschema of a <c>not</c> is *supposed* to fail. Reporting
    /// them told the author of a <c>writer: tool</c> slot that its writer should have been
    /// <c>counter</c>, which is the opposite of help. The keyword itself still reports.
    /// </remarks>
    private static readonly HashSet<string> SpeculativeKeywords = new(StringComparer.Ordinal)
    {
        "if", "not", "oneOf", "anyOf", "propertyNames",
    };

    /// <summary>Gets the text of the embedded <c>agentcore-v1</c> JSON Schema.</summary>
    public static string SchemaJson => LazySchemaJson.Value;

    /// <summary>Evaluates a document against the schema and returns every shape error.</summary>
    /// <param name="document">The document, already read from YAML or JSON.</param>
    /// <returns>The errors, ordered by their JSON Pointer. An empty list means the document passes.</returns>
    public static IReadOnlyList<ConfigurationError> Evaluate(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var element = JsonDocument.Parse(document.ToJsonString());
        var results = LazySchema.Value.Evaluate(element.RootElement, Options);
        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<ConfigurationError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in results.Details ?? [])
        {
            if (node.IsValid || node.Errors is not { Count: > 0 } || Speculative(node))
            {
                continue;
            }

            var pointer = node.InstanceLocation.ToString();
            foreach (var error in node.Errors)
            {
                if (Describe(node, error.Key, error.Value) is not { } message)
                {
                    continue;
                }

                if (seen.Add(pointer + "\0" + message))
                {
                    errors.Add(new ConfigurationError
                    {
                        Pointer = pointer,
                        Message = message,
                        Check = ConfigurationCheck.DocumentSchema,
                    });
                }
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new ConfigurationError
            {
                Pointer = ConfigurationError.RootPointer,
                Message = "the document does not match the agentcore/v1 schema",
                Check = ConfigurationCheck.DocumentSchema,
            });
        }

        errors.Sort(static (left, right) => string.CompareOrdinal(left.Pointer, right.Pointer));
        return errors;
    }

    /// <summary>Evaluates a document and throws when it holds any shape error.</summary>
    /// <param name="document">The document, already read from YAML or JSON.</param>
    /// <exception cref="ConfigurationLoadException">The document does not match the schema.</exception>
    public static void Validate(JsonNode document)
    {
        var errors = Evaluate(document);
        if (errors.Count > 0)
        {
            throw new ConfigurationLoadException(errors);
        }
    }

    /// <summary>Reports whether a failure came from inside a subschema the keyword only asked about.</summary>
    /// <param name="node">The evaluation node.</param>
    /// <returns><see langword="true"/> when the failure is the keyword working, not a document mistake.</returns>
    private static bool Speculative(EvaluationResults node)
    {
        var path = node.EvaluationPath;
        for (var index = 0; index < path.SegmentCount; index++)
        {
            if (SpeculativeKeywords.Contains(path[index].ToString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes one failure as a sentence about the document, or drops it.</summary>
    /// <param name="node">The evaluation node the failure came from.</param>
    /// <param name="keyword">The schema keyword that failed, or the empty string for a false schema.</param>
    /// <param name="message">The text the library wrote.</param>
    /// <returns>The message the author reads, or <see langword="null"/> to drop the failure.</returns>
    /// <remarks>
    /// The library writes for whoever wrote the schema: "All values fail against the false schema"
    /// is exact and tells the author of a configuration document nothing. Each sentence here names
    /// the rule the document broke. It deliberately does not quote the value that broke it — the
    /// pointer already leads to it, and the value may be a whole block.
    /// </remarks>
    private static string? Describe(EvaluationResults node, string keyword, string message)
    {
        if (RelayKeywords.Contains(keyword))
        {
            return null;
        }

        return keyword switch
        {
            // A `false` schema. The instance pointer ends in the key that is not allowed to be there.
            "" => Name(node) is { Length: > 0 } name
                ? $"the key '{name}' is not allowed here"
                : "the schema allows nothing here",

            "required" => Names(message) is { Count: 1 } one
                ? $"the required property '{one[0]}' is missing"
                : $"the required properties {Quote(Names(message))} are missing",

            "additionalProperties" => DescribeAdditionalProperties(node, message),

            "type" => WrongTypePattern().Match(message) is { Success: true } wrong
                ? $"the value is {wrong.Groups[1].Value}, and this key takes {wrong.Groups[2].Value}"
                : $"the value has the wrong type. {message}",

            "enum" => Accepted(node, "enum") is { Count: > 0 } members
                ? $"this key accepts only: {string.Join(", ", members)}"
                : "the value is not one this key accepts",

            "const" => Accepted(node, "const") is { Count: 1 } only
                ? $"this key is written exactly '{only[0]}', and nothing else"
                : "the value is not the one this key holds",

            "pattern" => Keyword(node, "pattern")?.GetValue<string>() is not { } pattern
                ? "the value is not written in the form this key requires"
                : pattern == @"\S"
                    ? "the text holds no words, and this key needs some"
                    : $"the text is not written in the form this key requires: {pattern}",

            "minLength" or "maxLength" => Bound(message) is { } length
                ? $"the text holds too {length.Way} characters, and this key takes at {length.Limit}"
                : $"the text is the wrong length. {message}",

            "minItems" or "maxItems" => Bound(message) is { } count
                ? $"the list holds too {count.Way} entries, and this key takes at {count.Limit}"
                : $"the list is the wrong length. {message}",

            "uniqueItems" => "the list repeats a value, and every entry here must differ",

            "minimum" or "exclusiveMinimum" or "maximum" or "exclusiveMaximum" or "multipleOf"
                => $"the number is outside the range this key accepts: {message}",

            // A composite the document has to satisfy exactly one way. The schema documents each way.
            "oneOf" => Alternatives(node) is { Count: > 0 } shapes
                ? $"this block matches none of the shapes allowed here: {string.Join("; ", shapes)}"
                : "this block matches none of the shapes allowed here",

            // The rule this states is written on the schema node itself, because no keyword can say it.
            "not" or "dependentRequired" => Documentation(node) ?? message,

            "propertyNames" => $"these key names are not names the schema accepts: {Quote(Names(message))}",

            // Anything the schema grows later still reports, with the keyword named.
            _ => string.Create(CultureInfo.InvariantCulture, $"{message} [{keyword}]"),
        };
    }

    /// <summary>Writes an <c>additionalProperties</c> failure, or drops it.</summary>
    /// <param name="node">The evaluation node.</param>
    /// <param name="message">The text the library wrote.</param>
    /// <returns>The message the author reads, or <see langword="null"/> to drop the failure.</returns>
    /// <remarks>
    /// The keyword carries two different meanings. Against <c>false</c> it is the document's own
    /// mistake — a key the schema does not know — and the parent pointer is where the author should
    /// look. Against a schema, as <c>state:</c> and <c>guards:</c> use it, it only relays that one of
    /// the values under the key failed, and that value reports itself.
    /// </remarks>
    private static string? DescribeAdditionalProperties(EvaluationResults node, string message)
        => Keyword(node, "additionalProperties") is JsonValue value && value.GetValueKind() == JsonValueKind.False
            ? $"the schema does not know these keys here: {Quote(Names(message))}"
            : null;

    /// <summary>Reads the last segment of the instance pointer, unescaped.</summary>
    private static string Name(EvaluationResults node)
    {
        var location = node.InstanceLocation;
        return location.SegmentCount == 0 ? string.Empty : location[location.SegmentCount - 1].ToString();
    }

    /// <summary>Reads the JSON array of names out of a library message.</summary>
    private static List<string> Names(string message)
    {
        var open = message.IndexOf('[', StringComparison.Ordinal);
        var close = message.LastIndexOf(']');
        if (open < 0 || close <= open)
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(message[open..(close + 1)]) is JsonArray array
                ? [.. array.Select(item => item?.GetValue<string>() ?? string.Empty)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Quote(List<string> names)
        => names.Count == 0 ? "(none)" : string.Join(", ", names.Select(name => $"'{name}'"));

    /// <summary>Reads one keyword off the schema node the failure came from.</summary>
    /// <param name="node">The evaluation node.</param>
    /// <param name="keyword">The keyword to read.</param>
    /// <returns>The keyword's value, or <see langword="null"/> when the node cannot be resolved.</returns>
    /// <remarks>
    /// The library reports where in the schema a failure happened, so the schema itself can answer
    /// what the document should have written. A location it cannot resolve — the library indexes
    /// some composite keywords in a form the document does not hold — falls back to a message that
    /// needs no lookup.
    /// </remarks>
    private static JsonNode? Keyword(EvaluationResults node, string keyword)
        => Schema(node) is JsonObject schema && schema.TryGetPropertyValue(keyword, out var value) ? value : null;

    private static JsonObject? Schema(EvaluationResults node)
    {
        var fragment = node.SchemaLocation.Fragment;
        if (fragment.Length == 0 || fragment[0] != '#' || !JsonPointer.TryParse(fragment[1..], out var pointer))
        {
            return null;
        }

        // The library indexes some keywords under a name of its own, so the location it reports may
        // hold a segment the schema document does not. Walking up finds the nearest node that does
        // exist; a walk that lands somewhere unhelpful simply holds no keyword to read, and the
        // caller falls back to a sentence that needs no lookup.
        for (var levels = 0; levels <= pointer.SegmentCount; levels++)
        {
            var candidate = levels == 0 ? pointer : pointer.GetParent(levels);
            if (candidate is { } at && at.TryEvaluate(LazySchemaTree.Value, out var found) && found is JsonObject schema)
            {
                return schema;
            }
        }

        return null;
    }

    /// <summary>Lists the values one key accepts, as the schema wrote them.</summary>
    private static List<string> Accepted(EvaluationResults node, string keyword)
        => Keyword(node, keyword) switch
        {
            JsonArray array => [.. array.Select(Written)],
            { } single => [Written(single)],
            _ => [],
        };

    private static string Written(JsonNode? value)
        => value is JsonValue text && text.GetValueKind() == JsonValueKind.String
            ? text.GetValue<string>()
            : value?.ToJsonString() ?? "null";

    /// <summary>Lists what each branch of a <c>oneOf</c> says about itself.</summary>
    private static List<string> Alternatives(EvaluationResults node)
        => Keyword(node, "oneOf") is JsonArray branches
            ? [.. branches
                .Select(branch => (branch as JsonObject)?["description"]?.GetValue<string>())
                .Where(description => !string.IsNullOrEmpty(description))
                .Select(description => description!)]
            : [];

    /// <summary>Reads what the schema says about the rule this node carries.</summary>
    private static string? Documentation(EvaluationResults node)
        => Keyword(node, "description")?.GetValue<string>() ?? Keyword(node, "$comment")?.GetValue<string>();

    /// <summary>Reads "at least 1 items" or "at most 3 items" out of a library message.</summary>
    /// <param name="message">The text the library wrote.</param>
    /// <returns>Which way the bound runs and what it is, or <see langword="null"/> when the message does not carry one.</returns>
    private static (string Way, string Limit)? Bound(string message)
        => BoundPattern().Match(message) is { Success: true } bound
            ? (bound.Groups[1].Value == "least" ? "few" : "many", $"{bound.Groups[1].Value} {bound.Groups[2].Value}")
            : null;

    private static string ReadResource()
    {
        var assembly = typeof(ConfigurationSchemaValidator).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^Value is ""([^""]+)"" but should be ""([^""]+)""$")]
    private static partial Regex WrongTypePattern();

    [GeneratedRegex(@"at (least|most) (\d+)")]
    private static partial Regex BoundPattern();
}

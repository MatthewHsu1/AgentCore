using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Pointer;
using Json.Schema;

namespace AgentCore.Application.Configuration.Parsing
{
    /// <summary>Writes one schema failure as a sentence about the document.</summary>
    internal static partial class SchemaFailureWording
    {
        private static readonly Lazy<JsonNode> LazySchemaTree = new(() => JsonNode.Parse(ConfigurationSchemaValidator.SchemaJson)!);

        /// <summary>
        /// The keywords that only relay a child failure upwards.
        /// </summary>
        private static readonly HashSet<string> RelayKeywords = new(StringComparer.Ordinal)
        {
            "properties", "patternProperties", "items", "prefixItems", "contains",
            "allOf", "then", "else", "dependentSchemas", "dependentRequired",
        };

        /// <summary>Writes one failure as a sentence about the document, or drops it.</summary>
        /// <param name="node">The evaluation node the failure came from.</param>
        /// <param name="keyword">The schema keyword that failed, or the empty string for a false schema.</param>
        /// <param name="message">The text the library wrote.</param>
        /// <returns>The message the author reads, or <see langword="null"/> to drop the failure.</returns>
        public static string? Describe(EvaluationResults node, string keyword, string message)
        {
            return RelayKeywords.Contains(keyword)
                ? null
                : keyword switch
                {
                    "" => FalseSchema(node),
                    "required" => Required(message),
                    "additionalProperties" => AdditionalProperties(node, message),
                    "type" => WrongType(message),
                    "enum" => Enumerated(node),
                    "const" => Constant(node),
                    "pattern" => Pattern(node),
                    "minLength" or "maxLength" => Length(message, "text", "characters"),
                    "minItems" or "maxItems" => Length(message, "list", "entries"),
                    "uniqueItems" => "the list repeats a value, and every entry here must differ",

                    "minimum" or "exclusiveMinimum" or "maximum" or "exclusiveMaximum" or "multipleOf"
                        => $"the number is outside the range this key accepts: {message}",

                    "oneOf" => OneOf(node),

                    // The rule this states is written on the schema node itself, because no keyword can say it.
                    "not" or "dependentRequired" => Documentation(node) ?? message,

                    "propertyNames" => $"these key names are not names the schema accepts: {Quote(Names(message))}",

                    // Anything the schema grows later still reports, with the keyword named.
                    _ => string.Create(CultureInfo.InvariantCulture, $"{message} [{keyword}]"),
                };
        }

        /// <summary>A <c>false</c> schema. The instance pointer ends in the key that is not allowed to be there.</summary>
        private static string FalseSchema(EvaluationResults node)
        {
            return Name(node) is { Length: > 0 } name
                        ? $"the key '{name}' is not allowed here"
                        : "the schema allows nothing here";
        }

        private static string Required(string message)
        {
            return Names(message) is { Count: 1 } one
                        ? $"the required property '{one[0]}' is missing"
                        : $"the required properties {Quote(Names(message))} are missing";
        }

        /// <summary>Writes an <c>additionalProperties</c> failure, or drops it.</summary>
        /// <param name="node">The evaluation node.</param>
        /// <param name="message">The text the library wrote.</param>
        /// <returns>The message the author reads, or <see langword="null"/> to drop the failure.</returns>
        private static string? AdditionalProperties(EvaluationResults node, string message)
        {
            return Keyword(node, "additionalProperties") is JsonValue value && value.GetValueKind() == JsonValueKind.False
                        ? $"the schema does not know these keys here: {Quote(Names(message))}"
                        : null;
        }

        private static string WrongType(string message)
        {
            return WrongTypePattern().Match(message) is { Success: true } wrong
                        ? $"the value is {wrong.Groups[1].Value}, and this key takes {wrong.Groups[2].Value}"
                        : $"the value has the wrong type. {message}";
        }

        private static string Enumerated(EvaluationResults node)
        {
            return Accepted(node, "enum") is { Count: > 0 } members
                        ? $"this key accepts only: {string.Join(", ", members)}"
                        : "the value is not one this key accepts";
        }

        private static string Constant(EvaluationResults node)
        {
            return Accepted(node, "const") is { Count: 1 } only
                        ? $"this key is written exactly '{only[0]}', and nothing else"
                        : "the value is not the one this key holds";
        }

        private static string Pattern(EvaluationResults node)
        {
            return Keyword(node, "pattern")?.GetValue<string>() switch
            {
                null => "the value is not written in the form this key requires",
                @"\S" => "the text holds no words, and this key needs some",
                var form => $"the text is not written in the form this key requires: {form}",
            };
        }

        /// <summary>Writes a length failure over <paramref name="what"/>, counted in <paramref name="unit"/>.</summary>
        private static string Length(string message, string what, string unit)
        {
            return Bound(message) is { } bound
                        ? $"the {what} holds too {bound.Way} {unit}, and this key takes at {bound.Limit}"
                        : $"the {what} is the wrong length. {message}";
        }

        /// <summary>A composite the document has to satisfy exactly one way. The schema documents each way.</summary>
        private static string OneOf(EvaluationResults node)
        {
            return Alternatives(node) is { Count: > 0 } shapes
                        ? $"this block matches none of the shapes allowed here: {string.Join("; ", shapes)}"
                        : "this block matches none of the shapes allowed here";
        }

        /// <summary>Reads the last segment of the instance pointer, unescaped.</summary>
        private static string Name(EvaluationResults node)
        {
            JsonPointer location = node.InstanceLocation;
            return location.SegmentCount == 0 ? string.Empty : location[location.SegmentCount - 1].ToString();
        }

        /// <summary>Reads the JSON array of names out of a library message.</summary>
        private static List<string> Names(string message)
        {
            int open = message.IndexOf('[', StringComparison.Ordinal);
            int close = message.LastIndexOf(']');
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
        {
            return names.Count == 0 ? "(none)" : string.Join(", ", names.Select(name => $"'{name}'"));
        }

        /// <summary>Reads one keyword off the schema node the failure came from.</summary>
        /// <param name="node">The evaluation node.</param>
        /// <param name="keyword">The keyword to read.</param>
        /// <returns>The keyword's value, or <see langword="null"/> when the node cannot be resolved.</returns>
        private static JsonNode? Keyword(EvaluationResults node, string keyword)
        {
            return Schema(node) is JsonObject schema && schema.TryGetPropertyValue(keyword, out JsonNode? value) ? value : null;
        }

        private static JsonObject? Schema(EvaluationResults node)
        {
            string fragment = node.SchemaLocation.Fragment;
            if (fragment.Length == 0 || fragment[0] != '#' || !JsonPointer.TryParse(fragment[1..], out JsonPointer pointer))
            {
                return null;
            }

            // The library indexes some keywords under a name of its own, so the location it reports may
            // hold a segment the schema document does not. Walking up finds the nearest node that does
            // exist; a walk that lands somewhere unhelpful simply holds no keyword to read, and the
            // caller falls back to a sentence that needs no lookup.
            for (int levels = 0; levels <= pointer.SegmentCount; levels++)
            {
                JsonPointer? candidate = levels == 0 ? pointer : pointer.GetParent(levels);
                if (candidate is { } at && at.TryEvaluate(LazySchemaTree.Value, out JsonNode? found) && found is JsonObject schema)
                {
                    return schema;
                }
            }

            return null;
        }

        /// <summary>Lists the values one key accepts, as the schema wrote them.</summary>
        private static List<string> Accepted(EvaluationResults node, string keyword)
        {
            return Keyword(node, keyword) switch
            {
                JsonArray array => [.. array.Select(Written)],
                { } single => [Written(single)],
                _ => [],
            };
        }

        private static string Written(JsonNode? value)
        {
            return value is JsonValue text && text.GetValueKind() == JsonValueKind.String
                        ? text.GetValue<string>()
                        : value?.ToJsonString() ?? "null";
        }

        /// <summary>Lists what each branch of a <c>oneOf</c> says about itself.</summary>
        private static List<string> Alternatives(EvaluationResults node)
        {
            return Keyword(node, "oneOf") is JsonArray branches
                        ? [.. branches
                    .Select(branch => (branch as JsonObject)?["description"]?.GetValue<string>())
                    .Where(description => !string.IsNullOrEmpty(description))
                    .Select(description => description!)]
                        : [];
        }

        /// <summary>Reads what the schema says about the rule this node carries.</summary>
        private static string? Documentation(EvaluationResults node)
        {
            return Keyword(node, "description")?.GetValue<string>() ?? Keyword(node, "$comment")?.GetValue<string>();
        }

        /// <summary>Reads "at least 1 items" or "at most 3 items" out of a library message.</summary>
        /// <param name="message">The text the library wrote.</param>
        /// <returns>Which way the bound runs and what it is, or <see langword="null"/> when the message does not carry one.</returns>
        private static (string Way, string Limit)? Bound(string message)
        {
            if (BoundPattern().Match(message) is not { Success: true } bound)
            {
                return null;
            }

            string way = bound.Groups[1].Value == "least" ? "few" : "many";
            return (way, $"{bound.Groups[1].Value} {bound.Groups[2].Value}");
        }

        [GeneratedRegex(@"^Value is ""([^""]+)"" but should be ""([^""]+)""$")]
        private static partial Regex WrongTypePattern();

        [GeneratedRegex(@"at (least|most) (\d+)")]
        private static partial Regex BoundPattern();
    }
}

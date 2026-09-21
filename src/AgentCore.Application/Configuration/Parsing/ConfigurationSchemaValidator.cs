using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Pointer;
using Json.Schema;

namespace AgentCore.Application.Configuration.Parsing
{
    /// <summary>
    /// Check 1 of section 8.5: the JSON Schema over the document. It fails on any shape error.
    /// </summary>
    /// <remarks>
    /// The schema ships as an embedded resource of this assembly. Checks 2 to 8 run after this one, on
    /// the bound records.
    /// </remarks>
    public static class ConfigurationSchemaValidator
    {
        private const string ResourceName = "AgentCore.Application.Configuration.Schema.agentcore-v1.schema.json";

        private static readonly Lazy<string> LazySchemaJson = new(ReadResource);

        private static readonly Lazy<JsonSchema> LazySchema = new(() => JsonSchema.FromText(LazySchemaJson.Value));

        private static readonly EvaluationOptions Options = new()
        {
            OutputFormat = OutputFormat.List,
        };

        /// <summary>
        /// The schema keywords whose subschemas are asked a question rather than imposed as a rule.
        /// </summary>
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

            using JsonDocument element = JsonDocument.Parse(document.ToJsonString());
            EvaluationResults results = LazySchema.Value.Evaluate(element.RootElement, Options);
            if (results.IsValid)
            {
                return [];
            }

            List<ConfigurationError> errors = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (EvaluationResults node in results.Details ?? [])
            {
                if (node.IsValid || node.Errors is not { Count: > 0 } || Speculative(node))
                {
                    continue;
                }

                string pointer = node.InstanceLocation.ToString();
                foreach (KeyValuePair<string, string> error in node.Errors)
                {
                    if (SchemaFailureWording.Describe(node, error.Key, error.Value) is not { } message)
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
            IReadOnlyList<ConfigurationError> errors = Evaluate(document);
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
            JsonPointer path = node.EvaluationPath;
            for (int index = 0; index < path.SegmentCount; index++)
            {
                if (SpeculativeKeywords.Contains(path[index].ToString()))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReadResource()
        {
            Assembly assembly = typeof(ConfigurationSchemaValidator).GetTypeInfo().Assembly;

            using Stream stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from the assembly.");

            using StreamReader reader = new(stream);

            return reader.ReadToEnd();
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>The two forms one configuration document may take.</summary>
public enum ConfigurationFormat
{
    /// <summary>YAML, read through YamlDotNet.</summary>
    Yaml,

    /// <summary>JSON, read through <see cref="JsonNode"/>.</summary>
    Json,
}

/// <summary>
/// The whole load pipeline of section 6: YamlDotNet to <see cref="JsonNode"/> to records.
/// </summary>
/// <remarks>
/// The loader runs check 1 of section 8.5 and nothing else. Checks 2 to 8 run on the records it
/// returns. Loading fails fast, and every message carries a JSON Pointer into the document.
/// Rule 17 of section 11 holds here: the same document loads identically as YAML and as JSON.
/// </remarks>
public static class ConfigurationLoader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // A repeated key is a mistake in both formats, and the YAML reader rejects it too.
        AllowDuplicateProperties = false,
    };

    /// <summary>Loads a YAML document.</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The bound configuration.</returns>
    /// <exception cref="ConfigurationLoadException">The document is malformed or fails check 1.</exception>
    public static AgentCoreConfiguration LoadYaml(string yaml) => Load(yaml, ConfigurationFormat.Yaml);

    /// <summary>Loads a JSON document.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The bound configuration.</returns>
    /// <exception cref="ConfigurationLoadException">The document is malformed or fails check 1.</exception>
    public static AgentCoreConfiguration LoadJson(string json) => Load(json, ConfigurationFormat.Json);

    /// <summary>Loads a document in the given format.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="format">The format of the text.</param>
    /// <returns>The bound configuration.</returns>
    /// <exception cref="ConfigurationLoadException">The document is malformed or fails check 1.</exception>
    public static AgentCoreConfiguration Load(string text, ConfigurationFormat format)
        => LoadDocument(ReadDocument(text, format));

    /// <summary>Loads a document from a file. The extension picks the format.</summary>
    /// <param name="path">The path of a <c>.yaml</c>, <c>.yml</c>, or <c>.json</c> file.</param>
    /// <returns>The bound configuration.</returns>
    /// <exception cref="ConfigurationLoadException">The extension is unknown, or the document fails to load.</exception>
    public static AgentCoreConfiguration LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extension = Path.GetExtension(path);
        var format = extension.ToUpperInvariant() switch
        {
            ".YAML" or ".YML" => ConfigurationFormat.Yaml,
            ".JSON" => ConfigurationFormat.Json,
            _ => throw Syntax($"the file extension '{extension}' is unknown. Use .yaml, .yml, or .json."),
        };

        return Load(File.ReadAllText(path), format);
    }

    /// <summary>Runs check 1 over a node tree, then binds it.</summary>
    /// <param name="document">The document as a node tree.</param>
    /// <returns>The bound configuration.</returns>
    /// <exception cref="ConfigurationLoadException">The document fails check 1.</exception>
    public static AgentCoreConfiguration LoadDocument(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ConfigurationSchemaValidator.Validate(document);
        return ConfigurationBinder.Bind(document);
    }

    /// <summary>Reads a document into a node tree, and binds nothing.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="format">The format of the text.</param>
    /// <returns>The node tree. Both formats produce the same tree for the same document.</returns>
    /// <exception cref="ConfigurationLoadException">The text is malformed.</exception>
    public static JsonNode ReadDocument(string text, ConfigurationFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);

        var node = format switch
        {
            ConfigurationFormat.Yaml => YamlToJson.Parse(text),
            ConfigurationFormat.Json => ReadJson(text),
            _ => throw Syntax($"the format '{format}' is unknown"),
        };

        // Both paths end in the same node backing, so two trees of the same document compare equal.
        // JsonOptions travels with this reparse too: ReadJson already enforces it on the JSON path,
        // and YamlToJson.ConvertMapping already rejects a repeated key on the YAML path (a JsonObject
        // cannot hold one twice to begin with), so this is belt-and-braces rather than a live gap.
        return JsonNode.Parse(node.ToJsonString(), documentOptions: JsonOptions)
               ?? throw Syntax("the document is empty");
    }

    private static JsonNode ReadJson(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: JsonOptions)
                   ?? throw Syntax("the document is empty");
        }
        catch (JsonException exception)
        {
            throw Syntax($"the document is not well-formed JSON: {exception.Message}", exception);
        }
    }

    private static ConfigurationLoadException Syntax(string message, Exception? cause = null)
        => new(
            new ConfigurationError
            {
                Pointer = ConfigurationError.RootPointer,
                Message = message,
                Check = ConfigurationCheck.Syntax,
            },
            cause);
}

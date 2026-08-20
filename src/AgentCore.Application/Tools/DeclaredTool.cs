using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// The base every tool the factory builds shares.
/// </summary>
public abstract class DeclaredTool : AIFunction
{
    /// <summary>The schema a tool advertises when the document declares no <c>parameters:</c>.</summary>
    private const string NoArgumentsSchema = """{"type":"object","properties":{}}""";

    private readonly JsonElement _schema;

    /// <summary>Creates the tool.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    protected DeclaredTool(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        Declaration = tool;
        _schema = ParseSchema(tool.Parameters?.ToJsonString() ?? NoArgumentsSchema);
    }

    /// <summary>Gets the declaration the document holds.</summary>
    protected ToolConfiguration Declaration { get; }

    /// <summary>Gets the tool id the document declares. It is the name the model calls.</summary>
    public override string Name => Declaration.Id;

    /// <summary>Gets the description the model reads.</summary>
    public override string Description => Declaration.Description ?? string.Empty;

    /// <summary>Gets the raw JSON Schema of the arguments, unchanged.</summary>
    public override JsonElement JsonSchema => _schema;

    /// <summary>The <see cref="AIFunction"/> entry point. Delegates straight to <see cref="CallAsync"/>.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Exactly what <see cref="CallAsync"/> returned.</returns>
    protected sealed override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return await CallAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the tool.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result the model reads.</returns>
    protected abstract ValueTask<object?> CallAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <summary>Builds the error result of this tool.</summary>
    /// <param name="message">What went wrong. It never holds a secret value.</param>
    /// <returns>The result the model reads.</returns>
    protected JsonObject Failed(string message) => ToolErrorResult.Create(Declaration.Id, message);

    /// <summary>Reads one argument as text.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="name">The argument name.</param>
    /// <returns>The text, or <see langword="null"/> when the model filled nothing.</returns>
    protected static string? ArgumentText(AIFunctionArguments arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(name);

        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            JsonValue node when node.TryGetValue<string>(out var text) => text,
            JsonNode node => node.ToJsonString(),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>Copies every argument into one JSON object.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <returns>The object a host delegate reads.</returns>
    protected static JsonObject ArgumentsAsJson(AIFunctionArguments arguments)
        => ToolArgumentJson.ToJsonObject(arguments);

    /// <summary>Reads one JSON Schema into the element the framework advertises.</summary>
    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}

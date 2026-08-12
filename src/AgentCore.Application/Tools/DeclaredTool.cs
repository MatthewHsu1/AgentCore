using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// The base every tool the factory builds shares.
/// </summary>
/// <remarks>
/// <para>
/// It carries the two rules that hold for each kind. The first: the model reads the raw
/// <see cref="ToolConfiguration.Parameters"/> JSON Schema, exactly as the document wrote it and with
/// nothing generated over it. The second, from section 8.7: a tool returns an error result and does
/// not throw, so <see cref="CallAsync"/> may fail any way it likes and the caller still reads a
/// result.
/// </para>
/// <para>
/// One exception passes through: a cancellation the caller asked for. A cancelled turn is not a tool
/// failure, nobody reads the result, and swallowing it would keep a dead call running.
/// </para>
/// </remarks>
public abstract class DeclaredTool : AIFunction
{
    /// <summary>The schema a tool advertises when neither the document nor the tool declares one.</summary>
    private const string NoArgumentsSchema = """{"type":"object","properties":{}}""";

    private readonly JsonElement _schema;

    /// <summary>Creates the tool.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <param name="defaultSchema">
    /// The JSON Schema to advertise when the document declares no <c>parameters:</c>. A declared
    /// schema always wins over it.
    /// </param>
    protected DeclaredTool(ToolConfiguration tool, string? defaultSchema = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        Declaration = tool;
        _schema = ParseSchema(tool.Parameters?.ToJsonString() ?? defaultSchema ?? NoArgumentsSchema);
    }

    /// <summary>Gets the declaration the document holds.</summary>
    protected ToolConfiguration Declaration { get; }

    /// <summary>Gets the tool id the document declares. It is the name the model calls.</summary>
    public override string Name => Declaration.Id;

    /// <summary>Gets the description the model reads.</summary>
    public override string Description => Declaration.Description ?? string.Empty;

    /// <summary>Gets the raw JSON Schema of the arguments, unchanged.</summary>
    public override JsonElement JsonSchema => _schema;

    /// <summary>Runs the tool, and turns any failure into a result the model reads.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result, or the error result.</returns>
    protected sealed override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return await CallAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller hung up. Nobody reads this result, so it stays a cancellation.
            throw;
        }
        catch (Exception failure)
        {
            return Failed(failure.GetType().Name + ": " + failure.Message);
        }
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

    /// <summary>Reads one argument as a whole number.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="name">The argument name.</param>
    /// <param name="fallback">The value to take when the model filled nothing readable.</param>
    /// <returns>The number.</returns>
    protected static int ArgumentInteger(AIFunctionArguments arguments, string name, int fallback)
        => int.TryParse(ArgumentText(arguments, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    /// <summary>Copies every argument into one JSON object.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <returns>The object a host delegate reads.</returns>
    protected static JsonObject ArgumentsAsJson(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        JsonObject payload = [];
        foreach (var argument in arguments)
        {
            payload[argument.Key] = ToNode(argument.Value);
        }

        return payload;
    }

    /// <summary>Carries one tool argument into a JSON node.</summary>
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>Reads one JSON Schema into the element the framework advertises.</summary>
    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}

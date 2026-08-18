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
/// It carries one rule that holds for each kind: the model reads the raw
/// <see cref="ToolConfiguration.Parameters"/> JSON Schema, exactly as the document wrote it and with
/// nothing generated over it.
/// </para>
/// <para>
/// <b>Task 7a moved the error policy off this base and into
/// <see cref="AgentCore.Application.Runtime.AuditingFunctionInvokingChatClient.InvokeFunctionAsync"/>
/// </b>, the framework's single choke point for every tool call, and not just the ones that happen to
/// inherit from here. <see cref="InvokeCoreAsync"/> therefore no longer catches anything: it runs
/// <see cref="CallAsync"/> and returns or throws exactly what that returned or threw, so a tool kind
/// still MAY answer a fault itself by returning <see cref="Failed"/> directly — <c>HttpTool</c> does,
/// for the status code it already read — but a thrown fault is no longer split into "answerable"
/// and "beyond the model" here. That split, and the reasoning behind each exception type in it, now
/// lives with the middleware, so a plain <c>AIFunctionFactory.Create(...)</c> tool that is not a
/// <see cref="DeclaredTool"/> at all gets the identical treatment.
/// </para>
/// </remarks>
public abstract class DeclaredTool : AIFunction
{
    /// <summary>The schema a tool advertises when the document declares no <c>parameters:</c>.</summary>
    private const string NoArgumentsSchema = """{"type":"object","properties":{}}""";

    private readonly JsonElement _schema;

    /// <summary>Creates the tool.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <remarks>
    /// This used to take a <c>defaultSchema</c> the tool kind supplied for a document that declared
    /// no <c>parameters:</c>. Only the four built-ins ever passed one, and Task 7b moved them to
    /// <c>AIFunctionFactory</c>, which generates a schema from the C# instead. What is left is the
    /// rule in the remarks on this class, with nothing generated over it and nothing behind it.
    /// </remarks>
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
    /// <remarks>
    /// It no longer catches anything. See the remarks on this class: the error policy that used to
    /// live here moved to <c>AuditingFunctionInvokingChatClient.InvokeFunctionAsync</c>, so what this
    /// tool's body throws reaches that middleware unfiltered, with its original stack.
    /// </remarks>
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

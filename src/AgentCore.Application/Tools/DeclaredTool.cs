using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
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
/// nothing generated over it. The second, from section 8.7: a tool returns an error result rather than
/// throwing, so <see cref="CallAsync"/> may fail and the model still reads an answer and decides what
/// to say next.
/// </para>
/// <para>
/// <b>The second rule holds for the failure it was written about, and not for every failure.</b> It is
/// the design five agent frameworks, Microsoft's own guidance, and the MCP specification all converged
/// on — MCP makes it normative, separating a protocol error from an <c>isError: true</c> tool-execution
/// error — and the reason is that a fault the model can answer must stay in front of the model so the
/// loop recovers. A fault the model CANNOT answer is the opposite case. A dead socket, a rejected
/// credential, and an endpoint that never replies are not facts the next set of arguments will fix, and
/// turning them into a result the model reads told it to try again against a dependency that is gone.
/// </para>
/// <para>
/// So the two are split here, once, at the base every kind shares, by
/// <see cref="IsBeyondTheModel"/>. A fault the model may answer becomes <see cref="Failed"/>, exactly
/// as before. A fault beyond it is left to propagate, which is what lets
/// <c>FunctionInvokingChatClient.MaximumConsecutiveErrorsPerRequest</c> — three, so the fourth
/// consecutive erroring round throws — end the turn on the fallback line, per section 8.7 row six.
/// Before this split that budget could never fire for an infrastructure fault, because every fault
/// looked like an answer.
/// </para>
/// <para>
/// One exception still passes through untouched: a cancellation the caller asked for. A cancelled turn
/// is not a tool failure, nobody reads the result, and swallowing it would keep a dead call running.
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

    /// <summary>Runs the tool, and turns a failure the model can answer into a result it reads.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result, or the error result.</returns>
    /// <remarks>
    /// Both catches are exception FILTERS, so a fault that belongs to neither is never caught at all:
    /// it keeps the stack it was thrown with, and the framework rethrows that same instance through
    /// <c>ExceptionDispatchInfo</c> when the budget runs out. A <c>catch</c> that rethrew would give
    /// the log a stack that starts here instead of at the socket.
    /// </remarks>
    protected sealed override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return await CallAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken)
                                        && !IsBeyondTheModel(failure))
        {
            return Failed(failure.GetType().Name + ": " + failure.Message);
        }
    }

    /// <summary>Reports whether a fault is one the model cannot possibly answer.</summary>
    /// <param name="failure">What the tool body threw. It is never the caller's own cancellation.</param>
    /// <returns>
    /// <see langword="true"/> to let the fault propagate, so the framework's consecutive-error budget
    /// counts it and eventually ends the turn on the fallback line.
    /// <see langword="false"/> to answer the model with <see cref="Failed"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The exception type is the only signal available here, because the body of a tool is arbitrary
    /// and nothing else about it is known. The set below is therefore deliberately SMALL and each
    /// member earns its place by naming a dependency that is not there — not a request the dependency
    /// refused, which is a fact the model reads and works around.
    /// </para>
    /// <para>
    /// Everything unlisted answers the model. That default is chosen and not accidental: an unknown
    /// fault from an arbitrary tool body is far more often a bad argument than a dead dependency, and
    /// the two mistakes do not cost the same. Guessing wrong here wastes one round of the model's
    /// budget. Guessing wrong the other way ends the caller's turn on the fallback line for something
    /// the model would have fixed by itself.
    /// </para>
    /// <para>
    /// It is <see langword="virtual"/> so a tool kind can refine it. A vendor SDK that spells a
    /// transport fault in its own exception type is the case: the SDK type is not visible from this
    /// assembly, so the kind that owns it overrides this rather than wrapping every call in a second
    /// catch block. D15 makes that a permanent obligation, which is the price of having the seam at
    /// all, and the alternative — every tool body classifying for itself — is the coupling this base
    /// exists to remove.
    /// </para>
    /// </remarks>
    protected virtual bool IsBeyondTheModel(Exception failure) => failure switch
    {
        // A path the model named that is not there IS answerable: it picks another one. These two
        // come first, because both derive from IOException and the arm below would otherwise swallow
        // them. A knowledge tool that reads a document the model chose lands here.
        FileNotFoundException or DirectoryNotFoundException => false,

        // The host is not resolvable, the connection was refused, or it dropped mid-body. HttpTool
        // answers a status code with Failed() itself, so this type only ever reaches here as
        // transport. No set of arguments reaches a host that is not answering.
        HttpRequestException or SocketException => true,

        // A pipe, a socket, or a file handle that faulted below the tool. The two "not there" cases
        // were already taken above, so what is left is the medium and not the name.
        IOException => true,

        // Nothing answered inside the deadline. A second attempt with different arguments waits the
        // same amount of time and the caller is on the telephone.
        TimeoutException => true,

        // The credential was refused, or the process may not read what it was told to read. Neither
        // is a fact the model holds, and retrying a rejected token only rate-limits us.
        UnauthorizedAccessException or AuthenticationException => true,

        // The caller's own cancellation never reaches here — InvokeCoreAsync tests the token first —
        // so what is left is somebody else's deadline. HttpClient reports its own that way: a
        // TaskCanceledException wrapping a TimeoutException, on a token nobody cancelled.
        OperationCanceledException => true,

        _ => false,
    };

    /// <summary>Reports whether a fault is the caller hanging up rather than the tool failing.</summary>
    /// <remarks>
    /// The TOKEN decides this and never the type, because the two are spelled the same. A caller who
    /// hung up and an endpoint that ran out of time both arrive as an
    /// <see cref="OperationCanceledException"/>, and only the token says which happened.
    /// </remarks>
    private static bool IsCallerCancellation(Exception failure, CancellationToken cancellationToken)
        => failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

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

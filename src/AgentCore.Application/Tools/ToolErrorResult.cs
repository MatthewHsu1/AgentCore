using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools;

/// <summary>
/// The one shape a failing tool returns.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.7: a tool returns an error result and does not throw. A 500 answer, a timeout, a file
/// that is not there, and a host delegate that throws all land here, because the model reads the
/// result and decides what to say next. An exception would end the turn instead, and the caller is
/// on the telephone.
/// </para>
/// <para>
/// The object holds three fields: <c>error</c> is always <see langword="true"/>, <c>tool</c> names
/// the declared tool id, and <c>message</c> says what went wrong in one sentence.
/// </para>
/// </remarks>
public static class ToolErrorResult
{
    /// <summary>The property that marks a result as a failure.</summary>
    public const string ErrorProperty = "error";

    /// <summary>The property that names the declared tool.</summary>
    public const string ToolProperty = "tool";

    /// <summary>The property that says what went wrong.</summary>
    public const string MessageProperty = "message";

    /// <summary>Builds the result a failing tool returns.</summary>
    /// <param name="tool">The declared tool id.</param>
    /// <param name="message">What went wrong. It never holds a secret value.</param>
    /// <returns>The result the model reads.</returns>
    public static JsonObject Create(string tool, string message)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(message);

        return new JsonObject
        {
            [ErrorProperty] = true,
            [ToolProperty] = tool,
            [MessageProperty] = message,
        };
    }

    /// <summary>Reports whether one tool result is a failure.</summary>
    /// <param name="result">The result the tool returned.</param>
    /// <returns><see langword="true"/> when the result carries the error mark.</returns>
    public static bool IsError(JsonNode? result)
        => result is JsonObject map
           && map.TryGetPropertyValue(ErrorProperty, out var mark)
           && mark is JsonValue value
           && value.TryGetValue<bool>(out var failed)
           && failed;
}

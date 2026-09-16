using System.Text.Json.Nodes;

namespace AgentCore.Application.Scripting;

/// <summary>What one script run produced.</summary>
public sealed record ScriptResult
{
    /// <summary>The value the script returned or emitted, as JSON, or <see langword="null"/> when it produced none.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>Why the script produced nothing, in words the model can act on, or <see langword="null"/> when it ran.</summary>
    public string? Error { get; init; }

    /// <summary>A run that finished.</summary>
    /// <param name="value">What it produced, or <see langword="null"/> when it returned nothing.</param>
    /// <returns>The result.</returns>
    public static ScriptResult Returned(JsonNode? value) => new() { Value = value };

    /// <summary>A run that did not finish.</summary>
    /// <param name="error">Why, for the model.</param>
    /// <returns>The result.</returns>
    public static ScriptResult Failed(string error) => new() { Error = error };
}

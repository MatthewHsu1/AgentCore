using System.Text.Json.Nodes;

namespace AgentCore.Application.Scripting;

/// <summary>One script to run.</summary>
/// <param name="Code">
/// What the model wrote. It is read as the body of a function with <paramref name="Data"/> in scope
/// as <c>data</c>, ending in <c>return</c>; a bare function expression is called with <c>data</c>
/// instead. Markdown fences around it are ignored.
/// </param>
/// <param name="Data">What the script sees as <c>data</c>.</param>
public sealed record ScriptRequest(string Code, JsonNode Data)
{
    /// <summary>
    /// The name of a function the script may call with its result instead of returning it, or
    /// <see langword="null"/> when only <c>return</c> counts. A model that was told to call a tool
    /// named <c>present</c> will call <c>present(tree)</c> from inside its code about as often as it
    /// returns the tree.
    /// </summary>
    public string? Emit { get; init; }
}

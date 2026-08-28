using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// Pairs each tool result with the name of the tool that was called.
/// </summary>
/// <remarks>
/// A <see cref="FunctionResultContent"/> carries only the id of the call it answers, so anything
/// that reads results in order has to remember the name off the <see cref="FunctionCallContent"/>
/// that came before it. One instance covers one run of contents and is not thread-safe.
/// </remarks>
public sealed class ToolCallNames
{
    private readonly Dictionary<string, string> _byCallId = new(StringComparer.Ordinal);

    /// <summary>Remembers the tool one call names.</summary>
    /// <param name="call">The call half.</param>
    public void Called(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);

        _byCallId[call.CallId] = call.Name;
    }

    /// <summary>Reads the tool one result answers for.</summary>
    /// <param name="result">The result half.</param>
    /// <returns>The tool name, or <see langword="null"/> when no call before it named one.</returns>
    public string? Of(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return _byCallId.TryGetValue(result.CallId, out var name) ? name : null;
    }
}

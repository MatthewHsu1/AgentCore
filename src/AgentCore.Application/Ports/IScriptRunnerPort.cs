using AgentCore.Application.Scripting;

namespace AgentCore.Application.Ports;

/// <summary>
/// Runs a script a model wrote, over data the host supplies, and hands back what it produced.
/// </summary>
public interface IScriptRunnerPort
{
    /// <summary>Runs one script.</summary>
    /// <param name="request">The code, the data it sees as <c>data</c>, and how it may hand its result back.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the script produced, or why it produced nothing.</returns>
    ValueTask<ScriptResult> RunAsync(ScriptRequest request, CancellationToken cancellationToken = default);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Scripting;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>
/// A script runner for tests that cannot run JavaScript. The "code" is the JSON the script would
/// have returned, or <c>throw: message</c> for a script that fails.
/// </summary>
internal sealed class FakeScriptRunner : IScriptRunnerPort
{
    private const string ThrowPrefix = "throw:";

    /// <summary>Gets every request this runner was handed, in call order.</summary>
    public List<ScriptRequest> Requests { get; } = [];

    public ValueTask<ScriptResult> RunAsync(ScriptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);

        if (request.Code.StartsWith(ThrowPrefix, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ScriptResult.Failed(request.Code[ThrowPrefix.Length..].Trim()));
        }

        try
        {
            return ValueTask.FromResult(ScriptResult.Returned(JsonNode.Parse(request.Code)));
        }
        catch (JsonException failure)
        {
            return ValueTask.FromResult(ScriptResult.Failed(failure.Message));
        }
    }
}

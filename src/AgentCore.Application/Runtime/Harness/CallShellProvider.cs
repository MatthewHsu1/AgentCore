using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// The context provider behind an agent's <c>shell:</c> block: on every invocation it hands the
/// model this call's shell executor as MAF's own tool. No instructions of our own — MAF's tool
/// description is what the model reads — and no <c>StateKeys</c>, because the shell keeps no
/// session state.
/// </summary>
internal sealed class CallShellProvider : AIContextProvider
{
    private const string NoTurnMessage =
        "A shell: tool runs only while a turn runs through a CallSession with a workspace root bound.";

    private readonly CallShellOptions _options;

    public CallShellProvider(CallShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var shells = TurnAmbients.Current?.Shells ?? throw new InvalidOperationException(NoTurnMessage);

        return new ValueTask<AIContext>(new AIContext
        {
            Tools = [shells.Get(_options).AsAIFunction(requireApproval: false)],
        });
    }
}

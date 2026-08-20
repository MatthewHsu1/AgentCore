using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Stateless;
using Stateless.Graph;

namespace AgentCore.Application.Policy;

/// <summary>
/// The stage machine of row 2 of the compile table, over string stages.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.2 measured this against MAF Workflows: the same six stages cost 55 lines under
/// <c>Stateless</c> and 115 to 142 under Workflows, and both ways to write a wrong transition table
/// fail silently in the graph. Configuration-authored guards raise the mistake rate, so the runtime
/// that throws is the correct one.
/// </para>
/// <para>
/// Zero guards true is the normal case: the stage stays where it is, which is what
/// <c>OnUnhandledTrigger</c> does. A stage that sets <see cref="StageNoMatch.Error"/> rejects that
/// instead. Two guards true at one point throws, and check 5 of section 8.5 catches it at startup.
/// </para>
/// <para>
/// One machine belongs to one call. The compiled agent is a process singleton, so the runtime builds
/// one machine for each call and shares no machine between calls.
/// </para>
/// </remarks>
public sealed class StagePolicy
{
    /// <summary>The one trigger. A turn ends, and the machine picks the next stage.</summary>
    public const string TurnEndedTrigger = "TurnEnded";

    private readonly PolicyConfiguration _policy;
    private readonly Dictionary<string, StageConfiguration> _stages;
    private readonly StateMachine<string, string> _machine;
    private readonly StateMachine<string, string>.TriggerWithParameters<IReadOnlyDictionary<string, JsonNode?>> _turnEnded;

    /// <summary>Builds the machine one configuration declares.</summary>
    /// <param name="policy">The <c>policy:</c> section.</param>
    /// <param name="guards">The evaluator that runs each exit guard.</param>
    /// <exception cref="ArgumentException">The initial stage is not declared.</exception>
    public StagePolicy(PolicyConfiguration policy, IGuardEvaluator guards)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(guards);

        _policy = policy;
        _stages = new Dictionary<string, StageConfiguration>(StringComparer.Ordinal);
        foreach (var stage in policy.Stages)
        {
            _stages[stage.Id] = stage;
        }

        if (!_stages.ContainsKey(policy.Initial))
        {
            throw new ArgumentException(
                $"The initial stage '{policy.Initial}' is not declared in policy.stages.", nameof(policy));
        }

        _machine = new StateMachine<string, string>(policy.Initial);
        _turnEnded = _machine.SetTriggerParameters<IReadOnlyDictionary<string, JsonNode?>>(TurnEndedTrigger);

        // No guard matched means the caller has not given us enough to move on. Staying is correct,
        // unless the stage asked for the opposite with onNoMatch: error.
        _machine.OnUnhandledTrigger((stage, _) =>
        {
            if (_stages.TryGetValue(stage, out var declared) && declared.OnNoMatch == StageNoMatch.Error)
            {
                throw new InvalidOperationException(
                    $"No exit guard of the stage '{stage}' is true, and the stage sets onNoMatch: error.");
            }
        });

        foreach (var stage in policy.Stages)
        {
            Configure(stage, guards);
        }
    }

    /// <summary>Gets the <c>policy:</c> section this machine was built from.</summary>
    public PolicyConfiguration Configuration => _policy;

    /// <summary>Gets the id of the stage the machine holds.</summary>
    public string Stage => _machine.State;

    /// <summary>Gets the stage the machine holds.</summary>
    public StageConfiguration CurrentStage => _stages[_machine.State];

    /// <summary>Gets whether the stage the machine holds ends the call.</summary>
    public bool IsTerminal => CurrentStage.Terminal;

    /// <summary>Gets the id of the agent that speaks in the stage the machine holds.</summary>
    public string? CurrentAgentId => CurrentStage.Agent;

    /// <summary>Ends a turn, and picks the stage of the next turn.</summary>
    /// <param name="state">The state snapshot the guards read.</param>
    /// <returns>The stage the machine now holds.</returns>
    /// <exception cref="InvalidOperationException">
    /// Two exit guards are true at once, or no guard is true and the stage sets
    /// <see cref="StageNoMatch.Error"/>.
    /// </exception>
    public string Advance(IReadOnlyDictionary<string, JsonNode?> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _machine.Fire(_turnEnded, state);
        return _machine.State;
    }

    /// <summary>Renders the live machine as a mermaid diagram. There is no second source of truth.</summary>
    /// <returns>The mermaid text, with each guard name printed next to its edge.</returns>
    public string ToMermaid() => MermaidGraph.Format(_machine.GetInfo());

    /// <summary>Renders the live machine as a DOT diagram.</summary>
    /// <returns>The DOT text.</returns>
    public string ToDot() => UmlDotGraph.Format(_machine.GetInfo());

    private void Configure(StageConfiguration stage, IGuardEvaluator guards)
    {
        var configured = _machine.Configure(stage.Id);

        // One unconditional exit needs no guard at all, and the diagram reads better without one.
        if (stage.To.Count == 1 && stage.To[0].When is null)
        {
            configured.Permit(TurnEndedTrigger, stage.To[0].Stage);
            return;
        }

        foreach (var exit in stage.To)
        {
            if (exit.When is { } guard)
            {
                configured.PermitIf(
                    _turnEnded,
                    exit.Stage,
                    state => guards.Evaluate(guard, state),
                    guard.ToString());
            }
            else
            {
                configured.PermitIf(_turnEnded, exit.Stage, static _ => true, "always");
            }
        }
    }
}

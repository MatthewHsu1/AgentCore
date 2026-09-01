using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Stateless;
using Stateless.Graph;

namespace AgentCore.Application.Policy;

/// <summary>
/// The stage machine of row 2 of the compile table, over string stages.
/// </summary>
public sealed class StagePolicy
{
    /// <summary>The one trigger. A turn ends, and the machine picks the next stage.</summary>
    public const string TurnEndedTrigger = "TurnEnded";

    private readonly PolicyConfiguration _policy;

    private readonly Dictionary<string, StageConfiguration> _stages;

    private readonly StateMachine<string, string> _machine;

    private readonly StateMachine<string, string>.TriggerWithParameters<IReadOnlyDictionary<string, JsonNode?>> _turnEnded;

    private string _stage;

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

        _stage = policy.Initial;

        _machine = new StateMachine<string, string>(() => _stage, stage => _stage = stage);

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

    /// <summary>Reports whether this policy declares a stage.</summary>
    public bool Declares(string stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return _stages.ContainsKey(stage);
    }

    /// <summary>Puts the machine back in the stage a previous session of this call left it in.</summary>
    internal void RestoreStage(string stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        if (!_stages.ContainsKey(stage))
        {
            throw new ArgumentException(
                $"The stage '{stage}' is not declared in policy.stages.", nameof(stage));
        }

        _stage = stage;
    }

    /// <summary>Ends a turn, and picks the stage of the next turn.</summary>
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

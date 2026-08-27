using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// The map from a name to the <see cref="IEvaluator"/> behind it.
/// </summary>
/// <remarks>
/// <para>
/// Decision D13 makes <see cref="IEvaluator"/> the evaluation port and keeps evaluation inside the
/// library. A quality evaluator, an NLP evaluator, the custom evaluators in this folder, and the
/// moderation adapter in <c>Infrastructure/Evaluation/OpenAiModeration/</c> all arrive here under one
/// name, so the composition root reads the same as it does for a tool or a binding.
/// </para>
/// <para>
/// This registry takes the shape <see cref="Tools.Binding.ToolBindingRegistry"/> takes, because D13 asks the
/// registries to be one shape. A name is compared with <see cref="StringComparer.Ordinal"/>.
/// Registration happens at startup and reading happens later, so the registry is safe to read from
/// many turns at once once the host stops writing to it.
/// </para>
/// <para>
/// The registry holds evaluators. It never runs one. Triage row T18 defers online sampled
/// evaluation, and D9 says a judge must never block a turn, so nothing here reaches a turn loop.
/// </para>
/// </remarks>
public sealed class EvaluatorRegistry
{
    private readonly Dictionary<string, IEvaluator> _evaluators = new(StringComparer.Ordinal);

    /// <summary>Gets the number of names the host registered.</summary>
    public int Count => _evaluators.Count;

    /// <summary>Gets the names the host registered.</summary>
    public IReadOnlyCollection<string> Names => _evaluators.Keys;

    /// <summary>Registers one evaluator.</summary>
    /// <param name="name">The name the host reads the evaluator back by.</param>
    /// <param name="evaluator">The evaluator.</param>
    /// <returns>This registry, so a host chains its registrations.</returns>
    /// <exception cref="ArgumentException">The name is already registered.</exception>
    public EvaluatorRegistry Register(string name, IEvaluator evaluator)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(evaluator);

        if (!_evaluators.TryAdd(name, evaluator))
        {
            throw new ArgumentException($"the evaluator '{name}' is already registered.", nameof(name));
        }

        return this;
    }

    /// <summary>Reports whether one name is registered.</summary>
    /// <param name="name">The name the host registered.</param>
    /// <returns><see langword="true"/> when the host registered the name.</returns>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _evaluators.ContainsKey(name);
    }

    /// <summary>Reads the evaluator one name points at.</summary>
    /// <param name="name">The name the host registered.</param>
    /// <param name="evaluator">The evaluator, when the name is registered.</param>
    /// <returns><see langword="true"/> when the host registered the name.</returns>
    public bool TryGetEvaluator(string name, out IEvaluator? evaluator)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _evaluators.TryGetValue(name, out evaluator);
    }
}

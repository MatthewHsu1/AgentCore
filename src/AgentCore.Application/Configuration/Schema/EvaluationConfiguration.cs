namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The <c>evaluation:</c> section. It sets the share of finished turns a judge scores.
/// </summary>
/// <remarks>
/// Triage row T18 defers online sampled evaluation and says the sample rate comes from
/// configuration, so this section holds it. Decision D9 keeps the judge off the turn: the rate
/// selects a finished turn and never delays one. The section is optional, and a document that omits
/// it takes <see cref="DefaultSampleRate"/>.
/// </remarks>
public sealed record EvaluationConfiguration
{
    /// <summary>The sample rate used when the document sets none. It evaluates no turn.</summary>
    /// <remarks>
    /// T18 defers the online path until the offline gate proves the evaluators, so the seam is
    /// reachable and costs nothing until a document raises the rate.
    /// </remarks>
    public const double DefaultSampleRate = 0;

    /// <summary>Gets the share of finished turns to evaluate, from 0 through 1.</summary>
    public double SampleRate { get; init; } = DefaultSampleRate;

    /// <summary>Gets the model that scores a turn, or <see langword="null"/> when the document names none.</summary>
    /// <remarks>
    /// <para>
    /// One key serves both evaluation paths. The online path of T18 scores the sampled turn with this
    /// model, and the offline golden set of D13 scores a recorded row with it, so a deployment tunes
    /// one entry and both paths follow. D9 still holds: the judge reads a turn that already ended and
    /// never delays one.
    /// </para>
    /// <para>
    /// The key is optional. A document that names no judge still runs every evaluator that calls no
    /// model, and an evaluator that needs a judge returns a metric with no value instead of a score.
    /// A judge reads with <c>temperature: 0</c> in practice, because a score gate cannot use a number
    /// that moves for the same input.
    /// </para>
    /// </remarks>
    public ModelReference? Judge { get; init; }
}

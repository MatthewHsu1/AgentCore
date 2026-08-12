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
}

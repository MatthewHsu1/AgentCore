namespace AgentCore.Application.Evaluation;

/// <summary>
/// The decision to evaluate one finished turn, or to skip it.
/// </summary>
/// <remarks>
/// <para>
/// Triage row T18 defers online sampled evaluation and says the sample rate comes from
/// configuration. This type answers one question and does nothing else: it never calls an evaluator,
/// never starts a task, and never touches a turn. Decision D9 says a judge must never block a turn,
/// so the sampler stays a decision and the caller owns what happens after it.
/// </para>
/// <para>
/// The rate comes from <c>evaluation.sampleRate</c>, which T18 asks for and D15 permits as an
/// optional key inside <c>agentcore/v1</c>. The composition root reads the document and passes the
/// rate here. A document that sets no rate takes
/// <see cref="Configuration.Schema.EvaluationConfiguration.DefaultSampleRate"/>, which evaluates no
/// turn.
/// </para>
/// <para>
/// A rate of 0 answers <see langword="false"/> and a rate of 1 answers <see langword="true"/> without
/// drawing a number, so the two ends are deterministic with no test seam at all. Every rate between
/// them draws once, and the test passes its own draw.
/// </para>
/// </remarks>
public sealed class EvaluationSampler
{
    private readonly Func<double> _draw;

    /// <summary>Builds a sampler that draws from <see cref="Random.Shared"/>.</summary>
    /// <param name="rate">The share of turns to evaluate, from 0 through 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not a number from 0 through 1.</exception>
    public EvaluationSampler(double rate)
        : this(rate, Random.Shared.NextDouble)
    {
    }

    /// <summary>Builds a sampler that draws from the supplied source.</summary>
    /// <param name="rate">The share of turns to evaluate, from 0 through 1.</param>
    /// <param name="draw">The source of one number in the range 0 through 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not a number from 0 through 1.</exception>
    /// <remarks>
    /// A test passes a fixed source here, so a rate between the two ends is deterministic too. The
    /// source runs on the caller's thread, so a source a test writes needs no lock.
    /// </remarks>
    public EvaluationSampler(double rate, Func<double> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        if (double.IsNaN(rate) || rate < 0 || rate > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "the sample rate runs from 0 through 1.");
        }

        Rate = rate;
        _draw = draw;
    }

    /// <summary>Gets the share of turns this sampler selects.</summary>
    public double Rate { get; }

    /// <summary>Decides whether to evaluate one finished turn.</summary>
    /// <returns><see langword="true"/> when the turn falls in the sample.</returns>
    public bool ShouldSample()
    {
        if (Rate <= 0)
        {
            return false;
        }

        return Rate >= 1 || _draw() < Rate;
    }
}

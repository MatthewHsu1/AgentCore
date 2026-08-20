using AgentCore.Application.Evaluation;
using Xunit;

namespace AgentCore.Application.Tests.Evaluation;

/// <summary>
/// The sampling decision triage row T18 defers, and decision D9 keeps off the turn.
/// </summary>
/// <remarks>
/// The sampler answers one question and runs nothing. The two ends of the range answer without a
/// draw, and every rate between them draws once from a source a test supplies.
/// </remarks>
public sealed class EvaluationSamplerTests
{
    [Fact]
    public void ARateOfZero_SamplesNothingAndDrawsNothing()
    {
        int draws = 0;
        EvaluationSampler sampler = new(0, () => { draws++; return 0; });

        Assert.False(sampler.ShouldSample());
        Assert.False(sampler.ShouldSample());
        Assert.Equal(0, draws);
    }

    [Fact]
    public void ARateOfOne_SamplesEveryTurnAndDrawsNothing()
    {
        int draws = 0;
        EvaluationSampler sampler = new(1, () => { draws++; return 1; });

        Assert.True(sampler.ShouldSample());
        Assert.True(sampler.ShouldSample());
        Assert.Equal(0, draws);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.09, true)]
    [InlineData(0.1, false)]
    [InlineData(0.9, false)]
    public void ARateBetweenTheEnds_ComparesTheDrawAgainstTheRate(double draw, bool sampled)
    {
        EvaluationSampler sampler = new(0.1, () => draw);

        Assert.Equal(sampled, sampler.ShouldSample());
    }

    [Fact]
    public void TheSamplerDrawsOncePerDecision()
    {
        Queue<double> draws = new([0.05, 0.5, 0.05]);
        EvaluationSampler sampler = new(0.1, draws.Dequeue);

        Assert.True(sampler.ShouldSample());
        Assert.False(sampler.ShouldSample());
        Assert.True(sampler.ShouldSample());
        Assert.Empty(draws);
    }

    [Fact]
    public void TheSamplerReportsItsRate()
    {
        Assert.Equal(0.25, new EvaluationSampler(0.25).Rate);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void ARateOutsideTheRange_FailsAtStartup(double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvaluationSampler(rate));
    }

    [Fact]
    public void ANullDrawSource_FailsAtStartup()
    {
        Assert.Throws<ArgumentNullException>(() => new EvaluationSampler(0.5, null!));
    }
}

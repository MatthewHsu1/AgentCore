using AgentCore.Domain;
using Xunit;

namespace AgentCore.Domain.Tests;

/// <summary>
/// <see cref="TurnResult"/> is a pure record, and Domain stays dependency-free.
/// </summary>
public sealed class TurnResultTests
{
    [Fact]
    public void TwoResultsWithTheSameContent_AreEqual()
    {
        TurnResult first = new("call-1", 0, "greeting", "identify", "hello", false, null);
        TurnResult second = new("call-1", 0, "greeting", "identify", "hello", false, null);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void AFailedExtraction_IsCarriedAndNotThrown()
    {
        TurnResult result = new("call-1", 2, "resolve", "resolve", "one moment", false, "the extractor returned an empty reply");

        Assert.Equal("the extractor returned an empty reply", result.ExtractionFailure);
        Assert.False(result.IsTerminal);
    }

    [Fact]
    public void ATerminalTurn_ReportsItself()
    {
        TurnResult result = new("call-1", 4, "escalate", "close", "goodbye", true, null);

        Assert.True(result.IsTerminal);
        Assert.Equal("close", result.StageAfter);
    }

    [Fact]
    public void Domain_ReferencesNothingOutsideTheFramework()
    {
        var referenced = typeof(TurnResult).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToList();

        // Domain holds pure records and takes no package, so the turn loop and the model client stay out.
        Assert.DoesNotContain(referenced, name => name.StartsWith("AgentCore.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal));
    }
}

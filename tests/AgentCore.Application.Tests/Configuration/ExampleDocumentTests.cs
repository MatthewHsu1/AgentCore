using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The worked example's own invariants, so a test file does not have to remember them.
/// </summary>
public sealed class ExampleDocumentTests
{
    [Fact]
    public void LastProviderLine_IsReallyTheLastLineOfProviders()
    {
        // Three seam tests splice their own provider block in after this anchor. If it stops being
        // the last line, the splice lands INSIDE whatever block follows: still valid YAML, wrong
        // nesting, and a provider quietly belonging to the wrong parent. That is harder to notice
        // than the failure this anchor was introduced to fix, so it is checked rather than trusted.
        var lines = ExampleDocument.Yaml.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var start = lines.FindIndex(line => line.StartsWith("providers:", StringComparison.Ordinal));
        Assert.True(start >= 0, "The example document has no providers: block.");

        var last = start;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith(' '))
            {
                break;
            }

            last = i;
        }

        Assert.Equal(ExampleDocument.LastProviderLine, lines[last]);
    }

    [Fact]
    public void LastProviderLine_AppearsExactlyOnce()
    {
        // Replace() rewrites every occurrence, so a second copy of this line would splice the same
        // provider block in twice and the document would fail to load on a duplicate key.
        var occurrences = ExampleDocument.Yaml.Split(ExampleDocument.LastProviderLine).Length - 1;

        Assert.Equal(1, occurrences);
    }
}

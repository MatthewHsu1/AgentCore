using AgentCore.Application.Skills;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Skills;

/// <summary>
/// The catalog is the only thing the compiler sees of a skills folder, so it must refuse to exist
/// half-built: a source with no names, or names with no source, would fail later and further away.
/// </summary>
public sealed class SkillCatalogTests
{
    [Fact]
    public void Constructor_CarriesTheSourceAndTheNames()
    {
        using AgentFileSkillsSource source = new(Path.Combine(Path.GetTempPath(), "agentcore-empty-skills"));
        HashSet<string> names = new(["warranty-returns"], StringComparer.Ordinal);

        SkillCatalog catalog = new(source, names);

        Assert.Same(source, catalog.Source);
        Assert.Contains("warranty-returns", catalog.Names);
    }

    [Fact]
    public void Constructor_ANullSource_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => new SkillCatalog(null!, new HashSet<string>(StringComparer.Ordinal)));

    [Fact]
    public void Constructor_NullNames_Throws()
    {
        using AgentFileSkillsSource source = new(Path.Combine(Path.GetTempPath(), "agentcore-empty-skills"));

        Assert.Throws<ArgumentNullException>(() => new SkillCatalog(source, null!));
    }
}

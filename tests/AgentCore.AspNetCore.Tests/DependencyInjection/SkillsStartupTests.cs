using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Skills;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// MAF says nothing when a skills folder is wrong: a missing folder, a dropped skill and a
/// duplicate name all enumerate cleanly and serve the wrong answers later. Every one of them must
/// stop the boot instead.
/// </summary>
public sealed class SkillsStartupTests
{
    [Fact]
    public async Task OpenAsync_NoHostBinding_ReturnsNull()
    {
        var catalog = await SkillsStartup.OpenAsync(
            new AgentCoreOptions(), NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

        Assert.Null(catalog);
    }

    [Fact]
    public async Task OpenAsync_AGoodFolder_CarriesEveryName()
    {
        using var folder = SkillFolder.Create()
            .WithSkill("warranty-returns")
            .WithSkill("shipping-claims");

        var catalog = await OpenAsync(folder.Root);

        Assert.NotNull(catalog);
        Assert.Equal(
            ["shipping-claims", "warranty-returns"],
            catalog.Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OpenAsync_AMissingFolder_FailsNamingThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "agentcore-skills-does-not-exist");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(missing).AsTask());

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_APathThatIsAFile_SaysSoRatherThanCallingItMissing()
    {
        // Directory.Exists is false for a file, so a deployment that hands over an archive or a
        // single SKILL.md would otherwise be sent hunting for a folder that is right there.
        using var folder = SkillFolder.Create();
        var file = Path.Combine(folder.Root, "skills.zip");
        await File.WriteAllTextAsync(file, "not a folder", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(file).AsTask());

        Assert.Contains($"the skills path '{file}' is a file, not a folder", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_AnEmptyFolder_Fails()
    {
        using var folder = SkillFolder.Create();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(folder.Root).AsTask());

        Assert.Contains("serves no skill", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ANameThatDoesNotMatchItsDirectory_FailsNamingTheDirectory()
    {
        using var folder = SkillFolder.Create().WithSkill("folder-name", frontmatterName: "other-name");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(folder.Root).AsTask());

        Assert.Contains("folder-name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_TwoDirectoriesServingOneName_FailsNamingBoth()
    {
        using var folder = SkillFolder.Create()
            .WithSkill("team-a/warranty-returns")
            .WithSkill("team-b/warranty-returns");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(folder.Root).AsTask());

        Assert.Contains("team-a", failure.Message, StringComparison.Ordinal);
        Assert.Contains("team-b", failure.Message, StringComparison.Ordinal);

        // Both names would also appear if MAF dropped both skills and the dropped-directory check
        // fired instead, so the wording is what tells the two checks apart.
        Assert.Contains("serves one name from more than one directory", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not load:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ASkillDirectoryInsideASkillDirectory_LoadsTheOuterOneAndDoesNotFail()
    {
        // MAF stops descending at the first SKILL.md, so the inner directory is never a candidate.
        // A glob-based check would call it a dropped skill and fail this legal folder.
        using var folder = SkillFolder.Create()
            .WithSkill("outer")
            .WithSkill("outer/inner");

        var catalog = await OpenAsync(folder.Root);

        Assert.NotNull(catalog);
        Assert.Equal(["outer"], catalog.Names);
    }

    [Fact]
    public async Task OpenAsync_ASkillWithAScript_HidesItFromLoadSkill()
    {
        using var folder = SkillFolder.Create().WithSkill("warranty-returns").WithScript("warranty-returns");

        var catalog = await OpenAsync(folder.Root);

        var body = await SkillFolder.LoadBodyAsync(catalog!.Source, "warranty-returns", TestContext.Current.CancellationToken);

        Assert.Contains("<available_scripts />", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hello.py", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_ASkillWithAReference_StillListsIt()
    {
        // The script filter must not take resources down with it.
        using var folder = SkillFolder.Create().WithSkill("warranty-returns").WithReference("warranty-returns");

        var catalog = await OpenAsync(folder.Root);

        var body = await SkillFolder.LoadBodyAsync(catalog!.Source, "warranty-returns", TestContext.Current.CancellationToken);

        Assert.Contains("references/notes.md", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAsync_APathBinding_HandsBackTheCachingWrapper()
    {
        // The boot tracker disposes whatever this returns as Source. It must be the caching wrapper,
        // because disposing that is what cascades into the file source underneath it.
        using var folder = SkillFolder.Create().WithSkill("warranty-returns");

        var catalog = await OpenAsync(folder.Root);

        Assert.IsType<CachingAgentSkillsSource>(catalog!.Source);
    }

    [Fact]
    public async Task OpenAsync_ABoundSource_CarriesEveryNameAndHandsTheSourceBackUnwrapped()
    {
        using AgentInMemorySkillsSource source = new([Skill("warranty-returns"), Skill("shipping-claims")]);

        var catalog = await OpenAsync(source);

        Assert.NotNull(catalog);
        Assert.Equal(
            ["shipping-claims", "warranty-returns"],
            catalog.Names.Order(StringComparer.Ordinal));

        // UseSkills(AgentSkillsSource) documents that it transfers ownership, and the boot tracker
        // disposes exactly what it finds here. Wrapping would hand back something else to dispose.
        Assert.Same(source, catalog.Source);
    }

    [Fact]
    public async Task OpenAsync_ABoundSourceServingNothing_FailsWithoutNamingAFolder()
    {
        using AgentInMemorySkillsSource source = new([]);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(source).AsTask());

        Assert.Equal(
            "The configuration document did not load. the skills source bound by "
            + "options.UseSkills(...) serves no skill.",
            failure.Message);
    }

    [Fact]
    public async Task OpenAsync_ABoundSourceServingOneNameTwice_FailsWithoutNamingAFolder()
    {
        // AgentInMemorySkillsSource keeps the list it was given, so a host reaches the duplicate
        // check with no file skill in the group and nothing but a name to be told about.
        using AgentInMemorySkillsSource source = new([Skill("warranty-returns"), Skill("warranty-returns")]);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(source).AsTask());

        Assert.Equal(
            "The configuration document did not load. the skills source bound by options.UseSkills(...) "
            + "serves one name more than once: 'warranty-returns' is served by 2 skills, of type "
            + "AgentInlineSkill. Which copy loads is decided by the order the source returned them, so "
            + "the answer can change between runs. Serve each name once.",
            failure.Message);
    }

    [Fact]
    public async Task OpenAsync_ABoundSourceThatFailsACheck_DisposesIt()
    {
        // UseSkills(AgentSkillsSource) documents that it takes ownership, and OpenAsync is the only
        // owner until it returns: the boot tracker never sees a source a check threw over.
        using var folder = SkillFolder.Create();
        TrackingSkillsSource tracked = new(folder.Root);

        await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(tracked).AsTask());

        Assert.True(tracked.Disposed);
    }

    [Fact]
    public async Task OpenAsync_ABoundSourceThatOpensCleanly_IsNotDisposed()
    {
        using var folder = SkillFolder.Create().WithSkill("warranty-returns");
        TrackingSkillsSource tracked = new(folder.Root);

        var catalog = await OpenAsync(tracked);

        Assert.NotNull(catalog);
        Assert.False(tracked.Disposed);
    }

    [Fact]
    public async Task OpenAsync_ABoundSourceThatCannotBeRead_FailsWithoutNamingAFolder()
    {
        using UnreadableSkillsSource source = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => OpenAsync(source).AsTask());

        Assert.StartsWith(
            "The configuration document did not load. the skills source bound by "
            + "options.UseSkills(...) could not be read:",
            failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("the skills folder", failure.Message, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
    }

    [Fact]
    public async Task UseSkills_ASourceAfterAPath_ReadsTheSource()
    {
        using var folder = SkillFolder.Create().WithSkill("from-the-folder");
        using AgentInMemorySkillsSource source = new([Skill("from-the-source")]);

        AgentCoreOptions options = new();
        options.UseSkills(folder.Root).UseSkills(source);

        var catalog = await SkillsStartup.OpenAsync(
            options, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(["from-the-source"], catalog!.Names);
    }

    [Fact]
    public async Task UseSkills_APathAfterASource_ReadsThePath()
    {
        // OpenAsync prefers a bound source, so only clearing the slot keeps this binding from being
        // silently discarded.
        using var folder = SkillFolder.Create().WithSkill("from-the-folder");
        using AgentInMemorySkillsSource source = new([Skill("from-the-source")]);

        AgentCoreOptions options = new();
        options.UseSkills(source).UseSkills(folder.Root);

        var catalog = await SkillsStartup.OpenAsync(
            options, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(["from-the-folder"], catalog!.Names);
    }

    private static AgentInlineSkill Skill(string name)
        => new(new AgentSkillFrontmatter(name, "A skill used by a test."), "Do the thing.");

    private static ValueTask<SkillCatalog?> OpenAsync(string path)
    {
        AgentCoreOptions options = new();
        options.UseSkills(path);

        return SkillsStartup.OpenAsync(options, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
    }

    private static ValueTask<SkillCatalog?> OpenAsync(AgentSkillsSource source)
    {
        AgentCoreOptions options = new();
        options.UseSkills(source);

        return SkillsStartup.OpenAsync(options, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// MAF ships no source that fails this way, and the handler exists for a host that binds its own
    /// source over a filesystem it cannot read.
    /// </summary>
    private sealed class UnreadableSkillsSource : AgentSkillsSource
    {
        public override Task<IList<AgentSkill>> GetSkillsAsync(
            AgentSkillsSourceContext context,
            CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("Access to the path '/srv/skills' is denied.");
    }
}

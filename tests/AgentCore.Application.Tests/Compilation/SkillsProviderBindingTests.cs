using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// An agent that declares skills: must carry the provider even when it declares no knowledge:
/// block — the knowledge branch returns early, so a skills branch placed below it would compile
/// to nothing at all.
/// </summary>
public sealed class SkillsProviderBindingTests
{
    private const string SkillsYaml = """
        apiVersion: agentcore/v1
        name: skills-only
        agents:
          items:
            - { id: support, skills: [warranty-returns] }
        """;

    private const string NoSkillsYaml = """
        apiVersion: agentcore/v1
        name: no-skills
        agents:
          items:
            - { id: greeter }
        """;

    [Fact]
    public void AnAgentWithSkillsAndNoKnowledge_CarriesTheSkillsProvider()
    {
        using var folder = SkillFolder.Create().WithSkill("warranty-returns");
        using AgentFileSkillsSource source = new(folder.Root);
        SkillCatalog catalog = new(source, new HashSet<string>(["warranty-returns"], StringComparer.Ordinal));

        var providers = Providers(CompileOne(SkillsYaml, catalog));

        Assert.Contains(providers, provider => provider is ReadOnlySkillsProvider);
    }

    [Fact]
    public void AnAgentWithNoSkillsList_CarriesNoSkillsProvider()
    {
        using var folder = SkillFolder.Create().WithSkill("warranty-returns");
        using AgentFileSkillsSource source = new(folder.Root);
        SkillCatalog catalog = new(source, new HashSet<string>(["warranty-returns"], StringComparer.Ordinal));

        var providers = Providers(CompileOne(NoSkillsYaml, catalog));

        Assert.DoesNotContain(providers, provider => provider is ReadOnlySkillsProvider);
    }

    [Fact]
    public void AnAgentWithSkillsAndNoBoundFolder_FailsNamingTheAgentAndTheSeam()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => CompileOne(SkillsYaml, catalog: null));

        Assert.Contains("agent 'support'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("options.UseSkills(...)", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/agents/items/0/skills", Assert.Single(failure.Errors).Pointer);
        Assert.Equal(ConfigurationCheck.DocumentSchema, Assert.Single(failure.Errors).Check);
    }

    private static AIAgent CompileOne(string yaml, SkillCatalog? catalog)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Skills = catalog });

        return Assert.Single(compiled.Agents.Values);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }
}

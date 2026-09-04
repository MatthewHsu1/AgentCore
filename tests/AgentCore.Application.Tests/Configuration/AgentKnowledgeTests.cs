using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// <see cref="AgentKnowledge"/>: the key-by-key merge of one agent's <c>knowledge:</c> block against
/// <c>agents.defaults.knowledge</c>, and the two boolean questions it answers over a whole document.
/// </summary>
public sealed class AgentKnowledgeTests
{
    [Fact]
    public void Compose_NoBlockAnywhere_IsNull()
    {
        // An agent with nothing to do with the knowledge base gets no provider at all,
        // and therefore pays nothing.
        Assert.Null(AgentKnowledge.Compose(defaults: null, Agent(knowledge: null)));
    }

    [Fact]
    public void Compose_DefaultsOnly_UsesTheDefaults()
    {
        var resolved = AgentKnowledge.Compose(
            Defaults(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Limit = 5 }),
            Agent(knowledge: null));

        Assert.Equal(KnowledgeMode.Prefetch, resolved!.Mode);
        Assert.Equal(5, resolved.Limit);
        Assert.False(resolved.Citations);
        Assert.True(resolved.Scoped);
    }

    [Fact]
    public void Compose_AgentSetsTwoKeys_InheritsTheRestKeyByKey()
    {
        // This is the new merge semantics. `Model` replaces wholesale and `Instructions`
        // concatenates; neither is this.
        var resolved = AgentKnowledge.Compose(
            Defaults(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Limit = 5, Citations = false }),
            Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool, Citations = false }));

        Assert.Equal(KnowledgeMode.Tool, resolved!.Mode);
        Assert.Equal(5, resolved.Limit);          // inherited
        Assert.False(resolved.Citations);
    }

    [Fact]
    public void Compose_AgentOverridesOnlyMode_LimitStillCameFromDefaultsNotTheBuiltInDefault()
    {
        // Defaults' Limit (7) differs from the built-in default (5), so this only passes if the
        // merge actually reads agents.defaults.knowledge.limit, and not a wholesale-replace that
        // falls back straight from the agent's block to the built-in constant.
        var resolved = AgentKnowledge.Compose(
            Defaults(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Limit = 7 }),
            Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool }));

        Assert.Equal(7, resolved!.Limit);
    }

    [Fact]
    public void Compose_CitationsUnset_DefaultsToFalse()
    {
        // A forgotten flag must fail loudly, not leak silently: manifest titles carry
        // internal labels and ticket titles can carry a customer name.
        var resolved = AgentKnowledge.Compose(null, Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool }));

        Assert.False(resolved!.Citations);
    }

    [Fact]
    public void Compose_ScopedUnset_DefaultsToTrue()
    {
        var resolved = AgentKnowledge.Compose(null, Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool }));

        Assert.True(resolved!.Scoped);
    }

    [Fact]
    public void AnyScoped_OneScopedAgentAmongUnscopedOnes_IsTrue()
    {
        var section = new AgentsConfiguration
        {
            Items =
            [
                Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool, Scoped = false }, id: "analyst"),
                Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch }, id: "resolver"),
            ],
        };

        Assert.True(AgentKnowledge.AnyScoped(section));
    }

    [Fact]
    public void AnyScoped_NoAgentDeclaresKnowledge_IsFalse()
        => Assert.False(AgentKnowledge.AnyScoped(new AgentsConfiguration { Items = [Agent(knowledge: null)] }));

    [Fact]
    public void AnyScoped_AgentInheritsScopedFromDefaultsWithNoOwnBlock_IsTrue()
    {
        // The composed value, not the raw one: this agent declares no knowledge: block of its own,
        // and is still scoped because it inherits scoped: true from agents.defaults.knowledge.
        var section = new AgentsConfiguration
        {
            Defaults = Defaults(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool, Scoped = true }),
            Items = [Agent(knowledge: null)],
        };

        Assert.True(AgentKnowledge.AnyScoped(section));
    }

    [Fact]
    public void AllScoped_EveryAgentThatDeclaresKnowledgeIsScoped_IsTrue()
    {
        var section = new AgentsConfiguration
        {
            Items =
            [
                Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool }, id: "a"),
                Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Scoped = true }, id: "b"),
            ],
        };

        Assert.True(AgentKnowledge.AllScoped(section));
    }

    [Fact]
    public void AllScoped_OneUnscopedAgentAmongScopedOnes_IsFalse()
    {
        var section = new AgentsConfiguration
        {
            Items =
            [
                Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool }, id: "a"),
                Agent(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Scoped = false }, id: "b"),
            ],
        };

        Assert.False(AgentKnowledge.AllScoped(section));
    }

    [Fact]
    public void AllScoped_NoAgentDeclaresKnowledge_IsTrueVacuously()
        // Mirrors Enumerable.All's own empty-sequence convention. A store no agent ever reads is
        // never asked to enforce anything, so defaulting the empty case to the fail-closed direction
        // costs nothing.
        => Assert.True(AgentKnowledge.AllScoped(new AgentsConfiguration { Items = [Agent(knowledge: null)] }));

    [Fact]
    public void AllScoped_AgentInheritsUnscopedFromDefaultsWithNoOwnBlock_IsFalse()
    {
        // Composed, not raw, cuts both ways: an agent with no knowledge: block of its own still
        // counts against AllScoped when the defaults it inherits from set scoped: false.
        var section = new AgentsConfiguration
        {
            Defaults = Defaults(new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Tool, Scoped = false }),
            Items = [Agent(knowledge: null)],
        };

        Assert.False(AgentKnowledge.AllScoped(section));
    }

    [Fact]
    public void TwoDefaultValuedInstances_AreEqualAndShareAHashCode()
    {
        // ResolvedKnowledge is a public record; its generated Equals and GetHashCode walk every
        // member, including Clarification. A default value that allocates a fresh instance per
        // ResolvedKnowledge would compare unequal to another default-valued one by reference,
        // silently breaking the value semantics a record promises.
        var a = new ResolvedKnowledge(KnowledgeMode.Prefetch, 5, Citations: false, Scoped: true);
        var b = new ResolvedKnowledge(KnowledgeMode.Prefetch, 5, Citations: false, Scoped: true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TheShippedSchema_CapsLimitAtTheSameValueAsTheConstant()
    {
        // JSON Schema cannot reference a C# constant, so agentcore-v1.schema.json hardcodes
        // AgentKnowledgeConfiguration.MaximumLimit's value under $defs.agentKnowledge.properties.limit.
        // Nothing ties the two together at compile time, so this is what catches the day someone
        // changes one and forgets the other.
        var path = Path.Combine(RepositoryRoot(), "src", "AgentCore.Application", "Configuration", "Schema", "agentcore-v1.schema.json");
        Assert.True(File.Exists(path), $"The shipped schema is missing at '{path}'.");

        var schema = JsonNode.Parse(File.ReadAllText(path))!;
        var maximum = schema["$defs"]!["agentKnowledge"]!["properties"]!["limit"]!["maximum"]!.GetValue<int>();

        Assert.Equal(AgentKnowledgeConfiguration.MaximumLimit, maximum);
    }

    private static AgentConfiguration Agent(AgentKnowledgeConfiguration? knowledge, string id = "agent")
        => new() { Id = id, Knowledge = knowledge };

    private static AgentDefaults Defaults(AgentKnowledgeConfiguration knowledge)
        => new() { Knowledge = knowledge };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentCore.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// <see cref="AgentHarness"/>: the key-by-key merge of one agent's <c>todos:</c> / <c>mode:</c>
/// switches against <c>agents.defaults</c>.
/// </summary>
public sealed class AgentHarnessTests
{
    [Fact]
    public void Compose_NoKeyAnywhere_BothFalse()
    {
        var resolved = AgentHarness.Compose(defaults: null, Agent(todos: null, mode: null));

        Assert.False(resolved.Todos);
        Assert.False(resolved.Mode);
    }

    [Fact]
    public void Compose_DefaultsOnly_IsInherited()
    {
        var resolved = AgentHarness.Compose(
            new AgentDefaults { Todos = true, Mode = true },
            Agent(todos: null, mode: null));

        Assert.True(resolved.Todos);
        Assert.True(resolved.Mode);
    }

    [Fact]
    public void Compose_AgentFalseOverridesDefaultsTrue_TakesTheAgent()
    {
        var resolved = AgentHarness.Compose(
            new AgentDefaults { Todos = true, Mode = true },
            Agent(todos: false, mode: false));

        Assert.False(resolved.Todos);
        Assert.False(resolved.Mode);
    }

    [Fact]
    public void Compose_AgentTrueOverNullDefaults_IsTrue()
    {
        var resolved = AgentHarness.Compose(defaults: null, Agent(todos: true, mode: true));

        Assert.True(resolved.Todos);
        Assert.True(resolved.Mode);
    }

    private static AgentConfiguration Agent(bool? todos, bool? mode) =>
        new() { Id = "reply", Todos = todos, Mode = mode };
}

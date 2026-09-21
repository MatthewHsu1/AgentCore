using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// <see cref="AgentHarness"/>: the key-by-key merge of one agent's <c>todos:</c> / <c>mode:</c> /
    /// <c>clock:</c> / <c>baseInstructions:</c> switches against <c>agents.defaults</c>.
    /// </summary>
    public sealed class AgentHarnessTests
    {
        [Fact]
        public void Compose_NoKeyAnywhere_BothFalse()
        {
            ResolvedHarness resolved = AgentHarness.Compose(defaults: null, Agent(todos: null, mode: null));

            Assert.False(resolved.Todos);
            Assert.False(resolved.Mode);
        }

        [Fact]
        public void Compose_NoKeyAnywhere_ClockAndBaseAreOn()
        {
            ResolvedHarness resolved = AgentHarness.Compose(defaults: null, Agent(todos: null, mode: null));

            Assert.True(resolved.Clock);
            Assert.True(resolved.BaseInstructions);
        }

        [Fact]
        public void Compose_DefaultsFalse_TurnsClockAndBaseOff()
        {
            ResolvedHarness resolved = AgentHarness.Compose(
                new AgentDefaults { Clock = false, BaseInstructions = false },
                Agent(todos: null, mode: null));

            Assert.False(resolved.Clock);
            Assert.False(resolved.BaseInstructions);
        }

        [Fact]
        public void Compose_AgentTrueOverDefaultsFalse_TakesTheAgent()
        {
            ResolvedHarness resolved = AgentHarness.Compose(
                new AgentDefaults { Clock = false, BaseInstructions = false },
                Agent(todos: null, mode: null) with { Clock = true, BaseInstructions = true });

            Assert.True(resolved.Clock);
            Assert.True(resolved.BaseInstructions);
        }

        [Fact]
        public void Compose_DefaultsOnly_IsInherited()
        {
            ResolvedHarness resolved = AgentHarness.Compose(
                new AgentDefaults { Todos = true, Mode = true },
                Agent(todos: null, mode: null));

            Assert.True(resolved.Todos);
            Assert.True(resolved.Mode);
        }

        [Fact]
        public void Compose_AgentFalseOverridesDefaultsTrue_TakesTheAgent()
        {
            ResolvedHarness resolved = AgentHarness.Compose(
                new AgentDefaults { Todos = true, Mode = true },
                Agent(todos: false, mode: false));

            Assert.False(resolved.Todos);
            Assert.False(resolved.Mode);
        }

        [Fact]
        public void Compose_AgentTrueOverNullDefaults_IsTrue()
        {
            ResolvedHarness resolved = AgentHarness.Compose(defaults: null, Agent(todos: true, mode: true));

            Assert.True(resolved.Todos);
            Assert.True(resolved.Mode);
        }

        private static AgentConfiguration Agent(bool? todos, bool? mode)
        {
            return new() { Id = "reply", Todos = todos, Mode = mode };
        }
    }
}

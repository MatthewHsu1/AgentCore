using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class HookTableTests
    {
        [Fact]
        public void AGateIsListedOnlyForTheHooksThatOverrideIt()
        {
            ToolOnly tool = new();
            NoticeOnly notice = new();

            HookTable table = HookTable.Build([tool, notice]);

            Assert.Equal([tool], table.For(GatePoint.BeforeTool));
            Assert.Empty(table.For(GatePoint.BeforeModel));
            Assert.True(table.Overrides(GatePoint.BeforeTool));
            Assert.False(table.Overrides(GatePoint.AfterRun));
        }

        [Fact]
        public void ANoticeIsWantedOnlyWhenSomeHookOverridesIt()
        {
            HookTable table = HookTable.Build([new ToolOnly(), new NoticeOnly()]);

            Assert.True(table.Wants(typeof(TurnCompleted)));
            Assert.False(table.Wants(typeof(ModelCalled)));
            Assert.Equal([typeof(TurnCompleted)], table.NoticesOf(table.NoticeHooks.Single()).ToArray());
        }

        // Hooks run in registration order; AgentCore puts its built-ins first in that list.
        [Fact]
        public void HooksKeepTheirRegistrationOrder()
        {
            ToolOnly first = new();
            ToolOnly second = new();

            Assert.Equal([first, second], HookTable.Build([first, second]).For(GatePoint.BeforeTool));
        }

        [Fact]
        public void AnInstanceRegisteredTwiceIsOneHook()
        {
            ToolOnly hook = new();

            HookTable table = HookTable.Build([hook, hook]);

            _ = Assert.Single(table.For(GatePoint.BeforeTool));
            _ = Assert.Single(table.Hooks);
        }

        [Fact]
        public void AMethodHiddenWithNewIsNotAnOverride()
        {
            HookTable table = HookTable.Build([new Hiding()]);

            Assert.Empty(table.For(GatePoint.BeforeTool));
            Assert.False(table.OverridesAnyGate(table.Hooks[0]));
        }

        private sealed class ToolOnly : AgentHook
        {
            public override ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken) => default;
        }

        private sealed class NoticeOnly : AgentHook
        {
            public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken) => default;
        }

        private sealed class Hiding : AgentHook
        {
            public new ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken) =>
                throw new InvalidOperationException($"{GetType().Name} hides the gate method, so no gate calls it.");
        }
    }
}

using AgentCore.Application.Hooks.Notices;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class TurnRefusalTokensTests
    {
        // The refusedReason tokens: busy, conflict, gone, dropped, terminal, disposed and in_use.
        [Theory]
        [InlineData(TurnRefusal.Busy, "busy")]
        [InlineData(TurnRefusal.Conflict, "conflict")]
        [InlineData(TurnRefusal.Gone, "gone")]
        [InlineData(TurnRefusal.Dropped, "dropped")]
        [InlineData(TurnRefusal.Terminal, "terminal")]
        [InlineData(TurnRefusal.Disposed, "disposed")]
        [InlineData(TurnRefusal.InUse, "in_use")]
        public void EachRefusalHasItsAuditToken(TurnRefusal refusal, string token)
        {
            Assert.Equal(token, TurnRefusalTokens.ToToken(refusal));
        }

        [Fact]
        public void AValueOutsideTheSetIsRefused()
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => TurnRefusalTokens.ToToken((TurnRefusal)99));
        }
    }
}

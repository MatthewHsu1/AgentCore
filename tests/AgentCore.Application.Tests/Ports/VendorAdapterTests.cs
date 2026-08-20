using AgentCore.Application.Ports;
using Xunit;

namespace AgentCore.Application.Tests.Ports;

/// <summary>Pins that every vendor seam shares one base, so one selector can serve them all.</summary>
public sealed class VendorAdapterTests
{
    [Theory]
    [InlineData(typeof(IChatClientAdapter))]
    [InlineData(typeof(IKnowledgeStoreAdapter))]
    [InlineData(typeof(IModerationAdapter))]
    [InlineData(typeof(ITelemetryAdapter))]
    [InlineData(typeof(ISpeechAdapter))]
    [InlineData(typeof(ICallAdapter))]
    public void EveryVendorSeamDerivesFromTheOneBase(Type seam)
    {
        Assert.True(
            typeof(IVendorAdapter).IsAssignableFrom(seam),
            $"{seam.Name} must derive from IVendorAdapter so VendorAdapterSelector can serve it.");
    }

    [Fact]
    public void TheBaseCarriesOnlyTheKind()
    {
        var members = typeof(IVendorAdapter).GetMembers();

        // One property means one getter method plus the property itself.
        Assert.Equal(2, members.Length);
        Assert.NotNull(typeof(IVendorAdapter).GetProperty("Kind"));
    }
}

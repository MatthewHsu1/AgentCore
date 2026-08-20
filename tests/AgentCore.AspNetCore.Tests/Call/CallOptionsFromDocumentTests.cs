using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// The three socket limits come out of <c>providers.call</c>, and a bad one stops the start.
/// </summary>
/// <remarks>
/// These replace the three map-time <c>ArgumentOutOfRangeException</c> facts that used to live in
/// <c>TelnyxRelayEndpointTests</c>. The values no longer come from a C# caller, so the failure is a
/// <see cref="ConfigurationLoadException"/> carrying the JSON pointer of the offending field rather
/// than an exception naming a property: a reader needs the line of the document to fix. Spec §12.
/// </remarks>
public sealed class CallOptionsFromDocumentTests
{
    private static CallProviderConfiguration Entry(
        int? idle = null, int? close = null, int? frame = null)
        => new()
        {
            Kind = "telnyx-relay",
            IdleTimeoutSeconds = idle,
            CloseTimeoutSeconds = close,
            MaxFrameBytes = frame,
        };

    [Fact]
    public void TheThreeKnobsReachTheOptions()
    {
        var options = TelnyxRelayCallAdapter.BuildOptions(Entry(idle: 30, close: 5, frame: 4096));

        Assert.Equal(TimeSpan.FromSeconds(30), options.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.CloseTimeout);
        Assert.Equal(4096, options.MaxFrameBytes);
    }

    [Fact]
    public void AnAbsentKnobKeepsTheShippedDefault()
    {
        var shipped = new TelnyxRelayOptions();

        var options = TelnyxRelayCallAdapter.BuildOptions(Entry());

        Assert.Equal(shipped.IdleTimeout, options.IdleTimeout);
        Assert.Equal(shipped.CloseTimeout, options.CloseTimeout);
        Assert.Equal(shipped.MaxFrameBytes, options.MaxFrameBytes);
    }

    [Fact]
    public void MinusOneMeansInfinite()
    {
        var options = TelnyxRelayCallAdapter.BuildOptions(Entry(idle: -1));

        Assert.Equal(Timeout.InfiniteTimeSpan, options.IdleTimeout);
    }

    [Fact]
    public void MinusOneMeansInfiniteOnTheCloseTimeoutToo()
    {
        // The two timeouts read the same field of the same document block, and nothing but this
        // pins that they read it the same way. A close timeout that quietly turned -1 into a
        // negative TimeSpan would be refused by CancelAfter on the first teardown that used it.
        var options = TelnyxRelayCallAdapter.BuildOptions(Entry(close: -1));

        Assert.Equal(Timeout.InfiniteTimeSpan, options.CloseTimeout);
    }

    [Fact]
    public void ANegativeSecondsThatIsNotMinusOneFailsTheStartWithAPointer()
    {
        // -1 is the one negative the document is allowed to write, and it means never. Every other
        // negative is a typo, and it must be caught here rather than handed to CancelAfter, which
        // refuses any negative span but -1 milliseconds at the moment teardown needs it.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => TelnyxRelayCallAdapter.BuildOptions(Entry(idle: -2)));

        Assert.Equal("/providers/call/idleTimeoutSeconds", failure.Errors[0].Pointer);
    }

    [Fact]
    public void AZeroFrameCapFailsTheStartWithAPointer()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => TelnyxRelayCallAdapter.BuildOptions(Entry(frame: 0)));

        Assert.Equal("/providers/call/maxFrameBytes", failure.Errors[0].Pointer);
    }

    [Fact]
    public void ATimeoutPastTheTimerCeilingFailsTheStartWithAPointer()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => TelnyxRelayCallAdapter.BuildOptions(Entry(idle: 5_000_000)));

        Assert.Equal("/providers/call/idleTimeoutSeconds", failure.Errors[0].Pointer);
    }

    [Fact]
    public void ACloseTimeoutPastTheTimerCeilingFailsTheStartWithItsOwnPointer()
    {
        // The pointer must name the field the document actually wrote. A shared checker that
        // reported idleTimeoutSeconds for both would send a reader to the wrong line.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => TelnyxRelayCallAdapter.BuildOptions(Entry(close: 5_000_000)));

        Assert.Equal("/providers/call/closeTimeoutSeconds", failure.Errors[0].Pointer);
    }
}

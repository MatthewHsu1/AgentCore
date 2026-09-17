using AgentCore.Application.Runtime.Harness;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// The one line a <c>clock:</c> agent reads each turn: the weekday, the date, and the time in the
/// zone it is given, with the zone named so a relative date has an anchor.
/// </summary>
public sealed class ClockContextProviderTests
{
    [Fact]
    public void Describe_ReadsTheClockInTheGivenZone()
    {
        // 2026-09-17 19:05 UTC is Thursday 14:05 in a zone five hours behind.
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 17, 19, 5, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.CreateCustomTimeZone("America/Chicago", TimeSpan.FromHours(-5), "Central", "Central");

        var line = ClockContextProvider.Describe(clock, zone);

        Assert.Equal("Today is Thursday, 2026-09-17. The local time is 14:05, America/Chicago (UTC-05:00).", line);
    }

    [Fact]
    public void Describe_AZoneAheadOfUtc_CarriesAPlusSignAndTheNextDay()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 17, 23, 30, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");

        var line = ClockContextProvider.Describe(clock, zone);

        Assert.Equal("Today is Friday, 2026-09-18. The local time is 07:30, Asia/Taipei (UTC+08:00).", line);
    }
}

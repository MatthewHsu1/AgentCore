using AgentCore.Application.Runtime.Harness;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// <see cref="CallerTimeZone.Parse"/>: an IANA id the system knows becomes a zone; anything else
    /// becomes nothing, so a bad header never changes the clock.
    /// </summary>
    public sealed class CallerTimeZoneTests
    {
        [Theory]
        [InlineData("America/Chicago")]
        [InlineData("  Asia/Taipei ")]
        [InlineData("UTC")]
        public void Parse_AKnownId_IsThatZone(string id)
        {
            TimeZoneInfo? zone = CallerTimeZone.Parse(id);

            Assert.NotNull(zone);
            Assert.Equal(id.Trim(), zone.Id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Mars/Olympus_Mons")]
        [InlineData("+08:00")]
        public void Parse_AnythingElse_IsNothing(string? id)
        {
            Assert.Null(CallerTimeZone.Parse(id));
        }
    }
}

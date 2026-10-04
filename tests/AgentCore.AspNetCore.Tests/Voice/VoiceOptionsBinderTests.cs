using System.Collections.ObjectModel;
using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Voice.Filler;
using AgentCore.AspNetCore.Voice.Options;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// <c>providers.conversation</c>'s <c>userAway</c> and <c>filler</c> reach <see cref="VoiceOptions"/>,
    /// and a value a <see cref="TimeProvider"/> wait would refuse stops the start with a pointer.
    /// </summary>
    public sealed class VoiceOptionsBinderTests
    {
        private static ConversationProviderConfiguration Entry(
            JsonElement userAway = default,
            IReadOnlyDictionary<string, VoiceFillerConfiguration>? filler = null)
        {
            return new()
            {
                Kind = "telnyx-relay",
                UserAway = userAway,
                Filler = filler ?? ReadOnlyDictionary<string, VoiceFillerConfiguration>.Empty,
            };
        }

        private static JsonElement Json(string text)
        {
            return JsonDocument.Parse(text).RootElement;
        }

        [Fact]
        public void AnOmittedUserAwayKeepsTheShippedDefault()
        {
            VoiceOptions options = VoiceOptionsBinder.Build(Entry());

            Assert.Equal(VoiceOptions.Default.UserAway, options.UserAway);
        }

        [Fact]
        public void AnExplicitNullTurnsTheAwayPromptOff()
        {
            VoiceOptions options = VoiceOptionsBinder.Build(Entry(userAway: Json("null")));

            Assert.Null(options.UserAway);
        }

        [Fact]
        public void AnObjectOverridesTheAwayPrompt()
        {
            VoiceOptions options = VoiceOptionsBinder.Build(
                Entry(userAway: Json("""{"timeoutSeconds": 30, "say": "Still there?"}""")));

            Assert.NotNull(options.UserAway);
            Assert.Equal(TimeSpan.FromSeconds(30), options.UserAway!.Timeout);
            Assert.Equal("Still there?", options.UserAway.Say);
        }

        [Fact]
        public void AnAwayTimeoutPastTheTimerCeilingFailsTheStartWithAPointer()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => VoiceOptionsBinder.Build(Entry(userAway: Json("""{"timeoutSeconds": 5000000, "say": "hi"}"""))));

            Assert.Equal("/providers/conversation/userAway/timeoutSeconds", failure.Errors[0].Pointer);
        }

        [Fact]
        public void NoFillerKeyOpensNoFiller()
        {
            VoiceOptions options = VoiceOptionsBinder.Build(Entry());

            Assert.Empty(options.Fillers);
        }

        [Fact]
        public void AFillerEntryReachesTheOptionsByToolId()
        {
            Dictionary<string, VoiceFillerConfiguration> filler = new(StringComparer.Ordinal)
            {
                ["lookup_order"] = new()
                {
                    Say = "One moment",
                    DelaySeconds = 1.5,
                    IntervalSeconds = 5,
                    MaxSteps = 2,
                },
            };

            VoiceOptions options = VoiceOptionsBinder.Build(Entry(filler: filler));

            FillerOptions built = Assert.Single(options.Fillers).Value;
            Assert.Equal("One moment", built.Source.Resolve(0));
            Assert.Equal(TimeSpan.FromSeconds(1.5), built.Delay);
            Assert.Equal(TimeSpan.FromSeconds(5), built.Interval);
            Assert.Equal(2, built.MaxSteps);
        }

        [Fact]
        public void AFillerWithNoIntervalOrCapFiresAtMostOnceWithNoLimit()
        {
            Dictionary<string, VoiceFillerConfiguration> filler = new(StringComparer.Ordinal)
            {
                ["lookup_order"] = new() { Say = "One moment", DelaySeconds = 1 },
            };

            VoiceOptions options = VoiceOptionsBinder.Build(Entry(filler: filler));

            FillerOptions built = options.Fillers["lookup_order"];
            Assert.Null(built.Interval);
            Assert.Null(built.MaxSteps);
        }

        [Fact]
        public void AFillerDelayPastTheTimerCeilingFailsTheStartWithAPointer()
        {
            Dictionary<string, VoiceFillerConfiguration> filler = new(StringComparer.Ordinal)
            {
                ["lookup_order"] = new() { Say = "One moment", DelaySeconds = 5_000_000 },
            };

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => VoiceOptionsBinder.Build(Entry(filler: filler)));

            Assert.Equal("/providers/conversation/filler/lookup_order/delaySeconds", failure.Errors[0].Pointer);
        }

        [Fact]
        public void AFillerIntervalPastTheTimerCeilingFailsTheStartWithItsOwnPointer()
        {
            Dictionary<string, VoiceFillerConfiguration> filler = new(StringComparer.Ordinal)
            {
                ["lookup_order"] = new() { Say = "One moment", DelaySeconds = 1, IntervalSeconds = 5_000_000 },
            };

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => VoiceOptionsBinder.Build(Entry(filler: filler)));

            Assert.Equal("/providers/conversation/filler/lookup_order/intervalSeconds", failure.Errors[0].Pointer);
        }

        [Theory]
        [InlineData(1e20)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(-1.0)]
        [InlineData(0.0)]
        public void AFillerDelayOutsideTheTimerRangeIsARangeErrorAtItsPointer(double seconds)
        {
            Dictionary<string, VoiceFillerConfiguration> filler = new(StringComparer.Ordinal)
            {
                ["lookup_order"] = new() { Say = "One moment", DelaySeconds = seconds },
            };

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => VoiceOptionsBinder.Build(Entry(filler: filler)));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal("/providers/conversation/filler/lookup_order/delaySeconds", error.Pointer);
            Assert.Equal(ConfigurationCheck.ValueRange, error.Check);
        }

        [Theory]
        [InlineData(1e20)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1.0)]
        public void AFillerIntervalOutsideTheTimerRangeIsARangeErrorAtItsPointer(double seconds)
        {
            Dictionary<string, VoiceFillerConfiguration> filler = new(StringComparer.Ordinal)
            {
                ["lookup_order"] = new() { Say = "One moment", DelaySeconds = 1, IntervalSeconds = seconds },
            };

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => VoiceOptionsBinder.Build(Entry(filler: filler)));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal("/providers/conversation/filler/lookup_order/intervalSeconds", error.Pointer);
            Assert.Equal(ConfigurationCheck.ValueRange, error.Check);
        }

        [Theory]
        [InlineData("1e20")]
        [InlineData("-1")]
        [InlineData("0")]
        public void AnAwayTimeoutOutsideTheTimerRangeIsARangeErrorAtItsPointer(string seconds)
        {
            JsonElement userAway = Json($$"""{"timeoutSeconds": {{seconds}}, "say": "hi"}""");

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => VoiceOptionsBinder.Build(Entry(userAway: userAway)));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal("/providers/conversation/userAway/timeoutSeconds", error.Pointer);
            Assert.Equal(ConfigurationCheck.ValueRange, error.Check);
        }
    }
}

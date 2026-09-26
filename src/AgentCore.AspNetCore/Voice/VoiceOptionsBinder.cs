using System.Globalization;
using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// Turns <c>providers.conversation</c>'s <c>userAway</c> and <c>filler</c> into a <see cref="VoiceOptions"/>.
    /// </summary>
    internal static class VoiceOptionsBinder
    {
        private const string ConversationPointer = "/providers/conversation";

        /// <summary>Builds the voice options a <c>providers.conversation</c> block asks for.</summary>
        /// <param name="configuration">The bound <c>providers.conversation</c> block.</param>
        /// <returns>The options, with <see cref="VoiceOptions.Default"/> kept for whatever the document leaves unwritten.</returns>
        /// <exception cref="ConfigurationLoadException">
        /// A <c>timeoutSeconds</c>, <c>delaySeconds</c>, or <c>intervalSeconds</c> is not more than zero, or
        /// would be refused at run time by <c>Task.Delay</c>, <c>CancelAfter</c>, or <c>Task.WaitAsync</c>.
        /// The pointer names the exact field.
        /// </exception>
        internal static VoiceOptions Build(ConversationProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return new VoiceOptions(BuildUserAway(configuration.UserAway), BuildFillers(configuration.Filler));
        }

        private static UserAwayOptions? BuildUserAway(JsonElement userAway)
        {
            return userAway.ValueKind switch
            {
                JsonValueKind.Undefined => VoiceOptions.Default.UserAway,
                JsonValueKind.Null => null,
                JsonValueKind.Object => new UserAwayOptions(
                    ToTimeSpan(
                        Pointer("userAway", "timeoutSeconds"),
                        "userAway.timeoutSeconds",
                        userAway.GetProperty("timeoutSeconds").GetDouble()),
                    userAway.GetProperty("say").GetString()!),
                _ => throw new InvalidOperationException(
                    $"unreachable: check 1 restricts providers.conversation.userAway to an object or null, and it is {userAway.ValueKind}."),
            };
        }

        private static IReadOnlyDictionary<string, FillerOptions> BuildFillers(
            IReadOnlyDictionary<string, VoiceFillerConfiguration> filler)
        {
            if (filler.Count == 0)
            {
                return VoiceOptions.Default.Fillers;
            }

            Dictionary<string, FillerOptions> built = new(filler.Count, StringComparer.Ordinal);
            foreach ((string toolId, VoiceFillerConfiguration one) in filler)
            {
                string field = $"filler.{toolId}";

                built[toolId] = new FillerOptions(
                    one.Say,
                    ToTimeSpan(Pointer("filler", toolId, "delaySeconds"), $"{field}.delaySeconds", one.DelaySeconds),
                    one.IntervalSeconds is { } interval
                        ? ToTimeSpan(Pointer("filler", toolId, "intervalSeconds"), $"{field}.intervalSeconds", interval)
                        : null,
                    one.MaxSteps);
            }

            return built;
        }

        /// <summary>Turns whole or fractional seconds from the document into the span a bounded wait accepts.</summary>
        /// <param name="pointer">The JSON Pointer to the field, for the error.</param>
        /// <param name="field">The dotted <c>providers.conversation</c> field name, for the error's message.</param>
        /// <param name="seconds">The seconds the document wrote.</param>
        private static TimeSpan ToTimeSpan(string pointer, string field, double seconds)
        {
            return seconds > 0 && seconds <= BoundedTimerLimits.MaximumBoundedDelay.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : throw new ConfigurationLoadException(new ConfigurationError
                {
                    Pointer = pointer,
                    Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"providers.conversation.{field} is {seconds} seconds. It must be more than 0 and at most "
                        + $"{BoundedTimerLimits.MaximumBoundedDelay}, the longest Task.Delay, CancelAfter, and "
                        + $"Task.WaitAsync accept at run time."),
                    Check = ConfigurationCheck.ValueRange,
                });
        }

        private static string Pointer(params ReadOnlySpan<string> segments)
        {
            string pointer = ConversationPointer;
            foreach (string segment in segments)
            {
                pointer = ConfigurationError.AppendPointer(pointer, segment);
            }

            return pointer;
        }
    }
}

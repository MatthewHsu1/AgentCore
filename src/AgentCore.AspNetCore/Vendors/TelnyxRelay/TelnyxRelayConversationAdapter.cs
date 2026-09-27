using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Voice;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// The Telnyx Conversation Relay as a conversation transport: it owns the socket and speaks Telnyx frames.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D28 buys the whole speech layer — recognition, turn detection, synthesis, and interruption —
    /// inside the relay, so <see cref="CarriesText"/> is <see langword="true"/> and both speech roles,
    /// <c>providers.speech.stt</c> and <c>providers.speech.tts</c>, must name this same vendor.
    /// </para>
    /// <para>
    /// This owns only the route. <c>TelnyxRelayConnection</c> is still what a conversation runs on, and
    /// <c>TelnyxRelayConversationChannelFactory</c> is still what hands out its two ports.
    /// </para>
    /// </remarks>
    public sealed class TelnyxRelayConversationAdapter : IConversationTransportAdapter
    {
        /// <summary>The one <c>providers.conversation.kind</c> value this vendor answers to.</summary>
        public const string TelnyxRelayKind = "telnyx-relay";

        /// <inheritdoc/>
        public string Kind => TelnyxRelayKind;

        /// <inheritdoc/>
        /// <remarks>The relay's frames carry text: the vendor performs recognition and synthesis itself.</remarks>
        public bool CarriesText => true;

        /// <inheritdoc/>
        public RequestDelegate CreateHandler(ConversationProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TelnyxRelayOptions options = BuildOptions(configuration);
            return http => TelnyxRelayEndpointRouteBuilderExtensions.HandleAsync(http, options);
        }

        /// <summary>Turns the document's limits into the options the endpoint runs on.</summary>
        /// <param name="configuration">The <c>providers.conversation</c> block.</param>
        /// <returns>The options, with the shipped default kept for every value the document omits.</returns>
        /// <exception cref="ConfigurationLoadException">
        /// A value would be refused at run time by <c>Task.Delay</c>, <c>CancelAfter</c>, or
        /// <c>Task.WaitAsync</c>, or a frame cap is not positive. The pointer names the exact field.
        /// </exception>
        /// <remarks>
        /// It throws <see cref="ConfigurationLoadException"/> and not
        /// <see cref="ArgumentOutOfRangeException"/> because the value came from a document rather than
        /// from a C# caller, and a reader needs the line to fix rather than a property name.
        /// </remarks>
        internal static TelnyxRelayOptions BuildOptions(ConversationProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TelnyxRelayOptions options = new();

            if (configuration.MaxFrameBytes is { } frame)
            {
                // Every frame is measured against this before it is written into the message buffer.
                // Zero or less would refuse the first byte of every message forever, which is not a
                // limit — it is a conversation nobody could ever place.
                if (frame <= 0)
                {
                    throw Fail("maxFrameBytes", $"must be positive, and it is {frame}.");
                }

                options.MaxFrameBytes = frame;
            }

            if (configuration.IdleTimeoutSeconds is { } idle)
            {
                options.IdleTimeout = ToTimeSpan("idleTimeoutSeconds", idle);
            }

            if (configuration.CloseTimeoutSeconds is { } close)
            {
                options.CloseTimeout = ToTimeSpan("closeTimeoutSeconds", close);
            }

            options.Voice = VoiceOptionsBinder.Build(configuration);

            return options;
        }

        /// <summary>Turns whole seconds from the document into the span a bounded wait accepts.</summary>
        /// <param name="field">The <c>providers.conversation</c> field the value came from, for the pointer.</param>
        /// <param name="seconds">The whole seconds the document wrote.</param>
        /// <returns>The span, or <see cref="Timeout.InfiniteTimeSpan"/> for <c>-1</c>.</returns>
        private static TimeSpan ToTimeSpan(string field, int seconds)
        {
            if (seconds == -1)
            {
                return Timeout.InfiniteTimeSpan;
            }

            if (seconds < 0)
            {
                throw Fail(field, $"must be -1 for never, or zero or more seconds, and it is {seconds}.");
            }

            TimeSpan value = TimeSpan.FromSeconds(seconds);
            return value <= BoundedTimerLimits.MaximumBoundedDelay
                ? value
                : throw Fail(
                    field,
                    $"is {seconds} seconds, which Task.Delay, CancelAfter, and Task.WaitAsync all "
                    + $"refuse at run time. The longest a timer can hold is {BoundedTimerLimits.MaximumBoundedDelay}.");
        }

        /// <summary>Builds the one failure every check here raises, pointed at the field.</summary>
        /// <param name="field">The <c>providers.conversation</c> field that is wrong.</param>
        /// <param name="problem">What is wrong with it, in the words a reader can act on.</param>
        /// <returns>The failure to throw.</returns>
        private static ConfigurationLoadException Fail(string field, string problem)
        {
            return new(new ConfigurationError
            {
                Pointer = $"/providers/conversation/{field}",
                Message = $"providers.conversation.{field} {problem}",
                Check = ConfigurationCheck.ReferenceResolution,
            });
        }
    }
}

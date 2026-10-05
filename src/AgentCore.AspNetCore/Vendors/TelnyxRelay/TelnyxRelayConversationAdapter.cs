using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Ports;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// The Telnyx Conversation Relay as a conversation transport: it owns the socket and speaks Telnyx frames.
    /// </summary>
    /// <param name="secrets">
    /// Reads the host's secret resolver when the route is built, or <see langword="null"/> to read
    /// <see cref="KnownSecrets.TelnyxRelayKeyVariable"/> from the environment only.
    /// </param>
    public sealed class TelnyxRelayConversationAdapter(Func<ISecretResolverPort?>? secrets = null) : IConversationTransportAdapter
    {
        /// <summary>The query parameter the socket URL carries the shared key in.</summary>
        public const string KeyParameter = "key";

        /// <summary>The one <c>providers.conversation.kind</c> value this vendor answers to.</summary>
        public const string TelnyxRelayKind = "telnyx-relay";

        /// <inheritdoc/>
        public string Kind => TelnyxRelayKind;

        /// <inheritdoc/>
        public bool CarriesText => true;

        /// <inheritdoc/>
        public ConversationRoute CreateRoute(ConversationProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TelnyxRelayOptions options = BuildOptions(configuration);

            // Telnyx signs no relay connect and sends no auth header, so the caller proves itself with a shared key in
            // the socket URL: wss://host/v1/{entry}/call?key=…, set in the TeXML verb.
            Task<byte[]> key = ResolveKeyAsync(secrets?.Invoke());
            return new ConversationRoute(
                http => TelnyxRelayEndpointRouteBuilderExtensions.HandleAsync(http, options),
                http => CarriesKeyAsync(http, key))
            {
                Ready = key,
            };
        }

        private static async Task<byte[]> ResolveKeyAsync(ISecretResolverPort? resolver)
        {
            string key = await resolver.RequireAsync(
                KnownSecrets.TelnyxRelay, "Telnyx signs no relay socket, so this shared key is the route's only check of its caller.").ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(key);
        }

        // Fixed time, so how long a refusal takes says nothing about how much of the key was right.
        private static async ValueTask<bool> CarriesKeyAsync(HttpContext http, Task<byte[]> key)
        {
            byte[] expected = await key.ConfigureAwait(false);
            byte[] given = Encoding.UTF8.GetBytes(http.Request.Query[KeyParameter].ToString());
            return CryptographicOperations.FixedTimeEquals(given, expected);
        }

        /// <summary>Turns the document's limits into the options the endpoint runs on.</summary>
        /// <param name="configuration">The <c>providers.conversation</c> block.</param>
        /// <returns>The options, with the shipped default kept for every value the document omits.</returns>
        /// <exception cref="ConfigurationLoadException">
        /// A value would be refused at run time by <c>Task.Delay</c>, <c>CancelAfter</c>, or
        /// <c>Task.WaitAsync</c>, or a frame cap is not positive. The pointer names the exact field.
        /// </exception>
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

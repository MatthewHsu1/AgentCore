using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive
{
    /// <summary>
    /// The <c>providers.conversation.live</c> block, read and checked at boot so a typo fails the start and not the first
    /// caller. Every word GPT-Live hears comes from here, because AgentCore holds no prompt text of its own.
    /// </summary>
    /// <param name="Instructions">GPT-Live's whole prompt, sent as written.</param>
    /// <param name="Voice">The voice GPT-Live speaks with.</param>
    /// <param name="Model">The GPT-Live model.</param>
    /// <param name="Greeting">
    /// The instruction GPT-Live gets once the call is up, such as "Greet the caller as your instructions say", or
    /// <see langword="null"/>: GPT-Live then stays silent until the caller speaks.
    /// </param>
    /// <param name="TransferSeconds">
    /// How long a transfer may take before it counts as failed. OpenAI never reports the outcome, so only the peer's
    /// hang-up of the AI leg says it worked; this must be longer than the peer rings the target.
    /// </param>
    internal sealed record OpenAiLiveSettings(string Instructions, string Voice, string Model, string? Greeting = null, int TransferSeconds = OpenAiLiveSettings.DefaultTransferSeconds)
    {
        internal const int DefaultTransferSeconds = 45;

        internal static readonly TimeSpan DefaultTransferWait = TimeSpan.FromSeconds(DefaultTransferSeconds);

        internal TimeSpan TransferWait => TimeSpan.FromSeconds(TransferSeconds);

        internal const string DefaultVoice = "marin";

        internal const string DefaultModel = "gpt-live-1";

        private const string Pointer = "/providers/conversation/live";

        /// <exception cref="ConfigurationLoadException">The block is missing, lacks instructions, or names a key it does not hold.</exception>
        internal static OpenAiLiveSettings From(ConversationProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            if (configuration.Live.ValueKind != JsonValueKind.Object)
            {
                throw Fail(string.Empty, "providers.conversation.live is missing. The openai-live vendor needs live.instructions: GPT-Live's own prompt.");
            }

            string? instructions = null;
            string? voice = null;
            string? model = null;
            string? greeting = null;
            int? transferSeconds = null;
            
            foreach (JsonProperty property in configuration.Live.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "instructions":
                        instructions = Text(property);
                        break;
                    case "voice":
                        voice = Text(property);
                        break;
                    case "model":
                        model = Text(property);
                        break;
                    case "greeting":
                        greeting = Text(property);
                        break;
                    case "transferSeconds":
                        transferSeconds = Seconds(property);
                        break;
                    default:
                        throw Fail(
                            "/" + property.Name,
                            $"providers.conversation.live.{property.Name} is not a setting of the openai-live vendor. It reads instructions, greeting, voice, model, and transferSeconds. "
                            + $"The OpenAI key and the webhook secret come from the secret resolver as '{KnownSecrets.OpenAiApiKeyName}' and '{KnownSecrets.OpenAiWebhookSecretName}'.");
                }
            }

            return string.IsNullOrWhiteSpace(instructions)
                ? throw Fail("/instructions", "providers.conversation.live.instructions is missing or empty. GPT-Live needs its own prompt.")
                : new OpenAiLiveSettings(instructions, voice ?? DefaultVoice, model ?? DefaultModel, greeting, transferSeconds ?? DefaultTransferSeconds);
        }

        private static string Text(JsonProperty property)
        {
            return property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } text
                ? text
                : throw Fail("/" + property.Name, $"providers.conversation.live.{property.Name} must be a non-empty string.");
        }

        private static int Seconds(JsonProperty property)
        {
            return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int seconds) && seconds > 0
                ? seconds
                : throw Fail("/" + property.Name, $"providers.conversation.live.{property.Name} must be a whole number of seconds above 0.");
        }

        private static ConfigurationLoadException Fail(string suffix, string message)
        {
            return new(new ConfigurationError
            {
                Pointer = Pointer + suffix,
                Message = message,
                Check = ConfigurationCheck.ReferenceResolution,
            });
        }
    }
}

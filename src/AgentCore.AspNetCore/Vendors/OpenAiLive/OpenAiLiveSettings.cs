using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive
{
    /// <summary>The <c>providers.conversation.live</c> block, read and checked at boot.</summary>
    /// <param name="Instructions">GPT-Live's own prompt: tone and greeting.</param>
    /// <param name="Voice">The voice GPT-Live speaks with.</param>
    /// <param name="Model">The GPT-Live model.</param>
    internal sealed record OpenAiLiveSettings(string Instructions, string Voice, string Model)
    {
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
                    default:
                        throw Fail(
                            "/" + property.Name,
                            $"providers.conversation.live.{property.Name} is not a setting of the openai-live vendor. It reads instructions, voice, and model. "
                            + $"The OpenAI key and the webhook secret come from the secret resolver as '{KnownSecrets.OpenAiApiKeyName}' and '{KnownSecrets.OpenAiWebhookSecretName}'.");
                }
            }

            return string.IsNullOrWhiteSpace(instructions)
                ? throw Fail("/instructions", "providers.conversation.live.instructions is missing or empty. GPT-Live needs its own prompt.")
                : new OpenAiLiveSettings(instructions, voice ?? DefaultVoice, model ?? DefaultModel);
        }

        private static string Text(JsonProperty property)
        {
            return property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } text
                ? text
                : throw Fail("/" + property.Name, $"providers.conversation.live.{property.Name} must be a non-empty string.");
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

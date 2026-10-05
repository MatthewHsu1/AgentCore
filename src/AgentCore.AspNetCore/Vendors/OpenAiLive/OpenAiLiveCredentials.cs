using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive
{
    /// <summary>The OpenAI key (accept, attach, hang up) and the webhook signing key.</summary>
    /// <param name="ApiKey">The OpenAI API key.</param>
    /// <param name="WebhookKey">The decoded webhook signing key.</param>
    internal sealed record OpenAiLiveCredentials(string ApiKey, byte[] WebhookKey)
    {
        /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds one of the two, or the webhook secret is not a valid signing secret.</exception>
        internal static async Task<OpenAiLiveCredentials> ResolveAsync(ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        {
            string apiKey = await secrets.RequireAsync(
                KnownSecrets.OpenAi, "The openai-live vendor accepts, attaches to, and hangs up each call with it.", cancellationToken).ConfigureAwait(false);
            string webhook = await secrets.RequireAsync(
                KnownSecrets.OpenAiWebhook, "The openai-live vendor checks the signature of every incoming-call webhook with it.", cancellationToken).ConfigureAwait(false);
            try
            {
                return new OpenAiLiveCredentials(apiKey, StandardWebhookSignature.KeyOf(webhook));
            }
            catch (FormatException failure)
            {
                throw new SecretResolutionException(
                    $"The secret '{KnownSecrets.OpenAiWebhookSecretName}' (variable {KnownSecrets.OpenAiWebhookSecretVariable}) is not a Standard Webhooks signing secret: "
                    + "it must be 'whsec_' followed by a base64 key.",
                    failure);
            }
        }
    }
}

using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The openai-live credentials come from the secret resolver, and a bad signing secret fails at boot naming the secret, never its value.</summary>
    public sealed class OpenAiLiveCredentialsTests
    {
        private const string ApiKey = "sk-test-key";

        [Fact]
        public async Task BothSecretsResolveAndTheSigningSecretIsDecoded()
        {
            MapSecretResolver secrets = new MapSecretResolver()
                .With(KnownSecrets.OpenAiApiKeyName, ApiKey)
                .With(KnownSecrets.OpenAiWebhookSecretName, "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");

            OpenAiLiveCredentials credentials = await OpenAiLiveCredentials.ResolveAsync(secrets, TestContext.Current.CancellationToken);

            Assert.Equal(ApiKey, credentials.ApiKey);
            Assert.Equal(Convert.FromBase64String("MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"), credentials.WebhookKey);
        }

        [Fact]
        public async Task ABadSigningSecretFailsNamingTheSecretAndNotItsValue()
        {
            const string badValue = "whsec_not*base64*at-all";
            MapSecretResolver secrets = new MapSecretResolver()
                .With(KnownSecrets.OpenAiApiKeyName, ApiKey)
                .With(KnownSecrets.OpenAiWebhookSecretName, badValue);

            SecretResolutionException failure = await Assert.ThrowsAsync<SecretResolutionException>(
                () => OpenAiLiveCredentials.ResolveAsync(secrets, TestContext.Current.CancellationToken));

            Assert.Contains(KnownSecrets.OpenAiWebhookSecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(KnownSecrets.OpenAiWebhookSecretVariable, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(badValue, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("not*base64", failure.Message, StringComparison.Ordinal);
        }
    }
}

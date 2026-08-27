using AgentCore.TestSupport;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Xunit;

namespace AgentCore.Application.Tests.Secrets;

/// <summary>
/// The optional read: a credential whose absence picks a path rather than failing one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SecretResolverExtensions.RequireAsync"/> answers the case where a host cannot start
/// without a key. <see cref="SecretResolverExtensions.TryReadAsync"/> answers the other case, and
/// <see cref="KnownSecrets.GrafanaCloudInstanceId"/> is what asked for it: no instance id means
/// export to a collector with no credential, and an instance id makes the token beside it mandatory.
/// </para>
/// <para>
/// These tests pin two things. The optional read walks the same two places in the same order as the
/// required one, and the two agree on what a blank answer means, because the required path is now
/// written in terms of this one and a change to either would otherwise pass unnoticed.
/// </para>
/// <para>
/// Every test that writes an environment variable owns a name of its own and puts back whatever it
/// found, so one test never decides the answer of another.
/// </para>
/// </remarks>
public sealed class SecretOptionalReadTests
{
    private const string SecretValue = "grafana-instance-123456";
    private const string OtherValue = "grafana-instance-999999";

    [Fact]
    public async Task TheChain_AnswersBeforeTheEnvironment()
    {
        SecretName secret = new("grafana-cloud-instance-id", "AGENTCORE_TEST_TRYREAD_CHAIN_FIRST");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, OtherValue);

        try
        {
            MapSecretResolver resolver = new MapSecretResolver().With(secret.Name, SecretValue);

            var value = await resolver.TryReadAsync(secret, TestContext.Current.CancellationToken);

            // Same order as the required read. A bound resolver is the deployment saying where its
            // secrets live, so the variable is a fallback and never an override.
            Assert.Equal(SecretValue, value);
            Assert.Equal([secret.Name], resolver.Asked);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task TheVariable_AnswersWhenTheChainHoldsNothing()
    {
        SecretName secret = new("grafana-cloud-instance-id", "AGENTCORE_TEST_TRYREAD_VARIABLE");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, SecretValue);

        try
        {
            MapSecretResolver resolver = new();

            var value = await resolver.TryReadAsync(secret, TestContext.Current.CancellationToken);

            Assert.Equal(SecretValue, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task NeitherPlace_AnswersNullRatherThanThrowing()
    {
        SecretName secret = new("grafana-cloud-instance-id", "AGENTCORE_TEST_TRYREAD_ABSENT");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, null);

        try
        {
            MapSecretResolver resolver = new();

            var value = await resolver.TryReadAsync(secret, TestContext.Current.CancellationToken);

            // This is the whole point of the method. A host that names no Grafana instance exports
            // with no credential, and that is an answer rather than a failure.
            Assert.Null(value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task ANullResolver_ReadsTheEnvironmentAlone()
    {
        SecretName secret = new("grafana-cloud-instance-id", "AGENTCORE_TEST_TRYREAD_NO_CHAIN");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, SecretValue);

        try
        {
            ISecretResolverPort? none = null;

            var value = await none.TryReadAsync(secret, TestContext.Current.CancellationToken);

            // A host that binds no chain is an ordinary case, so the extension takes the null rather
            // than making each caller test for it.
            Assert.Equal(SecretValue, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task ABlankChainAnswer_IsHeldAndEmpty_NotAMiss()
    {
        SecretName secret = new("grafana-cloud-instance-id", "AGENTCORE_TEST_TRYREAD_BLANK");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, OtherValue);

        try
        {
            MapSecretResolver resolver = new MapSecretResolver().With(secret.Name, string.Empty);

            var value = await resolver.TryReadAsync(secret, TestContext.Current.CancellationToken);

            // The empty string is returned rather than swallowed, so the caller can tell "the store
            // held this name and it is empty" from "nowhere holds it". Falling back to the variable
            // here would start the host on some other key the deployment never named.
            Assert.Equal(string.Empty, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task TheRequiredRead_StillFailsOnTheBlankTheOptionalReadReturns()
    {
        SecretName secret = new("grafana-cloud-api-token", "AGENTCORE_TEST_TRYREAD_BLANK_REQUIRED");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, OtherValue);

        try
        {
            MapSecretResolver resolver = new MapSecretResolver().With(secret.Name, string.Empty);

            // RequireAsync is now written in terms of TryReadAsync. This is the test that fails if
            // that refactor ever changes what a blank answer means on the required path: a mounted
            // secret file that renders empty must stop the host, not quietly read the variable.
            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await resolver.RequireAsync(
                    secret,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains(secret.Name, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherValue, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public void TheGrafanaCredential_IsNamedInBothPlaces()
    {
        // A pair with a blank half would ask the chain for nothing and fall back to nothing, and the
        // failure would arrive as a missing key rather than as the typo it is.
        Assert.Equal(KnownSecrets.GrafanaCloudInstanceIdName, KnownSecrets.GrafanaCloudInstanceId.Name);
        Assert.Equal(KnownSecrets.GrafanaCloudInstanceIdVariable, KnownSecrets.GrafanaCloudInstanceId.VariableName);
        Assert.Equal(KnownSecrets.GrafanaCloudApiTokenName, KnownSecrets.GrafanaCloudApiToken.Name);
        Assert.Equal(KnownSecrets.GrafanaCloudApiTokenVariable, KnownSecrets.GrafanaCloudApiToken.VariableName);

        // The two halves are one credential and must not collide with each other or with a vendor
        // key that already exists.
        Assert.NotEqual(KnownSecrets.GrafanaCloudInstanceId, KnownSecrets.GrafanaCloudApiToken);
        Assert.NotEqual(KnownSecrets.GrafanaCloudApiToken, KnownSecrets.OpenAi);
    }
}

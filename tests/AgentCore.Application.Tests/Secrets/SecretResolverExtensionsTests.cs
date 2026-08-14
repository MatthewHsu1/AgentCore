using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tests.Secrets.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Secrets;

/// <summary>
/// The three steps every vendor adapter used to write for itself: the chain, the variable, the fail.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISecretResolverPort"/> answers <see langword="null"/> for a name it does not hold, so
/// something has to decide what a miss means for a credential a host cannot start without.
/// <see cref="SecretResolverExtensions.RequireAsync"/> is that decision, and these tests pin the
/// order of the two places it reads and the shape of the failure when neither holds anything.
/// </para>
/// <para>
/// Every test that writes an environment variable owns a name of its own and puts back whatever it
/// found, so one test never decides the answer of another.
/// </para>
/// </remarks>
public sealed class SecretResolverExtensionsTests
{
    private const string SecretValue = "sk-live-0123456789";
    private const string OtherValue = "sk-live-9876543210";

    // ---------------------------------------------------------------------------------------------
    // The chain answers first.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheChain_AnswersBeforeTheEnvironment()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_CHAIN_FIRST");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, OtherValue);

        try
        {
            MapSecretResolver resolver = new(MapSecretResolver.Secret(secret.Name, SecretValue));

            var value = await resolver.RequireAsync(
                secret,
                cancellationToken: TestContext.Current.CancellationToken);

            // A bound resolver is the deployment saying where its secrets live, so the variable is a
            // fallback and never an override.
            Assert.Equal(SecretValue, value);
            Assert.Equal([secret.Name], resolver.Asked);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The variable answers the miss.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AChainThatHoldsNothing_FallsBackToTheVariable()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_CHAIN_MISS");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, SecretValue);

        try
        {
            MapSecretResolver resolver = new();

            var value = await resolver.RequireAsync(
                secret,
                cancellationToken: TestContext.Current.CancellationToken);

            // The chain was asked, said nothing, and the variable answered.
            Assert.Equal(SecretValue, value);
            Assert.Equal([secret.Name], resolver.Asked);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task AChainThatAnswersABlankString_FailsAndNeverReadsTheVariable()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_BLANK_ANSWER");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, SecretValue);

        try
        {
            // A store that answers the empty string held the name: a mounted file that renders
            // empty is a broken deployment and never a miss. Reading the variable instead would
            // start the host on a key it was never told to use, and nothing would report it.
            MapSecretResolver resolver = new(MapSecretResolver.Secret(secret.Name, string.Empty));

            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await resolver.RequireAsync(
                    secret,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains(secret.Name, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task NoChainAtAll_ReadsTheVariableAlone()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_NO_CHAIN");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, SecretValue);

        try
        {
            // A host that binds no resolver is an ordinary case, so the null is answered here rather
            // than at every call site.
            var value = await SecretResolverExtensions.RequireAsync(
                null,
                secret,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SecretValue, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Failing at startup, and never with a value in the message.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task NoKeyAnywhere_FailsAndNamesBothPlacesToPutOne()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_NOWHERE");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, null);

        try
        {
            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await new MapSecretResolver().RequireAsync(
                    secret,
                    cancellationToken: TestContext.Current.CancellationToken));

            // The reader has two places to put a key, so the message names both of them.
            Assert.Contains(secret.Name, failure.Message, StringComparison.Ordinal);
            Assert.Contains(secret.VariableName, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task AVariableSetToNothing_FailsLikeAnUnsetOne()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_EMPTY_VARIABLE");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, string.Empty);

        try
        {
            // An exported variable with nothing in it is a deployment that meant to set one and did
            // not. It fails at startup, where the message can still say what to do about it.
            await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await new MapSecretResolver().RequireAsync(
                    secret,
                    cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task TheFailure_SaysWhatTheKeyWasNeededFor()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_BECAUSE");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);
        Environment.SetEnvironmentVariable(secret.VariableName, null);

        try
        {
            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await new MapSecretResolver().RequireAsync(
                    secret,
                    "The zilliz store embeds every query with text-embedding-3-small.",
                    TestContext.Current.CancellationToken));

            // A host that never asked for an embedding model learns why one was asking for a key.
            Assert.Contains("text-embedding-3-small", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    [Fact]
    public async Task NoFailureMessage_EverHoldsAValue()
    {
        SecretName secret = new("orders-api-key", "AGENTCORE_TEST_NO_LEAK");
        var saved = Environment.GetEnvironmentVariable(secret.VariableName);

        // The variable holds a key, and the chain is asked for a name nothing holds. The failure
        // travels through a log, an alert, and a support ticket, so it carries neither value.
        Environment.SetEnvironmentVariable(secret.VariableName, SecretValue);

        try
        {
            SecretName missing = new("missing-api-key", "AGENTCORE_TEST_NO_LEAK_UNSET");
            MapSecretResolver resolver = new(MapSecretResolver.Secret(secret.Name, OtherValue));

            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await resolver.RequireAsync(
                    missing,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.DoesNotContain(SecretValue, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(OtherValue, failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret.VariableName, saved);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The read is a read, and the caller still owns the token.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheChain_TakesTheCancellationTokenTheCallerGives()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new CancellingSecretResolver().RequireAsync(
                KnownSecrets.OpenAi,
                cancellationToken: source.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // The catalog and the pair.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheCatalog_PairsEachNameWithTheVariableOfItsVendor()
    {
        // Every adapter of a vendor reads the same two strings, and this is where they are written.
        Assert.Equal("openai-api-key", KnownSecrets.OpenAiApiKeyName);
        Assert.Equal("OPENAI_API_KEY", KnownSecrets.OpenAiApiKeyVariable);
        Assert.Equal(new SecretName("openai-api-key", "OPENAI_API_KEY"), KnownSecrets.OpenAi);

        Assert.Equal("zilliz-api-key", KnownSecrets.ZillizApiKeyName);
        Assert.Equal("ZILLIZ_API_KEY", KnownSecrets.ZillizApiKeyVariable);
        Assert.Equal(new SecretName("zilliz-api-key", "ZILLIZ_API_KEY"), KnownSecrets.Zilliz);
    }

    [Theory]
    [InlineData("", "OPENAI_API_KEY")]
    [InlineData("  ", "OPENAI_API_KEY")]
    [InlineData("openai-api-key", "")]
    [InlineData("openai-api-key", "  ")]
    public void APairWithABlankHalf_IsRefusedWhereItIsWritten(string name, string variable)
    {
        // A blank half asks the chain for nothing, or falls back to nothing, and the failure would
        // then arrive as a missing key rather than as the typo it is.
        Assert.Throws<ArgumentException>(() => new SecretName(name, variable));
    }

    [Fact]
    public void ThePair_WritesBothNamesAndNoValue()
        => Assert.Equal("openai-api-key (OPENAI_API_KEY)", KnownSecrets.OpenAi.ToString());

    private sealed class CancellingSecretResolver : ISecretResolverPort
    {
        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(null);
        }
    }
}

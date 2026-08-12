using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tests.Configuration;
using AgentCore.Application.Tests.Secrets.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Secrets;

/// <summary>
/// The seam the parser leaves open: <c>${secret:name}</c> is a reference and never a value.
/// </summary>
/// <remarks>
/// <see cref="SecretTemplate"/> keeps the references, and <see cref="ResolvedSecrets"/> turns them
/// into values once, at startup. A tool call never reaches <see cref="ISecretResolverPort"/>.
/// </remarks>
public sealed class ResolvedSecretsTests
{
    private const string ApiKeyName = "orders-api-key";
    private const string ApiKeyValue = "sk-live-0123456789";

    // ---------------------------------------------------------------------------------------------
    // Resolving the whole document, once.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheWorkedExample_ResolvesItsOneSecret()
    {
        var document = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        MapSecretResolver resolver = new(MapSecretResolver.Secret(ApiKeyName, ApiKeyValue));

        var secrets = await ResolvedSecrets.ResolveAsync(
            document,
            resolver,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, secrets.Count);
        Assert.Equal([ApiKeyName], secrets.Names);
        Assert.True(secrets.Contains(ApiKeyName));

        var header = document.Tools.Single(tool => tool.Id == "lookup_order").Request!.Headers["Authorization"];
        Assert.Equal("Bearer " + ApiKeyValue, secrets.Format(header));
    }

    [Fact]
    public async Task OneName_ReadsTheResolverOnce_HoweverManyToolsReferenceIt()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: two-readers
            tools:
              - id: first
                kind: http
                request:
                  method: GET
                  url: "https://api.example.com/a"
                  headers: { Authorization: "Bearer ${secret:orders-api-key}", X-Trace: "${secret:orders-api-key}" }
              - id: second
                kind: http
                request:
                  method: GET
                  url: "https://api.example.com/b"
                  headers: { Authorization: "Bearer ${secret:orders-api-key}" }
            agents:
              items:
                - { id: only }
            """;

        MapSecretResolver resolver = new(MapSecretResolver.Secret(ApiKeyName, ApiKeyValue));

        var secrets = await ResolvedSecrets.ResolveAsync(
            ConfigurationLoader.LoadYaml(document),
            resolver,
            TestContext.Current.CancellationToken);

        // Three references, one name, one read. Startup pays for the read, and no call does.
        Assert.Equal([ApiKeyName], resolver.Asked);
        Assert.Equal(1, secrets.Count);
    }

    [Fact]
    public async Task ADocumentWithNoReference_ReadsNothing()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: no-secrets
            tools:
              - id: plain
                kind: http
                request:
                  method: GET
                  url: "https://api.example.com/a"
                  headers: { Accept: application/json }
            agents:
              items:
                - { id: only }
            """;

        MapSecretResolver resolver = new();

        var secrets = await ResolvedSecrets.ResolveAsync(
            ConfigurationLoader.LoadYaml(document),
            resolver,
            TestContext.Current.CancellationToken);

        Assert.Empty(resolver.Asked);
        Assert.Equal(0, secrets.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Failing at startup, and never in the middle of a call.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AnUnresolvableName_FailsAtStartupAndNamesThePointer()
    {
        var document = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        MapSecretResolver resolver = new();

        var failure = await Assert.ThrowsAsync<SecretResolutionException>(
            async () => await ResolvedSecrets.ResolveAsync(
                document,
                resolver,
                TestContext.Current.CancellationToken));

        Assert.Equal(ApiKeyName, failure.SecretName);
        Assert.Contains(ApiKeyName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("/tools/2/request/headers/Authorization", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingAnUnknownName_Fails()
    {
        var secrets = ResolvedSecrets.Create([MapSecretResolver.Secret(ApiKeyName, ApiKeyValue)]);
        var template = SecretTemplate.Parse("${secret:missing-name}");

        var failure = Assert.Throws<SecretResolutionException>(() => secrets.Format(template));

        Assert.Equal("missing-name", failure.SecretName);
    }

    // ---------------------------------------------------------------------------------------------
    // A value never reaches a log or a message.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void NoFailureMessage_EverHoldsAValue()
    {
        var secrets = ResolvedSecrets.Create([MapSecretResolver.Secret(ApiKeyName, ApiKeyValue)]);
        var template = SecretTemplate.Parse("Bearer ${secret:orders-api-key} and ${secret:missing-name}");

        var failure = Assert.Throws<SecretResolutionException>(() => secrets.Format(template));

        Assert.DoesNotContain(ApiKeyValue, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheResolvedSet_NeverWritesAValueInItsOwnText()
    {
        var secrets = ResolvedSecrets.Create([MapSecretResolver.Secret(ApiKeyName, ApiKeyValue)]);

        // A resolved set lands in a log line, a diagnostic dump, or a debugger tooltip sooner or
        // later. It reports how many names it holds, and never what they are worth.
        Assert.DoesNotContain(ApiKeyValue, secrets.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Formatting.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void ATemplateWithNoReference_FormatsToItself()
    {
        var template = SecretTemplate.Parse("application/json");

        Assert.Equal("application/json", ResolvedSecrets.Empty.Format(template));
    }

    [Fact]
    public void TwoReferencesInOneString_BothResolve()
    {
        var secrets = ResolvedSecrets.Create(
        [
            MapSecretResolver.Secret("left", "L"),
            MapSecretResolver.Secret("right", "R"),
        ]);

        Assert.Equal("<L|R>", secrets.Format(SecretTemplate.Parse("<${secret:left}|${secret:right}>")));
    }

    [Fact]
    public async Task TheResolver_TakesTheCancellationTokenTheCallerGives()
    {
        var document = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ResolvedSecrets.ResolveAsync(document, new CancellingSecretResolver(), source.Token));
    }

    private sealed class CancellingSecretResolver : ISecretResolverPort
    {
        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(null);
        }
    }
}

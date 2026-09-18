using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The new <c>shell:</c> keys: <c>env:</c> (with <c>${secret:name}</c>), <c>maxOutputKb:</c>, and the
/// Docker-only keys refused under <c>kind: local</c>.
/// </summary>
public sealed class ShellOptionsCompilerTests
{
    [Fact]
    public void Load_ShellEnvWithASecretReference_BindsTheReferenceAndLeavesAPlainValueAlone()
    {
        var configuration = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            agents:
              items:
                - id: only
                  instructions: "run commands"
                  shell: { kind: local, env: { CUSTSERVICE_DSN: "${secret:custservice-dsn}", PYTHONUNBUFFERED: "1" } }
            entries:
              main:
                agent: only
            """);

        var shell = Assert.Single(configuration.Agents!.Items).Shell;
        Assert.NotNull(shell);
        Assert.True(shell!.Env["CUSTSERVICE_DSN"].HasSecretReferences);
        Assert.Equal("custservice-dsn", Assert.Single(shell.Env["CUSTSERVICE_DSN"].References).Name);
        Assert.False(shell.Env["PYTHONUNBUFFERED"].HasSecretReferences);
    }

    [Fact]
    public void Build_ShellEnvAndMaxOutputKb_ResolveIntoTheCallShellOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentcore-shell-env-secret-" + Guid.NewGuid().ToString("N"));
        var item = new AgentConfiguration { Id = "only" };
        var shell = new ShellConfiguration
        {
            Kind = ShellKind.Local,
            MaxOutputKb = 64,
            Env = new Dictionary<string, SecretTemplate>(StringComparer.Ordinal)
            {
                ["CUSTSERVICE_DSN"] = SecretTemplate.Parse("${secret:custservice-dsn}"),
                ["PYTHONUNBUFFERED"] = SecretTemplate.Parse("1"),
            },
        };

        var context = new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi")))
        {
            WorkspaceRoot = root,
            Secrets = ResolvedSecrets.Create([new KeyValuePair<string, string>("custservice-dsn", "postgres://x")]),
        };

        var options = AgentShellOptionsCompiler.Build(item, shell, context, "/agents/items/0");

        Assert.Equal("postgres://x", options.Env!["CUSTSERVICE_DSN"]);
        Assert.Equal("1", options.Env!["PYTHONUNBUFFERED"]);
        Assert.Equal(64 * 1024, options.MaxOutputBytes);
    }

    [Fact]
    public void Compile_ShellWithADockerOnlyKeyUnderKindLocal_FailsNamingTheAgentAndTheKey()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            agents:
              items:
                - id: only
                  instructions: "run commands"
                  shell: { kind: local, image: "ghcr.io/x/y:1.0" }
            entries:
              main:
                agent: only
            """);

        var root = Path.Combine(Path.GetTempPath(), "agentcore-shell-docker-only-" + Guid.NewGuid().ToString("N"));

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi")))
            {
                WorkspaceRoot = root,
            })["main"]);

        Assert.Equal("/agents/items/0/shell", failure.Pointer);
        Assert.Contains("'only'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("image:", failure.Message, StringComparison.Ordinal);
    }
}

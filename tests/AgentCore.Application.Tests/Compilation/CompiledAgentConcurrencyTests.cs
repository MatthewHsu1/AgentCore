using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Tests.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Rule 16 of section 11, and T44. A fan-out of 26 simultaneous calls against one compiled agent is
/// clean, and no code path compiles an agent for each call.
/// </summary>
public sealed class CompiledAgentConcurrencyTests
{
    private const int FanOut = 26;

    [Fact]
    public async Task Rule16_TwentySixSimultaneousCalls_ShareOneCompiledAgent()
    {
        using ScriptedChatClient client = new("Hello", " from", " one", " agent.");
        var document = ConfigurationLoader.LoadYaml(CompileTableTests.OneAgentYaml);
        AgentCompilationContext context = new(new FakeChatClientFactory(client));
        CompiledAgentRegistry registry = new();

        var token = TestContext.Current.CancellationToken;
        using Barrier gate = new(FanOut);

        var runs = Enumerable.Range(0, FanOut).Select(index => Task.Run(
            async () =>
            {
                // Every call asks the registry for the agent, exactly as the turn loop does.
                var compiled = registry.GetOrCompile(document, context);

                // Nothing starts until all 26 are ready, so the fan-out is really simultaneous.
                gate.SignalAndWait(token);

                var reply = await compiled.Agent.RunAsync($"call {index}", cancellationToken: token)
                    .ConfigureAwait(false);
                return (compiled, reply.Text);
            },
            token));

        var results = await Task.WhenAll(runs);

        // No code path compiles an agent for each call. The compiled agent is a process singleton.
        Assert.Equal(1, registry.CompileCount);
        Assert.All(results, result => Assert.Same(results[0].compiled, result.compiled));
        Assert.All(results, result => Assert.Equal(client.FullText, result.Text));
        Assert.Equal(FanOut, client.Calls);
    }

    [Fact]
    public async Task Rule16_TwentySixSimultaneousCalls_AreCleanAgainstOneGraphWrapper()
    {
        using ScriptedChatClient client = new("Hello", " from", " the", " graph.");
        var document = ConfigurationLoader.LoadYaml(CompileTableTests.ExplicitGraphYaml);
        AgentCompilationContext context = new(new FakeChatClientFactory(client));
        CompiledAgentRegistry registry = new();

        var token = TestContext.Current.CancellationToken;
        var compiled = registry.GetOrCompile(document, context);
        using Barrier gate = new(FanOut);

        var runs = Enumerable.Range(0, FanOut).Select(index => Task.Run(
            async () =>
            {
                gate.SignalAndWait(token);
                var reply = await compiled.Agent.RunAsync($"call {index}", cancellationToken: token)
                    .ConfigureAwait(false);
                return reply.Text;
            },
            token));

        var texts = await Task.WhenAll(runs);

        // T44: nothing serializes inside the AsAIAgent() wrapper, so one wrapper serves every call.
        Assert.Equal(1, registry.CompileCount);
        Assert.All(texts, text => Assert.Contains("graph.", text, StringComparison.Ordinal));
    }

    [Fact]
    public void Rule16_TheRegistryCompilesOncePerDocument()
    {
        using ScriptedChatClient client = new("ok");
        var document = ConfigurationLoader.LoadYaml(CompileTableTests.OneAgentYaml);
        AgentCompilationContext context = new(new FakeChatClientFactory(client));
        CompiledAgentRegistry registry = new();

        var first = registry.GetOrCompile(document, context);
        var second = registry.GetOrCompile(document, context);

        Assert.Same(first, second);
        Assert.Equal(1, registry.CompileCount);
    }
}

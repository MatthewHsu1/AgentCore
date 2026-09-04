using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// K42's strip: <see cref="AuditingFunctionInvokingChatClient.InvokeFunctionAsync"/> removes
/// <see cref="TurnAmbients.Clarifications"/> from the ambient for the duration of a <em>nested</em>
/// tool call, and leaves it alone on the outermost one.
/// </summary>
/// <remarks>
/// The first two facts drive <see cref="AuditingFunctionInvokingChatClient"/> directly, the same way
/// <see cref="AuditingFunctionInvokingChatClientRenderTests"/> does, with the ambient hand-rolled
/// through <see cref="TurnAmbients.Amend"/>. The third drives a real <see cref="CallSession"/> turn,
/// because that is the only place that proves <c>CallSession.EnterAmbients</c> actually threads its
/// own <c>Clarifications</c> instance through — a fact the first two cannot see, since they open the
/// ambient by hand.
/// </remarks>
public sealed class AuditingFunctionInvokingChatClientStripTests
{
    [Fact]
    public async Task TheCallersOwnToolCall_SeesTheHolder()
    {
        var clarifications = new Clarifications();
        Clarifications? seen = null;

        var tool = AIFunctionFactory.Create(
            () =>
            {
                seen = TurnAmbients.Current?.Clarifications;
                return "done.";
            },
            "search_tool",
            "Reads the ambient.");

        using var scope = TurnAmbients.Amend(ambients => ambients with { Clarifications = clarifications });

        ToolCallingChatClient inner = new("the loop continues.");
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "search")], options, TestContext.Current.CancellationToken);

        Assert.Same(clarifications, seen);
    }

    [Fact]
    public async Task ANestedToolCall_HasNoHolder_WhileTheOuterCallStillDoes()
    {
        // The outer tool's own invocation reads the ambient before starting the nested loop, and the
        // inner tool reads it from inside that nested loop's own InvokeFunctionAsync. The two reads
        // must disagree: K42's strip is conditional on being nested, not on being any tool call at
        // all, or it would blind the caller's own search too.
        var clarifications = new Clarifications();
        Clarifications? seenOutside = null;
        Clarifications? seenNested = null;

        var innerTool = AIFunctionFactory.Create(
            () =>
            {
                seenNested = TurnAmbients.Current?.Clarifications;
                return "inner done.";
            },
            "inner_tool",
            "Reads the ambient from inside a nested loop.");

        var outerTool = AIFunctionFactory.Create(
            async () =>
            {
                seenOutside = TurnAmbients.Current?.Clarifications;

                ToolCallingChatClient innerModel = new("nested done.");
                using AuditingFunctionInvokingChatClient innerClient = new(innerModel);
                ChatOptions innerOptions = new() { Tools = [innerTool] };
                await innerClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "go")], innerOptions, TestContext.Current.CancellationToken);

                return "outer done.";
            },
            "outer_tool",
            "Runs a nested loop.");

        using var scope = TurnAmbients.Amend(ambients => ambients with { Clarifications = clarifications });

        ToolCallingChatClient outerModel = new("done.");
        using AuditingFunctionInvokingChatClient outerClient = new(outerModel);
        ChatOptions outerOptions = new() { Tools = [outerTool] };

        await outerClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], outerOptions, TestContext.Current.CancellationToken);

        Assert.Same(clarifications, seenOutside);
        Assert.Null(seenNested);
    }

    // -------------------------------------------------------------------------------------------
    // The wiring fact: a real CallSession turn opens its own Clarifications and threads it through
    // EnterAmbients, so the model's own (outermost) knowledge search sees it, exactly as the two
    // hand-rolled facts above predict once CallSession is actually driving.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task RunTurnAsync_AToolModeKnowledgeSearch_SeesTheHolder()
    {
        const string yaml = """
            apiVersion: agentcore/v1
            name: clarifications-through-callsession
            agents:
              items:
                - id: only
                  instructions: "answer the caller"
                  knowledge: { mode: tool, scoped: false }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: only }
            """;

        var port = new StubKnowledgePort([Card("a")]);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(
                new ToolCallingChatClient(
                    "done.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["userQuestion"] = "what is it" })))
            {
                Knowledge = port,
            });

        var factory = new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        var session = factory.Create("call-strip-1");

        await session.RunTurnAsync("what is it", TestContext.Current.CancellationToken);

        Assert.Equal(1, port.Calls);
        Assert.NotNull(port.ClarificationsAtTheStore);
    }

    private static KnowledgeCard Card(string id)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };
}

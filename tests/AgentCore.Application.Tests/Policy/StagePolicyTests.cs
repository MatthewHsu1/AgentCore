using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Policy;
using AgentCore.Application.Tests.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Policy;

/// <summary>
/// The stage machine of row 2 of the section 8.2 compile table.
/// </summary>
public sealed class StagePolicyTests
{
    private const string StayDocument =
        """
        apiVersion: agentcore/v1
        name: stay
        state:
          ready: { type: boolean, default: false, writer: extractor }
        guards:
          isReady: { var: ready }
        agents:
          items:
            - { id: first }
            - { id: second }
        policy:
          initial: one
          stages:
            - id: one
              agent: first
              to: [ { stage: two, when: isReady } ]
            - id: two
              agent: second
              terminal: true
        """;

    private const string ErrorDocument =
        """
        apiVersion: agentcore/v1
        name: reject
        state:
          ready: { type: boolean, default: false, writer: extractor }
        guards:
          isReady: { var: ready }
        agents:
          items:
            - { id: first }
            - { id: second }
        policy:
          initial: one
          stages:
            - id: one
              agent: first
              onNoMatch: error
              to: [ { stage: two, when: isReady } ]
            - id: two
              agent: second
              terminal: true
        """;

    private const string OverlapDocument =
        """
        apiVersion: agentcore/v1
        name: overlap
        state:
          ready: { type: boolean, default: false, writer: extractor }
        guards:
          isReady:   { var: ready }
          alsoReady: { "!": { "!": { var: ready } } }
        agents:
          items:
            - { id: first }
            - { id: left }
            - { id: right }
        policy:
          initial: one
          stages:
            - id: one
              agent: first
              to:
                - { stage: two,   when: isReady }
                - { stage: three, when: alsoReady }
            - id: two
              agent: left
              terminal: true
            - id: three
              agent: right
              terminal: true
        """;

    [Fact]
    public void ZeroGuardsTrue_KeepsTheStageWhereItIs()
    {
        var (policy, _) = Build(StayDocument);

        // Zero guards true is the normal case, and it matches OnUnhandledTrigger.
        Assert.Equal("one", policy.Advance(Snapshot(ready: false)));
        Assert.Equal("one", policy.Advance(Snapshot(ready: false)));
    }

    [Fact]
    public void OneGuardTrue_MovesToThatStage()
    {
        var (policy, _) = Build(StayDocument);

        Assert.Equal("two", policy.Advance(Snapshot(ready: true)));
        Assert.True(policy.IsTerminal);
        Assert.Equal("second", policy.CurrentAgentId);
    }

    [Fact]
    public void ATerminalStage_StaysTerminal()
    {
        var (policy, _) = Build(StayDocument);

        policy.Advance(Snapshot(ready: true));
        Assert.Equal("two", policy.Advance(Snapshot(ready: true)));
    }

    [Fact]
    public void OnNoMatchError_RejectsTheZeroGuardsTrueCase()
    {
        var (policy, _) = Build(ErrorDocument);

        var failure = Assert.Throws<InvalidOperationException>(() => policy.Advance(Snapshot(ready: false)));

        Assert.Contains("onNoMatch: error", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSiblingGuardsTrue_Throws()
    {
        var (policy, _) = Build(OverlapDocument);

        // Stateless throws on the first overlap. Check 5 of section 8.5 finds every one at startup.
        Assert.ThrowsAny<InvalidOperationException>(() => policy.Advance(Snapshot(ready: true)));
    }

    [Fact]
    public void AGuardThatThrows_IsFalseAndTheCallContinues()
    {
        var (policy, guards) = Build(
            """
            apiVersion: agentcore/v1
            name: throwing
            state:
              count: { type: integer, default: 0, writer: counter, increment: { var: count } }
            guards:
              broken: { "!!": { var: count } }
            agents:
              items:
                - { id: first }
                - { id: second }
            policy:
              initial: one
              stages:
                - id: one
                  agent: first
                  to: [ { stage: two, when: broken } ]
                - id: two
                  agent: second
                  terminal: true
            """);

        var snapshot = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["count"] = JsonValue.Create(1),
        };

        // The rule is the operand shorthand the library refuses at run time, and check 4 cannot see
        // every such case. Section 8.7: log once, treat the guard as false, and continue.
        Assert.Equal("one", policy.Advance(snapshot));
        Assert.Equal(1, guards.Failures);
    }

    [Fact]
    public void AnUndeclaredInitialStage_Throws()
    {
        AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(StayDocument);
        PolicyConfiguration broken = document.Policy! with { Initial = "nowhere" };

        Assert.Throws<ArgumentException>(() => new StagePolicy(broken, new TestGuardEvaluator(document)));
    }

    private static (StagePolicy Policy, TestGuardEvaluator Guards) Build(string yaml)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        TestGuardEvaluator guards = new(document);
        return (new StagePolicy(document.Policy!, guards), guards);
    }

    private static Dictionary<string, JsonNode?> Snapshot(bool ready)
        => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["ready"] = JsonValue.Create(ready),
        };
}

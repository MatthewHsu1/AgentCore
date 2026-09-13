using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The <c>approval:</c> block reaching a compiled agent as <c>UseToolApproval</c> rules: an
/// <c>auto:</c> entry lets its tool run with no request surfacing, anything else still asks, and
/// the layer adds no state keys — <c>toolApprovalState</c> persistence rides with the Phase 2
/// round trip, when standing rules can exist.
/// </summary>
public sealed class ApprovalCompilationTests
{
    private const string GatedToolYaml =
        """
        apiVersion: agentcore/v1
        name: approval-auto
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email ]
              approval: { auto: [ send_email ] }
        """;

    private const string UngatedToolYaml =
        """
        apiVersion: agentcore/v1
        name: approval-auto
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email ]
              approval: { auto: [ get_time ] }
        """;

    [Fact]
    public void Compose_AgentAuto_ReturnsItsOwn()
    {
        var agent = new AgentConfiguration
        {
            Id = "coder",
            Approval = new ApprovalConfiguration { Auto = ["send_email"] },
        };

        Assert.Equal(["send_email"], AgentApproval.Compose(null, agent));
    }

    [Fact]
    public void Compose_NoAgentAuto_ReturnsTheDefaults()
    {
        var defaults = new AgentDefaults { Approval = new ApprovalConfiguration { Auto = ["get_time"] } };
        var agent = new AgentConfiguration { Id = "coder" };

        Assert.Equal(["get_time"], AgentApproval.Compose(defaults, agent));
    }

    [Fact]
    public void Compose_BothAuto_AgentWins()
    {
        var defaults = new AgentDefaults { Approval = new ApprovalConfiguration { Auto = ["get_time"] } };
        var agent = new AgentConfiguration
        {
            Id = "coder",
            Approval = new ApprovalConfiguration { Auto = ["send_email"] },
        };

        Assert.Equal(["send_email"], AgentApproval.Compose(defaults, agent));
    }

    [Fact]
    public void Compose_NoApprovalAnywhere_ReturnsEmpty()
    {
        var agent = new AgentConfiguration { Id = "coder" };

        Assert.Empty(AgentApproval.Compose(null, agent));
    }

    [Fact]
    public void Matches_ExactName_MatchesOnlyItself()
    {
        Assert.True(AgentApproval.Matches("send_email", "send_email"));
        Assert.False(AgentApproval.Matches("send_email", "send_sms"));
        Assert.False(AgentApproval.Matches("send_email", "Send_Email"));
    }

    [Fact]
    public void Matches_TrailingStar_MatchesByPrefix()
    {
        Assert.True(AgentApproval.Matches("file_access_read*", "file_access_read_lines"));
        Assert.False(AgentApproval.Matches("file_access_read*", "file_access_write"));
    }

    [Fact]
    public void Matches_StarAlone_MatchesEverythingButMidStarIsLiteral()
    {
        Assert.True(AgentApproval.Matches("*", "send_email"));
        Assert.False(AgentApproval.Matches("send*email", "send_email"));
    }

    [Fact]
    public async Task Compile_ApprovalAutoMatchingTool_RunsWithoutSurfacingRequest()
    {
        var token = TestContext.Current.CancellationToken;
        int sent = 0;
        using ToolCallingChatClient client = new(
            "done.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" });
        var compiled = Compile(GatedToolYaml, client, () => sent++, token);

        var response = await compiled.Agents["only"].RunAsync("send it", cancellationToken: token);

        Assert.Equal(1, sent);
        Assert.DoesNotContain(
            response.Messages.SelectMany(message => message.Contents),
            content => content is ToolApprovalRequestContent);
        Assert.Equal("done.", response.Text);
    }

    [Fact]
    public async Task Compile_ApprovalAutoNamingAnotherTool_RequestStillSurfaces()
    {
        var token = TestContext.Current.CancellationToken;
        int sent = 0;
        using ToolCallingChatClient client = new(
            "done.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" });
        var compiled = Compile(UngatedToolYaml, client, () => sent++, token);

        var response = await compiled.Agents["only"].RunAsync("send it", cancellationToken: token);

        Assert.Equal(0, sent);
        Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
    }

    [Fact]
    public void Compile_ApprovalAuto_AddsNoStateKeys()
    {
        var token = TestContext.Current.CancellationToken;
        using ToolCallingChatClient client = new("unused");
        var compiled = Compile(GatedToolYaml, client, () => { }, token);

        Assert.Empty(compiled.HarnessStateKeys);
    }

    private static CompiledAgent Compile(string yaml, IChatClient client, Action onSend, CancellationToken token)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        return ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(client))
            {
                Tools = TestToolRegistry.From(
                    document,
                    declared => declared.Uses == "test.send" ? GatedSendEmail(onSend) : null,
                    token),
            });
    }

    private static ApprovalRequiredAIFunction GatedSendEmail(Action onSend) => new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
        (string to) =>
        {
            onSend();
            return "sent";
        },
        "send_email",
        "Send an email."));
}

using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// The unfilled-slot reminder of section 8.3. It costs no model request.
/// </summary>
public sealed class UnfilledSlotReminderTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: reminder
        state:
          machineModel:      { type: string,  writer: extractor, description: the machine model }
          serialNumber:      { type: string,  writer: extractor, description: the serial number }
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
          postcode:          { type: string,  writer: extractor }
          orderStatus:       { type: string,  writer: tool, from: lookup_order.status }
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read }
        guards:
          identified:
            and:
              - { "!!": [ { var: machineModel } ] }
              - { "!!": [ { var: serialNumber } ] }
        agents:
          items:
            - { id: identifier }
            - { id: resolver }
        policy:
          initial: identify
          stages:
            - id: identify
              agent: identifier
              to: [ { stage: resolve, when: identified } ]
            - id: resolve
              agent: resolver
              terminal: true
        """;

    private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(Yaml);

    private static StageConfiguration Identify => Document.Policy!.Stages[0];

    private static StageConfiguration Resolve => Document.Policy!.Stages[1];

    [Fact]
    public void AnUnfilledSlot_ProducesTheReminder()
    {
        StateDocument state = new(Document);
        state.TryWrite("serialNumber", JsonValue.Create("SF240117"));

        var reminder = UnfilledSlotReminder.Build(state, Identify);

        Assert.Equal(
            "<system-reminder>\n"
            + "You still need this from the caller before you can continue: the machine model.\n"
            + "Ask for it in your next reply.\n"
            + "</system-reminder>",
            reminder);
    }

    [Fact]
    public void TwoUnfilledSlots_ReadAsOneSentence()
    {
        StateDocument state = new(Document);

        var reminder = UnfilledSlotReminder.Build(state, Identify);

        Assert.Contains("the machine model and the serial number.", reminder, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilledSlot_ProducesNoReminder()
    {
        StateDocument state = new(Document);
        state.TryWrite("machineModel", JsonValue.Create("F85"));
        state.TryWrite("serialNumber", JsonValue.Create("SF240117"));

        Assert.Null(UnfilledSlotReminder.Build(state, Identify));
    }

    [Fact]
    public void AStageWithNoGuardedExit_ProducesNoReminder()
    {
        StateDocument state = new(Document);

        Assert.Empty(UnfilledSlotReminder.UnfilledSlots(state, Resolve));
        Assert.Null(UnfilledSlotReminder.Build(state, Resolve));
    }

    [Fact]
    public void OnlyAnExtractorSlot_Reminds()
    {
        StateDocument state = new(Document);

        // orderStatus is unfilled too, and no reminder names it: a tool fills it, not the caller.
        Assert.DoesNotContain("orderStatus", UnfilledSlotReminder.UnfilledSlots(state, Identify));
    }

    [Fact]
    public void TheReminder_GoesAboveTheReplyAgentsInput()
    {
        StateDocument state = new(Document);

        var input = UnfilledSlotReminder.Prepend(UnfilledSlotReminder.Build(state, Identify), "the caller said hello");

        Assert.StartsWith(UnfilledSlotReminder.OpenTag, input, StringComparison.Ordinal);
        Assert.EndsWith("the caller said hello", input, StringComparison.Ordinal);
    }

    [Fact]
    public void NoReminder_LeavesTheInputAlone()
        => Assert.Equal("hello", UnfilledSlotReminder.Prepend(null, "hello"));

    [Fact]
    public void AnExitGuardedByMissing_RemindsOnTheMissingSlot()
    {
        // A guard written with the JSONLogic `missing` operator (rather than `var`) still names the
        // slot it waits on. The walk that collects stage-exit slots must catch both forms, or a guard
        // written this way reads as waiting on no slots and the caller never gets a reminder.
        var yaml = Yaml.Replace(
            "- { \"!!\": [ { var: serialNumber } ] }",
            "- { \"!\": { missing: [ \"serialNumber\" ] } }",
            StringComparison.Ordinal);
        var document = ConfigurationLoader.LoadYaml(yaml);
        StateDocument state = new(document);
        state.TryWrite("machineModel", JsonValue.Create("F85"));

        var reminder = UnfilledSlotReminder.Build(state, document.Policy!.Stages[0]);

        Assert.Contains("the serial number.", reminder, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlotWithNoDescription_RemindsByItsName()
    {
        var yaml = Yaml.Replace(
            "- { \"!!\": [ { var: serialNumber } ] }",
            "- { \"!!\": [ { var: postcode } ] }",
            StringComparison.Ordinal);
        var document = ConfigurationLoader.LoadYaml(yaml);
        StateDocument state = new(document);
        state.TryWrite("machineModel", JsonValue.Create("F85"));

        var reminder = UnfilledSlotReminder.Build(state, document.Policy!.Stages[0]);

        Assert.Contains("postcode.", reminder, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlotWithADeclaredDefault_IsNeverUnfilled()
    {
        // Section 8.3 reminds on a slot that is "still null". A declared default means the slot
        // reads as that default and never as null, so the caller has nothing left to supply. T51
        // rests on the same rule: a missed extraction and an unanswered question both leave null.
        var yaml = Yaml.Replace(
            "- { \"!!\": [ { var: serialNumber } ] }",
            "- { \"!\": { var: callerSaidGoodbye } }",
            StringComparison.Ordinal);
        var document = ConfigurationLoader.LoadYaml(yaml);
        StateDocument state = new(document);
        state.TryWrite("machineModel", JsonValue.Create("F85"));

        Assert.DoesNotContain("callerSaidGoodbye", UnfilledSlotReminder.UnfilledSlots(state, document.Policy!.Stages[0]));
        Assert.Null(UnfilledSlotReminder.Build(state, document.Policy!.Stages[0]));
    }

    [Fact]
    public void AnInferredFlagWithADefault_NeverReachesTheCaller()
    {
        // The shape config/local.yaml ships: one boolean the extractor infers from the turn, read by
        // the exit guard of the only talking stage. Before this rule the agent was told to ask the
        // caller for "callerSaidGoodbye", and it did.
        var yaml =
            """
            apiVersion: agentcore/v1
            name: local-shape
            state:
              callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
            guards:
              saidGoodbye: { var: callerSaidGoodbye }
            agents:
              items:
                - { id: helper }
                - { id: closer }
            policy:
              initial: talk
              stages:
                - id: talk
                  agent: helper
                  to: [ { stage: close, when: saidGoodbye } ]
                - id: close
                  agent: closer
                  terminal: true
            """;
        var document = ConfigurationLoader.LoadYaml(yaml);
        StateDocument state = new(document);

        Assert.Null(UnfilledSlotReminder.Build(state, document.Policy!.Stages[0]));
    }
}

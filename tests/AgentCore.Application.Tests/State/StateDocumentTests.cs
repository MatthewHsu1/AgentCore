using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// <see cref="StateDocument.WrittenSlots"/>: the durable-blob view of a call's declared state.
/// </summary>
public sealed class StateDocumentTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: written-slots
        state:
          model:  { type: string, writer: tool, from: lookup.model }
          serial: { type: string, writer: tool, from: lookup.serial }
        tools:
          - { id: lookup, kind: builtin, uses: orders.read }
        agents:
          items:
            - { id: only }
        """;

    private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(Yaml);

    /// <summary>How many slots the concurrency test fills. Enough to keep the reader busy while it does.</summary>
    private const int SlotCount = 200;

    /// <summary>A document of <see cref="SlotCount"/> declared slots, written out rather than held as a literal.</summary>
    private static string ManySlotsYaml =>
        $$"""
        apiVersion: agentcore/v1
        name: many-slots
        state:
        {{string.Join('\n', Enumerable.Range(0, SlotCount)
            .Select(slot => $"  {SlotName(slot)}: {{ type: integer, writer: tool, from: lookup.{SlotName(slot)} }}"))}}
        tools:
          - { id: lookup, kind: builtin, uses: orders.read }
        agents:
          items:
            - { id: only }
        """;

    [Fact]
    public void WrittenSlots_HandsOutAFreshCopyEveryCall()
    {
        StateDocument state = new(Document);
        var result = JsonNode.Parse("""{ "model": "F63" }""");
        Assert.Equal(1, ToolStateWriter.Apply(state, "lookup", result));

        var first = state.WrittenSlots();
        var second = state.WrittenSlots();

        // The declared state schema only ever writes scalars (section 8.3's four writers coerce
        // to boolean/integer/number/string), so a scalar JsonValue is the only value shape this
        // slot can hold today, and a scalar exposes no public mutator. The clone's contract is
        // still checked the way a caller could actually break it without one: two calls must not
        // hand back the same node instance, because a caller free to reparent or wrap one call's
        // node (e.g. into a JsonObject of its own) would otherwise reach into the live document,
        // or into the copy a different caller was given.
        Assert.NotSame(first["model"], second["model"]);
        Assert.Equal("F63", second["model"]!.GetValue<string>());
    }

    [Fact]
    public void WrittenSlots_OmitsASlotNoWriterHasFilled()
    {
        StateDocument state = new(Document);
        var result = JsonNode.Parse("""{ "model": "F63" }""");
        Assert.Equal(1, ToolStateWriter.Apply(state, "lookup", result));

        var written = state.WrittenSlots();

        // "serial" is declared but no writer touched it. Absent, not present-with-its-default,
        // is the distinction IsUnfilled exists to preserve; Snapshot() (which fills every
        // declared slot with its default) would destroy it.
        Assert.True(written.ContainsKey("model"));
        Assert.False(written.ContainsKey("serial"));
        Assert.True(state.IsUnfilled("serial"));
    }

    [Fact]
    public async Task WrittenSlots_ReadWhileTheTurnLoopIsStillWriting_AnswersInsteadOfThrowing()
    {
        // The turn loop is this document's only WRITER, but it is not its only toucher.
        // CallSession.Snapshot reads it off the turn, and AgentCoreAgent.SerializeSessionCoreAsync is
        // a framework seam any host thread may call while a turn is running — an exposure this branch
        // created, because that seam used to throw NotSupportedException instead of answering.
        // Enumerating a plain Dictionary mid-write is an InvalidOperationException, and it would
        // surface out of the framework's own serialization API.
        var document = ConfigurationLoader.LoadYaml(ManySlotsYaml);

        for (var round = 0; round < 60; round++)
        {
            StateDocument state = new(document);
            using ManualResetEventSlim reading = new();
            using CancellationTokenSource written = new();

            var reader = Task.Run(
                () =>
                {
                    reading.Set();
                    while (!written.IsCancellationRequested)
                    {
                        // A torn read is fine and is the accepted price: the snapshot is best effort
                        // by D5, and the next turn's write corrects it. A throw is not fine.
                        _ = state.WrittenSlots();
                    }
                },
                TestContext.Current.CancellationToken);

            reading.Wait(TestContext.Current.CancellationToken);

            for (var slot = 0; slot < SlotCount; slot++)
            {
                Assert.True(state.TryWrite(SlotName(slot), JsonValue.Create((long)slot)));
            }

            await written.CancelAsync();
            await reader;
        }
    }

    private static string SlotName(int index) => $"s{index}";
}

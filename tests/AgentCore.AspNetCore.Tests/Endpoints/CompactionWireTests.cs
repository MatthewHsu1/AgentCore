using System.Text.Json;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// D8: a compaction notice rides the dialect as its own <c>agentcore_compaction</c> part, with the
    /// same null-omitting JSON every other payload uses.
    /// </summary>
    /// <remarks>
    /// D7 — the notice update never reaches the framework converter — lives in
    /// <c>ResponsesTurnStream.StreamAgentUpdatesAsync</c>, which the YAML-driven host used by
    /// <c>ToolWireTests</c> cannot drive: compaction is not a document-configurable feature. The
    /// converter's split, and why the drop matters, is recorded in
    /// <c>docs/probes/CompactionNoticeWireProbe</c>; nothing here re-proves the framework.
    /// </remarks>
    public sealed class CompactionWireTests
    {
        [Fact]
        public void ACompactionNotice_MapsToOneCompactionPart()
        {
            ChatResponseUpdate update = new(ChatRole.Assistant, [new CompactionContent(CompactionContent.EndPhase, "compacted")]);

            TurnStreamPart part = Assert.Single(TurnStreamParts.From(update, new ToolCallNames()));

            Assert.Equal(TurnStreamPart.Compaction, part.Member);
            CompactionPayload payload = Assert.IsType<CompactionPayload>(part.Payload);
            Assert.Equal(CompactionContent.EndPhase, payload.Phase);
            Assert.Equal("compacted", payload.Outcome);
        }

        [Fact]
        public void AStartNotice_SerializesWithNoOutcomeMember()
        {
            CompactionPayload start = new() { Phase = CompactionContent.StartPhase };
            CompactionPayload end = new() { Phase = CompactionContent.EndPhase, Outcome = "unchanged" };

            Assert.Equal(
                """{"phase":"start"}""",
                JsonSerializer.Serialize(start, ResponsesJson.Options));
            Assert.Equal(
                """{"phase":"end","outcome":"unchanged"}""",
                JsonSerializer.Serialize(end, ResponsesJson.Options));
        }
    }
}

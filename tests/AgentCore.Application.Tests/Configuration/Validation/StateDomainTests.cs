using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation;

/// <summary>
/// The state-domain enumeration of check 5, section 8.5.
/// </summary>
public sealed class StateDomainTests
{
    [Fact]
    public void Points_EnumSlotWithNoDefault_IncludesTheUnfilledPoint()
    {
        AgentCoreConfiguration configuration = new()
        {
            ApiVersion = "agentcore/v1",
            Name = "doc",
            State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
            {
                ["applies_to"] = new()
                {
                    Type = StateSlotType.String,
                    Writer = StateWriter.Extractor,
                    EnumValues = [JsonValue.Create("f63")!, JsonValue.Create("f65")!],
                },
            },
        };

        var points = Points(configuration, "applies_to");

        Assert.Contains(points, point => point is null);
        Assert.Equal(3, points.Count);
    }

    private static IReadOnlyList<JsonNode?> Points(AgentCoreConfiguration configuration, string slotName)
    {
        var facts = new GuardRuleFacts();
        facts.Collect(new JsonObject { ["var"] = slotName });

        var domains = StateDomain.Build(facts, configuration.State, new Dictionary<string, JsonNode?>(StringComparer.Ordinal));
        return domains.Single(domain => domain.Name == slotName).Points;
    }
}

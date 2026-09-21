using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>One group of siblings check 5 reads together: the exits of one stage, or the edges out of one node.</summary>
    /// <param name="Exits">The siblings, in document order.</param>
    /// <param name="Pointer">The JSON Pointer to the group, used by the coverage warning.</param>
    /// <param name="Description">How the warning names the group.</param>
    /// <param name="Pinned">Slots whose value the group already fixes, such as <c>stage</c>.</param>
    internal sealed record SiblingGroup(
        IReadOnlyList<SiblingExit> Exits,
        string Pointer,
        string Description,
        IReadOnlyDictionary<string, JsonNode?> Pinned);
}

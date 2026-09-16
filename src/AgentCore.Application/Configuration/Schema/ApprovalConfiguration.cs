namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One agent's <c>approval:</c> block: the standing rules that answer an approval request before it
/// surfaces. Everything a rule does not name still asks.
/// </summary>
public sealed record ApprovalConfiguration
{
    /// <summary>
    /// Gets the tool-name patterns a request never surfaces for. A pattern names one tool exactly,
    /// or a prefix with a trailing <c>*</c>.
    /// </summary>
    public IReadOnlyList<string> Auto { get; init; } = [];
}
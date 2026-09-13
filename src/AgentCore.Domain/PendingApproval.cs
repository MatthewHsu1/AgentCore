using System.Text.Json;

namespace AgentCore.Domain;

/// <summary>
/// One tool call waiting on a human answer: what the model wanted to run, and the id the
/// answer names.
/// </summary>
/// <param name="RequestId">The id the approval answer carries back.</param>
/// <param name="ToolName">The tool the model asked to call.</param>
/// <param name="Arguments">The arguments the model produced, as it produced them.</param>
public sealed record PendingApproval(string RequestId, string ToolName, JsonElement Arguments);

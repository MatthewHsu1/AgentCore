using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// One tool a source serves.
/// </summary>
/// <param name="Id">The name the model calls. It is unique across every source.</param>
/// <param name="Description">The sentence the model reads to decide when to call it.</param>
/// <param name="Materialise">
/// Builds the tool. It runs at most once, on the first resolve, and never during a call.
/// </param>
public sealed record ToolRegistration(string Id, string Description, Func<AITool> Materialise);

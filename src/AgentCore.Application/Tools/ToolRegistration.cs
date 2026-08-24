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
/// <param name="CallTimeout">
/// How long one call may take, or <see langword="null"/> for no deadline of its own. A source sets
/// this when the document gives it one; <see cref="ToolRegistryBuilder"/> is what applies it, so
/// every kind that wants a deadline gets the same one implementation and the same message.
/// </param>
public sealed record ToolRegistration(
    string Id,
    string Description,
    Func<AITool> Materialise,
    TimeSpan? CallTimeout = null);

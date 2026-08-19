using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>One stored message of one call. It is one row of store 1.</summary>
/// <param name="CallId">The call the message belongs to.</param>
/// <param name="Ordinal">The message's position within the call. It is dense and never reused.</param>
/// <param name="TurnIndex">The turn the message belongs to. It is the join to the audit chain.</param>
/// <param name="Content">The message itself.</param>
internal sealed record CallMessage(string CallId, int Ordinal, int TurnIndex, ChatMessage Content);

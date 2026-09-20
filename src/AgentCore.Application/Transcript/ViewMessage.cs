using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>
/// One message of a compaction view, with the last ordinal it stands for: a live row's own, or
/// what a standing summary covers. This is what lets the strategy's output land back on ordinals
/// instead of positions.
/// </summary>
/// <param name="Message">The message, as the model would see it.</param>
/// <param name="LastOrdinal">The last ordinal this message stands for, inclusive.</param>
internal readonly record struct ViewMessage(ChatMessage Message, int LastOrdinal);

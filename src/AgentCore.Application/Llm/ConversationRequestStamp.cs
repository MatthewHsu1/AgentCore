using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Llm;

/// <summary>
/// Copies the running turn's conversation id onto the request, under
/// <see cref="ChatRequestProperties.ConversationId"/>, where a vendor adapter can read it.
/// </summary>
internal static class ConversationRequestStamp
{
    internal static void Apply(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (TurnInvocation.From(options) is { ConversationId: { Length: > 0 } conversationId })
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties[ChatRequestProperties.ConversationId] = conversationId;
        }
    }
}

using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Resolves a <c>{ ref: reply }</c> model reference to a chat client.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>Gets the chat client one model reference names.</summary>
    /// <param name="model">
    /// The reference, or <see langword="null"/> when the agent inherits nothing and the factory
    /// picks its own default.
    /// </param>
    /// <returns>The chat client. The compiler never disposes it.</returns>
    IChatClient GetChatClient(ModelReference? model);

    /// <summary>Resolves a hosted tool marker for one model reference.</summary>
    /// <param name="marker">The marker the document declared.</param>
    /// <param name="model">The reference, or <see langword="null"/> for the factory's default entry.</param>
    /// <returns>The tool to hand the model, or <see langword="null"/> when the vendor behind it does not run it.</returns>
    AITool? ResolveHostedTool(AITool marker, ModelReference? model) => null;
}

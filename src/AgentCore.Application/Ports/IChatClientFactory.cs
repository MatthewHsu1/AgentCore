using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Resolves a <c>{ ref: reply }</c> model reference to a chat client.
/// </summary>
/// <remarks>
/// <c>providers:</c> configures adapters and never changes agent shape, so the compiler never reads
/// a vendor name. It asks for the client behind the <c>as</c> name and builds the same agent either
/// way. This is the seam where the LLM port binds.
/// </remarks>
public interface IChatClientFactory
{
    /// <summary>Gets the chat client one model reference names.</summary>
    /// <param name="model">
    /// The reference, or <see langword="null"/> when the agent inherits nothing and the factory
    /// picks its own default.
    /// </param>
    /// <returns>The chat client. The compiler never disposes it.</returns>
    IChatClient GetChatClient(ModelReference? model);
}

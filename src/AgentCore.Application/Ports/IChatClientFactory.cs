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

    /// <summary>Whether the vendor behind one model reference runs a hosted web search.</summary>
    /// <param name="model">The reference, or <see langword="null"/> for the factory's default entry.</param>
    /// <returns><see langword="true"/> when the vendor runs the search itself.</returns>
    bool SupportsHostedWebSearch(ModelReference? model) => false;
}

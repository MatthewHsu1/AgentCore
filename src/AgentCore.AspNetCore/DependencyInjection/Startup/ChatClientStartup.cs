using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 3c: build the chat client factory, before anything that needs a model.</summary>
/// <remarks>
/// It is built from the document and the resolved secrets and from nothing else, so it can be built
/// before the tools. That ordering is what lets a shipped agent hold its own model rather than
/// looking one up at call time.
/// </remarks>
internal static class ChatClientStartup
{
    /// <summary>Asks the host for its chat clients.</summary>
    /// <param name="options">The options the host filled. It carries the chat client seam.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The factory.</returns>
    /// <exception cref="InvalidOperationException">The options bind no chat client adapter.</exception>
    internal static ValueTask<IChatClientFactory> BuildAsync(
        AgentCoreOptions options,
        AgentCoreStartup startup,
        CancellationToken cancellationToken)
        => (options.ChatClients
            ?? throw new InvalidOperationException(
                "AddAgentCoreAsync binds no chat client adapter. Call options.UseChatClients(...), because the "
                + "compile table asks it for every agent and for the extractor."))
            .Invoke(startup, cancellationToken);
}

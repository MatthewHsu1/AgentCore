using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the vendor client behind one <c>providers.llm[].kind</c> value.
/// </summary>
public interface IChatClientAdapter : IVendorAdapter
{
    /// <summary>Builds the vendor client of one entry.</summary>
    /// <param name="entry">The <c>providers.llm[]</c> entry, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The client. The composite owns and disposes it; the adapter never does.</returns>
    ValueTask<IChatClient> CreateClientAsync(
        LlmProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a hosted tool marker for one entry, tuning it for this vendor.</summary>
    /// <param name="marker">The marker the document declared, such as a hosted search or code execution tool.</param>
    /// <param name="entry">The <c>providers.llm[]</c> entry this adapter serves.</param>
    /// <returns>The tool to hand the model, or <see langword="null"/> when this vendor does not run it.</returns>
    AITool? ResolveHostedTool(AITool marker, LlmProviderConfiguration entry) => null;

    /// <summary>Reports the model's context window, in tokens.</summary>
    /// <param name="entry">The <c>providers.llm[]</c> entry this adapter serves.</param>
    /// <returns>
    /// The window in tokens, or <see langword="null"/> when this adapter does not know the model.
    /// Compaction is always on, so the compiler refuses an agent whose model answers
    /// <see langword="null"/>.
    /// </returns>
    int? GetContextWindow(LlmProviderConfiguration entry) => null;
}

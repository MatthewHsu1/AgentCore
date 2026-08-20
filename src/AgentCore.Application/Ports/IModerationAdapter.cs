using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the moderation evaluator behind one <c>providers.moderation</c> value.
/// </summary>
/// <remarks>
/// <para>
/// This is the moderation mirror of <see cref="IChatClientAdapter"/> and
/// <see cref="IKnowledgeStoreAdapter"/>. The document names a vendor and the host registers one
/// adapter for each vendor it supports, so a document that changes moderation vendors changes no
/// code.
/// </para>
/// <para>
/// D13 makes <see cref="IEvaluator"/> the moderation port and refuses a second one, so this
/// interface builds an <see cref="IEvaluator"/> rather than wrapping it. It is a factory, and never
/// a port that a turn calls. D13 also promises that replacing OpenAI with a self-hosted classifier
/// is a one-file change: that file is the adapter, and this is the seam it plugs into.
/// </para>
/// <para>
/// An adapter owns its vendor only: the endpoint, the credential, and the shape of the reply.
/// Everything vendor-neutral — reading the verdict, refusing the turn, and writing
/// <c>prompt.flagged</c> — lives above it, so no adapter repeats it.
/// </para>
/// <para>
/// A host that registers no adapter, or a document that names no <c>providers.moderation</c>,
/// moderates nothing. Every turn then reaches the model. That is the deliberate default: moderation
/// needs a vendor account, and a library that refused to start without one could not be used in a
/// test.
/// </para>
/// </remarks>
public interface IModerationAdapter : IVendorAdapter
{
    /// <summary>Builds the evaluator that reads text through this vendor's endpoint.</summary>
    /// <param name="entry">The <c>providers.moderation</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The evaluator. The host owns it for the life of the process.</returns>
    /// <remarks>
    /// This runs once, while the host starts. A missing credential therefore stops the host and never
    /// a call, which is what item 9 of section 11 asks for. It opens no socket, so a host with no
    /// route to the vendor still starts.
    /// </remarks>
    ValueTask<IEvaluator> CreateAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}

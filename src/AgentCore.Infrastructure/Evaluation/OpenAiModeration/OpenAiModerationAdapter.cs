using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Infrastructure.Evaluation.OpenAiModeration;

/// <summary>
/// The <c>openai</c> moderation vendor: the OpenAI Moderation endpoint behind <see cref="IEvaluator"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the host registers, and it takes the shape <see cref="IKnowledgeStoreAdapter"/>
/// already takes: the host lists the vendors it supports once, <c>providers.moderation.kind</c>
/// picks one, and a document that changes vendors changes no code here.
/// </para>
/// <para>
/// The adapter owns the vendor only. Everything vendor-neutral — reading the verdict, refusing the
/// turn, and writing <c>prompt.flagged</c> — lives above it. D13 promises that a self-hosted
/// classifier behind the same <see cref="IEvaluator"/> is a one-file change, and this file plus
/// <see cref="OpenAiModerationEvaluator"/> are that one file's worth of vendor knowledge.
/// </para>
/// <para>
/// Building costs no request, exactly as <c>ZillizKnowledgeAdapter</c> costs none. The key is read
/// once, from the same chain every other OpenAI call reads, so a missing credential stops the host
/// rather than a call, and a bad one stops the first check rather than the start.
/// </para>
/// </remarks>
public sealed class OpenAiModerationAdapter : IModerationAdapter
{
    /// <summary>The one <c>kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "openai";

    private readonly IHttpMessageHandlerFactory _handlers;

    /// <summary>Creates the adapter over the outbound pipeline of the host.</summary>
    /// <param name="handlers">The pipeline that holds the connection lifetime, the deadline, and the retry.</param>
    /// <exception cref="ArgumentNullException">The pipeline is <see langword="null"/>.</exception>
    /// <remarks>
    /// The adapter builds the client, and nothing above it builds one. That is the rule
    /// <c>ZillizKnowledgeAdapter</c> already follows, so no adapter holds a retry policy of its own.
    /// </remarks>
    public OpenAiModerationAdapter(IHttpMessageHandlerFactory handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
    }

    /// <summary>Gets the one <c>kind</c> value this adapter serves.</summary>
    public string Kind => ProviderKind;

    /// <inheritdoc />
    public async ValueTask<IEvaluator> CreateAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
        => await OpenAiModerationEvaluator
            .CreateAsync(_handlers, secrets, cancellationToken)
            .ConfigureAwait(false);
}

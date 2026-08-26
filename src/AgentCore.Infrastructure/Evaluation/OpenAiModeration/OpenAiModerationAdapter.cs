using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Infrastructure.Evaluation.OpenAiModeration;

/// <summary>
/// The <c>openai</c> moderation vendor: the OpenAI Moderation endpoint behind <see cref="IEvaluator"/>.
/// </summary>
public sealed class OpenAiModerationAdapter : IModerationAdapter
{
    /// <summary>The one <c>kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "openai";

    private readonly IHttpMessageHandlerFactory _handlers;

    /// <summary>Creates the adapter over the outbound pipeline of the host.</summary>
    /// <param name="handlers">The pipeline that holds the connection lifetime, the deadline, and the retry.</param>
    /// <exception cref="ArgumentNullException">The pipeline is <see langword="null"/>.</exception>
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

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>The evaluation seam of D13: the registry the turn loop reads, and the sampled judge.</summary>
internal static class EvaluationStartup
{
    /// <summary>Builds the registry, with the built-in evaluator and any moderation vendor.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.moderation</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    /// <returns>The registry, ready for the turn loop and for the offline golden set alike.</returns>
    internal static async ValueTask<EvaluatorRegistry> CreateRegistryAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CancellationToken cancellationToken)
    {
        EvaluatorRegistry evaluators = new();
        evaluators.Register("fault_code", new FaultCodeEvaluator());

        if (options.Moderation is { } moderationAdapters
            && await ModerationEvaluatorFactory
                .CreateAsync(configuration, options.SecretResolver, moderationAdapters, cancellationToken)
                .ConfigureAwait(false) is { } moderation)
        {
            evaluators.Register(PromptModerator.ModerationEvaluatorName, moderation);
        }

        return evaluators;
    }
}

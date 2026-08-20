using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>Registers the evaluation seam of D13, at the rate the document sets.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>evaluation.sampleRate</c>.</param>
    /// <param name="evaluators">
    /// The registry <see cref="CreateRegistryAsync"/> built, which already holds <c>fault_code</c>
    /// and, when the host bound one, the moderation evaluator.
    /// </param>
    internal static void Register(
        IServiceCollection services,
        AgentCoreConfiguration configuration,
        EvaluatorRegistry evaluators)
    {
        services.TryAddSingleton(evaluators);
        services.TryAddSingleton(new EvaluationSampler(configuration.Evaluation?.SampleRate ?? EvaluationConfiguration.DefaultSampleRate));
        services.TryAddSingleton<IEvaluationScorePublisher, InMemoryEvaluationScorePublisher>();
    }
}

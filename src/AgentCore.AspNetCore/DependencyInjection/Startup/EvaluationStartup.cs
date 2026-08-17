using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>The evaluation seam of D13: the registry the turn loop reads, and the sampled judge.</summary>
internal static class EvaluationStartup
{
    /// <summary>Builds the registry, with the built-in evaluator and any moderation vendor.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.moderation</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    /// <returns>The registry, ready for the turn loop and for the offline golden set alike.</returns>
    /// <remarks>
    /// <para>
    /// This runs before the session factory is built, because the moderator the turn loop reads comes
    /// out of it. D13 asks for one evaluator used twice: the same object serves the turn loop and the
    /// offline golden set through the registration below.
    /// </para>
    /// <para>
    /// <c>providers.moderation.kind</c> picks one of the vendors the host registered. A document that
    /// names none moderates nothing, and no adapter is asked to build anything.
    /// </para>
    /// <para>
    /// <c>fault_code</c> is registered because D13 names it and because it calls no model: the
    /// measurement is a set comparison over the reply text. It is the one evaluator that is safe to
    /// register by default.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Each registration is a <c>TryAdd</c>, so a host that registered its own registry, its own
    /// sampler, or its own publisher keeps it. That matches how <c>ICallSessionStore</c> is
    /// registered, and it matters most for the publisher: the in-memory one keeps every score in a
    /// list that grows without a bound, so a long-running host replaces it.
    /// </para>
    /// <para>
    /// <b>The sample rate comes from the document, and it defaults to 0.</b> Triage row T18 says the
    /// rate comes from configuration and defers the online path until the offline gate proves the
    /// evaluators, and D9 says a judge must never block a turn. A document that sets no rate
    /// therefore draws no number and calls no evaluator, so the seam is reachable and costs nothing.
    /// The range is checked at load, so the value read here is already good.
    /// </para>
    /// <para>
    /// <b>Moderation does not pass through <see cref="EvaluationSampler"/>.</b> D13 says the endpoint
    /// is free at any volume and counts against no usage limit, so every turn is checked and
    /// "sampling buys nothing when the call is free". The sampler here governs the judge of T18, and
    /// nothing else.
    /// </para>
    /// </remarks>
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

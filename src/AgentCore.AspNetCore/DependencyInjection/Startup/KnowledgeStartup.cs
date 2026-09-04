using System.Text;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Step 3b: open the knowledge base the document names, before any tool is built.</summary>
internal static class KnowledgeStartup
{
    /// <summary>Opens the knowledge port the document names.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.knowledge</c>.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors and any explicit seam.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <param name="embeddings">
    /// The generator <c>providers.embeddings</c> built, or <see langword="null"/> when the document
    /// names none. Handed to the matched adapter, which fails the start by name when it ranks by
    /// vector and received none.
    /// </param>
    /// <param name="scopeDeclared">Whether ANY agent in the document declares <c>knowledge: { scoped: true }</c>.</param>
    /// <param name="requireScope">
    /// Whether EVERY agent in the document declares <c>knowledge: { scoped: true }</c>. See
    /// <see cref="CompositeKnowledgeStoreFactory.CreateAsync"/> for why this is a different question
    /// from <paramref name="scopeDeclared"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the adapter build.</param>
    /// <returns>The port, open, or <see langword="null"/>.</returns>
    internal static ValueTask<IKnowledgeRetrievalPort?> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        AgentCoreStartup startup,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        bool scopeDeclared,
        bool requireScope,
        CancellationToken cancellationToken)
    {
        if (options.KnowledgeRetrieval is { } retrieval)
        {
            return ValueTask.FromResult<IKnowledgeRetrievalPort?>(retrieval(startup));
        }

        if (configuration.Providers?.Knowledge is null && !AnyAgentDeclares(configuration))
        {
            return ValueTask.FromResult<IKnowledgeRetrievalPort?>(null);
        }

        return options.KnowledgeStores is { } stores
            ? CompositeKnowledgeStoreFactory.CreateAsync(
                configuration, options.SecretResolver, stores, embeddings, scopeDeclared, requireScope, cancellationToken)
            : ValueTask.FromResult<IKnowledgeRetrievalPort?>(null);
    }

    /// <summary>Whether any agent's <c>knowledge:</c> block composes.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <returns><see langword="true"/> when at least one agent reads the knowledge base.</returns>
    private static bool AnyAgentDeclares(AgentCoreConfiguration configuration)
        => configuration.Agents is { } agents && AgentKnowledge.AnyDeclared(agents);

    /// <summary>
    /// Section 10: reads every <c>vocabulary:</c> slot's domain into <paramref name="vocabulary"/>,
    /// K44's Unicode probe ahead of those reads, and K48's per-<c>wildcard.facets</c> member check.
    /// </summary>
    /// <param name="configuration">The loaded document. Already past checks 2 to 8.</param>
    /// <param name="knowledge">The port <see cref="OpenAsync"/> built, or <see langword="null"/>.</param>
    /// <param name="vocabulary">The cache every successful read is installed into.</param>
    /// <param name="logger">Where K28's and K48's warnings go.</param>
    /// <param name="cancellationToken">Cancels every read.</param>
    /// <param name="composesUnicode">
    /// K44's probe, or <see langword="null"/> for the real one (<see cref="ComposesUnicode"/>). A
    /// test drives the outcome through this seam: the AspNetCore test host already runs under
    /// <c>InvariantGlobalization=true</c>, which makes the non-composing case the ambient default and
    /// the composing case unreachable without one.
    /// </param>
    /// <exception cref="ConfigurationLoadException">
    /// A <c>vocabulary:</c> slot is declared and <paramref name="knowledge"/> is not an
    /// <see cref="IFacetVocabularyPort"/> (K27); the runtime cannot compose Unicode and a slot does
    /// not declare <c>assumeNormalized: true</c> (K44); or a slot's read is degenerate (K4, K31).
    /// </exception>
    internal static async ValueTask ApplyVocabularyAsync(
        AgentCoreConfiguration configuration,
        IKnowledgeRetrievalPort? knowledge,
        VocabularyCache vocabulary,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<bool>? composesUnicode = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(logger);

        var vocabularySlots = configuration.State
            .Where(entry => entry.Value.Vocabulary is not null)
            .ToList();

        var wildcard = configuration.Providers?.Knowledge?.Scope.Wildcard;
        var wildcardFacets = wildcard?.Facets ?? [];

        if (vocabularySlots.Count == 0 && wildcardFacets.Count == 0)
        {
            return;
        }

        if (knowledge is not IFacetVocabularyPort port)
        {
            if (vocabularySlots.Count == 0)
            {
                // A wildcard-only document predates this design (the 2026-09-01 wildcard plan), and
                // K27's refusal is scoped to a document that declares vocabulary:. Forcing every such
                // deployment to also implement IFacetVocabularyPort would break K19's byte-identity
                // promise for a feature this design never touches.
                return;
            }

            var reason = knowledge is null
                ? "no knowledge port was built, so there is nothing to read a facet vocabulary from"
                : $"the built port ({knowledge.GetType().Name}) does not implement {nameof(IFacetVocabularyPort)}";

            throw FailSlots(
                vocabularySlots.Select(entry => entry.Key),
                $"declares vocabulary:, and {reason}. A vocabulary: read needs "
                + $"{nameof(IFacetVocabularyPort)}.{nameof(IFacetVocabularyPort.ReadAsync)}, which a "
                + "host-bound port (UseKnowledgeRetrieval), the composite CompositeKnowledgeStoreFactory "
                + "builds from providers.knowledge.kind, and a document with no registered store adapter "
                + "can each fail to serve.");
        }

        var template = ScopeTemplate.Parse(configuration.Providers?.Knowledge?.Scope.Template)
            ?? throw new InvalidOperationException(
                "a vocabulary: or wildcard.facets read needs providers.knowledge.scope.template, and "
                + "this document names none. Check 5 of section 8.5 should have refused this document "
                + "before boot ever reached this read.");

        if (vocabularySlots.Count > 0)
        {
            if (!(composesUnicode ?? ComposesUnicode)())
            {
                var offending = vocabularySlots
                    .Where(entry => !entry.Value.Vocabulary!.AssumeNormalized)
                    .Select(entry => entry.Key)
                    .ToList();

                if (offending.Count > 0)
                {
                    throw FailSlots(
                        offending,
                        "declares vocabulary:, and this runtime cannot compose Unicode: "
                        + "InvariantGlobalization is enabled and string.Normalize is a no-op, so two "
                        + "spellings of one id that only differ by composition would never fold alike "
                        + "and section 10's collision check would never fire. Set InvariantGlobalization "
                        + "to false to gain ICU, or declare vocabulary.assumeNormalized: true if every id "
                        + "in the collection and every mention the extractor emits is already NFC.");
                }
            }

            foreach (var (slotName, slot) in vocabularySlots)
            {
                var vocab = slot.Vocabulary!;
                var path = template.Resolve(slotName);
                var values = await port.ReadAsync(path, vocab.MaxValues, cancellationToken).ConfigureAwait(false);
                var wildcardValue = wildcardFacets.Contains(slotName, StringComparer.Ordinal)
                    ? wildcard!.Value
                    : null;

                try
                {
                    vocabulary.Replace(slotName, values, vocab.MaxValues, wildcardValue);
                }
                catch (VocabularyException degenerate)
                {
                    throw Fail(
                        ConfigurationError.AppendPointer(StatePointer(slotName), "vocabulary"),
                        $"{degenerate.Message} Read at path '{path}'.",
                        degenerate);
                }

                if (vocabulary.Snapshot().TryGetValue(slotName, out var view) && view.Originals.Count == 1)
                {
                    KnowledgeStartupLog.SingleValueVocabulary(logger, slotName, view.Originals[0]);
                }
            }
        }

        foreach (var facet in wildcardFacets)
        {
            var path = template.Resolve(facet);

            IReadOnlyList<string> sample;
            try
            {
                sample = await port.ReadAsync(path, 2, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                // K48 sits under "warned, not refused": a facet whose store cannot serve this small
                // sample read (for example, no keyword payload index at this path) must not turn a
                // deployment that was booting into one that cannot.
                KnowledgeStartupLog.WildcardFacetCheckFailed(logger, facet, path, fault);
                continue;
            }

            if (!sample.Contains(wildcard!.Value, StringComparer.Ordinal))
            {
                KnowledgeStartupLog.WildcardFacetMissingStar(logger, facet, path);
            }
        }
    }

    /// <summary>
    /// K44's probe: whether this runtime can compose Unicode. Built from escape sequences, never
    /// typed characters — a typed version reads identically and silently reports a false pass,
    /// because the source file reaches disk already pre-composed (section 10).
    /// </summary>
    /// <returns><see langword="true"/> when <see cref="string.Normalize(NormalizationForm)"/> actually composes.</returns>
    internal static bool ComposesUnicode() => "\u0041\u030A".Normalize(NormalizationForm.FormC) == "\u00C5";

    private static string StatePointer(string slot) => ConfigurationError.AppendPointer("/state", slot);

    private static ConfigurationLoadException Fail(string pointer, string message, Exception? inner = null)
        => new(
            new ConfigurationError { Pointer = pointer, Message = message, Check = ConfigurationCheck.ReferenceResolution },
            inner);

    private static ConfigurationLoadException FailSlots(IEnumerable<string> slots, string reasonAfterSlotName)
        => new(slots.Select(slot => new ConfigurationError
        {
            Pointer = ConfigurationError.AppendPointer(StatePointer(slot), "vocabulary"),
            Message = $"the slot '{slot}' {reasonAfterSlotName}",
            Check = ConfigurationCheck.ReferenceResolution,
        }).ToList());
}

/// <summary>Every warning <see cref="KnowledgeStartup.ApplyVocabularyAsync"/> writes.</summary>
internal static partial class KnowledgeStartupLog
{
    /// <summary>A slot's vocabulary read back exactly one value (K28).</summary>
    /// <param name="logger">The boot logger.</param>
    /// <param name="slot">The slot the vocabulary belongs to.</param>
    /// <param name="value">The one value.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "the vocabulary for slot '{Slot}' has exactly one value: '{Value}'. The gate and "
            + "the linker still work; only the ambiguity half is inert, because one value can never "
            + "produce more than a confirm question.")]
    public static partial void SingleValueVocabulary(ILogger logger, string slot, string value);

    /// <summary>A <c>wildcard.facets</c> member's collection holds no <c>*</c> value (K48).</summary>
    /// <param name="logger">The boot logger.</param>
    /// <param name="facet">The facet key.</param>
    /// <param name="path">The resolved payload path the read used.</param>
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "the wildcard facet '{Facet}' has no '*' value in the collection at '{Path}'. While "
            + "it is unfilled, its own wildcard condition matches nothing, so no search and no probe "
            + "of another facet can return a card. Fill it from a writer: const slot, or take "
            + "'{Facet}' out of wildcard.facets.")]
    public static partial void WildcardFacetMissingStar(ILogger logger, string facet, string path);

    /// <summary>A <c>wildcard.facets</c> member's own K48 read faulted (warned, not refused).</summary>
    /// <param name="logger">The boot logger.</param>
    /// <param name="facet">The facet key.</param>
    /// <param name="path">The resolved payload path the read used.</param>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "the K48 check for wildcard facet '{Facet}' could not run: the read at '{Path}' "
            + "failed. Boot continues; the missing-'*'-value check is skipped for this facet only.")]
    public static partial void WildcardFacetCheckFailed(ILogger logger, string facet, string path, Exception exception);
}

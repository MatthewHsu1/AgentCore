using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Google.Protobuf.Collections;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant
{
    /// <summary>
    /// The startup proof of one collection: it exists, its vector is as wide as this host embeds, and
    /// one point read back carries what the store reads off it. Every failure is a refusal to start
    /// rather than a deployment that silently returns nothing.
    /// </summary>
    internal static class QdrantCollectionProof
    {
        // Embedded once at startup, only to measure the deployment's embedder width against the
        // collection's own. Its content is never a real query and is never sent to Qdrant.
        private const string DimensionProbeText = "agentcore-qdrant-startup-probe";

        /// <summary>Runs every proof against the live collection.</summary>
        /// <exception cref="InvalidOperationException">One proof failed.</exception>
        internal static async ValueTask AssertAsync(
            QdrantClient client,
            KnowledgeProviderConfiguration entry,
            IEmbeddingGenerator<string, Embedding<float>> embedder,
            IKnowledgePointMapper? mapper,
            Guid linkNamespace,
            CancellationToken cancellationToken)
        {
            if (!await client.CollectionExistsAsync(entry.Collection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"providers.knowledge.collection names '{entry.Collection}', and no such collection or "
                    + "alias exists on this cluster. AgentCore reads a knowledge base and never creates "
                    + "one: run whatever ingests your cards, or correct the name.");
            }

            CollectionInfo info = await client.GetCollectionInfoAsync(entry.Collection, cancellationToken).ConfigureAwait(false);
            ulong width = VectorWidth(info.Config.Params.VectorsConfig, entry);
            ulong dimensions = await EmbedderWidthAsync(embedder, entry, cancellationToken).ConfigureAwait(false);

            if (width != dimensions)
            {
                string label = entry.Vector is { Length: > 0 } named ? $"a '{named}' vector" : "an anonymous vector";
                throw new InvalidOperationException(
                    $"'{entry.Collection}' has {label} of {width} dimensions and this "
                    + $"host embeds at {dimensions}. Every score would be meaningless. Either set "
                    + "providers.embeddings to the model this collection was built with, or rebuild the "
                    + "collection with this host's model.");
            }

            await AssertPayloadAsync(client, entry, mapper, linkNamespace, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Reads the width of the vector the document names, or of the one anonymous vector.</summary>
        private static ulong VectorWidth(VectorsConfig vectors, KnowledgeProviderConfiguration entry)
        {
            if (entry.Vector is { Length: > 0 } vectorName)
            {
                return vectors.ConfigCase == VectorsConfig.ConfigOneofCase.ParamsMap
                    && vectors.ParamsMap.Map.TryGetValue(vectorName, out VectorParams? dense)
                    ? dense.Size
                    : throw new InvalidOperationException(
                        $"'{entry.Collection}' carries no named vector '{vectorName}'. A collection whose "
                        + "vector is unnamed, or named something else, misses every point on search with no "
                        + "error — and it still fetches by key, so a smoke test would not notice. Set "
                        + "providers.knowledge.vector to the name the collection was built with, or drop "
                        + "the setting for a collection with a single anonymous vector.");
            }

            return vectors.ConfigCase == VectorsConfig.ConfigOneofCase.Params
                ? vectors.Params.Size
                : throw new InvalidOperationException(
                    $"'{entry.Collection}' carries named vectors and providers.knowledge.vector names "
                    + "none, so every query would search an anonymous vector this collection does not "
                    + "have and miss every point with no error. Set providers.knowledge.vector to the "
                    + "name the collection was built with.");
        }

        /// <summary>Embeds the probe text once, only to measure how wide this host's vectors are.</summary>
        private static async ValueTask<ulong> EmbedderWidthAsync(
            IEmbeddingGenerator<string, Embedding<float>> embedder,
            KnowledgeProviderConfiguration entry,
            CancellationToken cancellationToken)
        {
            try
            {
                Embedding<float> probe = await embedder
                    .GenerateAsync(DimensionProbeText, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return (ulong)probe.Vector.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' could not be checked against this host's embedder: the startup "
                    + "width probe failed before it produced a vector. This is not a schema problem with "
                    + "the collection; check the embedding generator's own configuration and credentials.",
                    ex);
            }
        }

        /// <summary>
        /// Reads one point back and proves the payload still carries what the store reads off it.
        /// </summary>
        private static async ValueTask AssertPayloadAsync(
            QdrantClient client,
            KnowledgeProviderConfiguration entry,
            IKnowledgePointMapper? mapper,
            Guid linkNamespace,
            CancellationToken cancellationToken)
        {
            ScrollResponse scrolled = await client
                .ScrollAsync(entry.Collection, limit: 1, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (scrolled.Result.Count == 0)
            {
                return;
            }

            RetrievedPoint point = scrolled.Result[0];

            if (mapper is not null)
            {
                // A custom mapper owns the payload shape, so no key list can be asserted — but the
                // refuse-to-start property survives: map the one scrolled point and demand text.
                KnowledgeCard? card = mapper.Map(QdrantPointConverter.ToPoint(point.Id, point.Payload, score: null));
                if (card is not { Text.Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"'{entry.Collection}' holds points that mapper '{mapper.Name}' reads as empty: it "
                        + "returned no card, or a card with no text, for a point scrolled at startup. "
                        + "AgentCore would then inject blank cards into every turn without failing anything. "
                        + "Correct the mapper, or point this host at the collection it was written for.");
                }

                return;
            }

            foreach ((string? role, string? key, bool numeric) in DeclaredKeys(entry.Fields!))
            {
                if (!Carries(point.Payload, key, numeric))
                {
                    throw new InvalidOperationException(
                        $"'{entry.Collection}' holds points whose payload has no {(numeric ? "numeric" : "non-empty")} "
                        + $"'{key}', which providers.knowledge.fields.{role} names. AgentCore reads every field "
                        + "that block maps off every point and treats a missing key as absent, so this role "
                        + "would be silently empty on every card. Map the path this collection really uses, "
                        + "declare the role with an explicit null if the collection does not carry it, or "
                        + "point this host at the collection whose payload matches.");
                }
            }

            if (entry.Links is { } links && links.Lookup != KnowledgeLinkLookup.Filter)
            {
                AssertPointKey(point, entry, links, linkNamespace);
            }
        }

        /// <summary>
        /// Every payload key the document actually mapped, with the role that named it and whether that
        /// role holds a number rather than text.
        /// </summary>
        /// <remarks>
        /// All six roles, not the two the built-in mapping cannot do without. A wrong path under
        /// <c>source</c>, <c>locator</c> or <c>authority</c> throws nothing and returns nothing: the
        /// citation is simply blank on every card, on every turn, for the life of the deployment. That is
        /// exactly the failure a startup proof exists to convert into a refusal to start.
        /// </remarks>
        private static IEnumerable<(string Role, string Key, bool Numeric)> DeclaredKeys(
            KnowledgeFieldsConfiguration fields)
        {
            if (fields.Body is { Length: > 0 } body)
            {
                yield return ("body", body, false);
            }

            if (fields.Id is { Length: > 0 } id)
            {
                yield return ("id", id, false);
            }

            if (fields.Lexical is { Length: > 0 } lexical)
            {
                yield return ("lexical", lexical, false);
            }

            if (fields.Source is { Length: > 0 } source)
            {
                yield return ("source", source, false);
            }

            if (fields.Locator is { Length: > 0 } locator)
            {
                yield return ("locator", locator, false);
            }

            // Authority ranks trust, so a collection writes it as an integer. Demanding a string here
            // would refuse every correctly built collection there is.
            if (fields.Authority is { Length: > 0 } authority)
            {
                yield return ("authority", authority, true);
            }
        }

        /// <summary>Whether one point really carries the role a mapped key claims.</summary>
        private static bool Carries(MapField<string, Value> payload, string key, bool numeric)
        {
            return QdrantPayload.Read(payload, key) switch
            {
                { KindCase: Value.KindOneofCase.StringValue } value => !numeric && value.StringValue.Length > 0,
                { KindCase: Value.KindOneofCase.IntegerValue } => numeric,
                { KindCase: Value.KindOneofCase.DoubleValue } => numeric,
                _ => false,
            };
        }

        /// <summary>Proves a linked id resolves back to the point that holds it.</summary>
        private static void AssertPointKey(
            RetrievedPoint point,
            KnowledgeProviderConfiguration entry,
            KnowledgeLinksConfiguration links,
            Guid linkNamespace)
        {
            if (QdrantPayload.Read(point.Payload, links.Field!) is null)
            {
                return;
            }

            string cardId = QdrantPayload.Read(point.Payload, entry.Fields!.Id!)!.StringValue;

            if (point.Id.PointIdOptionsCase != PointId.PointIdOptionsOneofCase.Uuid)
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' holds points keyed by number, and providers.knowledge.links.lookup "
                    + $"is {links.Lookup.ToString().ToLowerInvariant()}, which builds a UUID key. Every "
                    + "link expansion would silently return nothing. Set links.lookup: filter to match on "
                    + $"'{entry.Fields.Id}' instead.");
            }

            Guid expected;
            if (links.Lookup == KnowledgeLinkLookup.Direct)
            {
                if (!Guid.TryParse(cardId, out expected))
                {
                    throw new InvalidOperationException(
                        $"'{entry.Collection}' holds a point whose '{entry.Fields.Id}' is '{cardId}', which is "
                        + "not a GUID, but providers.knowledge.links.lookup is direct. Qdrant's point key is a "
                        + "GUID or an unsigned integer, so a free-form id cannot be one. Set links.lookup: "
                        + $"filter to match on '{entry.Fields.Id}' instead.");
                }
            }
            else
            {
                expected = Uuid5PointId.For(cardId, linkNamespace, links.Prefix);
            }

            if (Guid.Parse(point.Id.Uuid) != expected)
            {
                throw new InvalidOperationException(
                    $"'{entry.Collection}' holds a point whose '{entry.Fields.Id}' is '{cardId}' and whose "
                    + $"point key is {point.Id.Uuid}, but providers.knowledge.links.lookup is "
                    + $"{links.Lookup.ToString().ToLowerInvariant()} with namespace "
                    + $"'{links.Namespace}' and prefix '{links.Prefix}', which derives {expected}. "
                    + $"Every '{links.Field}' expansion would silently return nothing. Correct the "
                    + $"namespace or prefix, or set links.lookup: filter to match on '{entry.Fields.Id}'.");
            }
        }
    }
}

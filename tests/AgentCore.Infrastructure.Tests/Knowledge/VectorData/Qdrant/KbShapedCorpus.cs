using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>One card of the synthetic corpus.</summary>
public sealed record KbShapedCard
{
    /// <summary>Gets the card id.</summary>
    public required string CardId { get; init; }

    /// <summary>Gets the retrieval text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the trust level.</summary>
    public required int Authority { get; init; }

    /// <summary>Gets the product this card applies to.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the card ids this card links to.</summary>
    public required IReadOnlyList<string> SeeAlso { get; init; }

    /// <summary>Gets the products this card also applies to, as a LIST facet.</summary>
    /// <remarks>
    /// A real knowledge bank writes <c>facets.applies_to</c> as an array, and Qdrant matches a keyword
    /// against a list when any element matches. <c>QdrantKnowledgeStore.Holds</c> mirrors that rule for
    /// the see_also re-check, and with a scalar-only corpus its list branch was never executed — so a
    /// deployment scoped on an array facet would have dropped every linked card, silently.
    /// </remarks>
    public required IReadOnlyList<string> AppliesTo { get; init; }

    /// <summary>Gets the embedding.</summary>
    public required float[] Vector { get; init; }
}

/// <summary>
/// A hand-built corpus in the shape one particular ingester writes: 30 cards, 8 dimensions, no
/// OpenAI and no checked-in vector file.
/// </summary>
/// <remarks>
/// <para>
/// Named for the shape, not for the fact that it is synthetic. Every corpus in this folder is
/// synthetic; what matters about this one is that its payload keys, its nested facets and its
/// uuid5 point keys are ONE deployment's conventions. A test that passes here has proved the store
/// works against that shape and nothing more, which is why <see cref="ForeignCorpus"/> and
/// <see cref="HostileCorpus"/> exist beside it.
/// </para>
/// <para>
/// The block a document writes to read it lives here too — <see cref="Fields"/>,
/// <see cref="Scope"/>, <see cref="Links"/>. AgentCore supplies none of it.
/// </para>
/// </remarks>
/// <remarks>
/// Card 6 holds <c>e27</c> and card 7 holds <c>e33</c>, so the look-alike case puts the identifier
/// card one dense rank BELOW its rival. Only the required-identifier prefetch lifts it.
/// </remarks>
public static class KbShapedCorpus
{
    /// <summary>The vector width. Small on purpose: nothing here needs a real embedding model.</summary>
    public const int Dim = 8;

    /// <summary>How many cards the corpus holds.</summary>
    public const int Count = 30;

    /// <summary>A query with no identifier in it.</summary>
    public const string PlainQuery = "how do i clean the deck";

    /// <summary>
    /// A query naming one identifier, e33, whose card (7) sits one dense rank below its rival e27's
    /// card (6) -- so only the required-identifier prefetch lifts it.
    /// </summary>
    public const string LookalikeQuery = "the screen says e33";

    /// <summary>A query naming two identifiers that co-occur in no card. This is what proves `must`.</summary>
    public const string TwoIdentifierQuery = "the screen says e33 e27";

    /// <summary>The value every card carries in its <c>applies_to</c> LIST facet, alongside its own model.</summary>
    public const string SharedAudience = "shared";

    private const int E27 = 6;
    private const int E33 = 7;

    /// <summary>The id of card <paramref name="index"/>.</summary>
    public static string Id(int index) => $"syn-{index:00}";

    /// <summary>The one vector every query embeds to. Card 0 is nearest it, card 29 farthest.</summary>
    public static float[] QueryVector()
    {
        var v = new float[Dim];
        v[0] = 1f;
        return v;
    }

    /// <summary>Builds the corpus.</summary>
    /// <param name="interleaved">
    /// Whether out-of-scope cards are spread through the ranking. When they cluster at the far end
    /// instead, the nearest cards are all in scope anyway and a dropped scope filter changes nothing
    /// any assertion can see. That is a trap a fixture author walks into without noticing, so scope
    /// tests must pass <see langword="true"/>.
    /// </param>
    /// <returns>The cards, in dense-rank order.</returns>
    public static IReadOnlyList<KbShapedCard> Cards(bool interleaved)
    {
        var cards = new List<KbShapedCard>(Count);

        for (var i = 0; i < Count; i++)
        {
            var angle = i * 0.05;
            var vector = new float[Dim];
            vector[0] = (float)Math.Cos(angle);
            vector[1] = (float)Math.Sin(angle);

            var model = interleaved
                ? (i % 3 != 2 ? "ct900" : i % 2 == 0 ? "ctsbs900" : "ct900ent")
                : (i < 20 ? "ct900" : i < 25 ? "ctsbs900" : "ct900ent");

            cards.Add(new KbShapedCard
            {
                CardId = Id(i),
                Text = i switch
                {
                    E27 => "err e27 communication code error on the console",
                    E33 => "err e33 incline motor error on the console",
                    _ => $"card {Id(i)} deck belt console maintenance text",
                },
                Authority = 3 - (i % 3),
                Model = model,
                // The nearest card links to the farthest one, which is therefore never already
                // on the page -- so a see_also assertion cannot pass by accident.
                SeeAlso = i == 0 ? [Id(Count - 1)] : [],

                // Two elements, one of them shared by every card: a scope on SharedAudience holds for
                // every card, and a scope on one model holds for that model's cards alone. Both
                // answers come out of the SAME list branch, so a branch that always said yes and a
                // branch that always said no are each caught by one of the two.
                AppliesTo = [model, SharedAudience],
                Vector = vector,
            });
        }

        return cards;
    }

    /// <summary>
    /// The <c>fields:</c> block a document writes to read THIS corpus.
    /// </summary>
    /// <remarks>
    /// Written out, never inherited. AgentCore ships no field names, so a corpus that wants to be
    /// read has to describe itself, and the description belongs beside the corpus that made it true.
    /// </remarks>
    public static KnowledgeFieldsConfiguration Fields => new()
    {
        Id = "card_id",
        Body = "body",
        Lexical = "text",
        Source = "source.ref",
        Locator = "source.locator",
        Authority = "authority",
    };

    /// <summary>The payload path one facet key becomes here: a nested struct, so a dotted path.</summary>
    public const string ScopeTemplate = "facets.{key}";

    /// <summary>What this corpus puts in front of a card id before hashing it into a point key.</summary>
    /// <remarks>
    /// Written out here and read back by every fixture, because the framework's own prefix default
    /// is the empty string. A corpus that hashes with a prefix has to say which one.
    /// </remarks>
    public const string LinkPrefix = "kb:";

    /// <summary>The <c>links:</c> block a document writes to follow THIS corpus's links.</summary>
    public static KnowledgeLinksConfiguration Links => new()
    {
        Field = "see_also",
        Lookup = KnowledgeLinkLookup.Uuid5,
        Prefix = LinkPrefix,
    };

    /// <summary>The point key of one card here.</summary>
    public static Guid PointKey(string cardId)
        => Uuid5PointId.For(cardId, Uuid5PointId.Namespace(KnowledgeLinksConfiguration.DefaultNamespace), LinkPrefix);

    /// <summary>The <c>scope:</c> block a document writes to scope over THIS corpus.</summary>
    public static KnowledgeScopeConfiguration Scope => new() { Template = ScopeTemplate };

    /// <summary>Creates the collection and fills it, shaped the way a real ingester shapes one.</summary>
    /// <param name="client">The client.</param>
    /// <param name="collection">The collection name. The caller drops it afterwards.</param>
    /// <param name="interleaved">See <see cref="Cards"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <remarks>
    /// The vector is <b>named</b> and the facets are a <b>nested struct</b> indexed at
    /// <c>facets.model</c>, because that is what the knowledge repository writes. A test collection
    /// shaped any other way proves nothing about production.
    /// </remarks>
    public static async Task CreateAsync(
        QdrantClient client, string collection, bool interleaved, CancellationToken cancellationToken)
    {
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: cancellationToken);

        await client.CreatePayloadIndexAsync(
            collection, "text", PayloadSchemaType.Text, cancellationToken: cancellationToken);
        await client.CreatePayloadIndexAsync(
            collection, "card_id", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);
        await client.CreatePayloadIndexAsync(
            collection, "facets.model", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);
        await client.CreatePayloadIndexAsync(
            collection, "facets.applies_to", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);

        var points = Cards(interleaved).Select(card =>
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = PointKey(card.CardId).ToString() },
                Vectors = new Vectors { Vectors_ = new NamedVectors { Vectors = { ["dense"] = card.Vector } } },
            };

            point.Payload["card_id"] = card.CardId;
            point.Payload["text"] = card.Text;
            point.Payload["body"] = card.Text;
            point.Payload["authority"] = card.Authority;
            point.Payload["see_also"] = new Value
            {
                ListValue = new ListValue { Values = { card.SeeAlso.Select(id => new Value { StringValue = id }) } },
            };
            point.Payload["facets"] = new Value
            {
                StructValue = new Struct
                {
                    Fields =
                    {
                        ["model"] = new Value { StringValue = card.Model },

                        // An ARRAY facet, which is what the sibling knowledge-bank design writes for
                        // applies_to. Qdrant matches a keyword against any element of it, and
                        // QdrantKnowledgeStore.Holds has to mirror that for the see_also re-check.
                        ["applies_to"] = new Value
                        {
                            ListValue = new ListValue
                            {
                                Values = { card.AppliesTo.Select(model => new Value { StringValue = model }) },
                            },
                        },
                    },
                },
            };
            point.Payload["source"] = new Value
            {
                StructValue = new Struct
                {
                    Fields =
                    {
                        ["ref"] = new Value { StringValue = $"manifest-{card.CardId}" },
                        ["locator"] = new Value { StringValue = "p.1" },
                    },
                },
            };

            return point;
        }).ToList();

        await client.UpsertAsync(collection, points, cancellationToken: cancellationToken);
    }
}
